using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Configuration;
using DataVortex.Core.Licensing;
using DataVortex.Core.Models;
using DataVortex.Licensing;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

/// <summary>
/// Owns the two decoupled worker pools and the bandwidth/pause primitives they share. Subscribes to
/// <see cref="ITelegramService.FileDetected"/> and routes each file: download -> processing -> archived.
/// </summary>
public sealed class PipelineCoordinator : IPipelineCoordinator, IDisposable
{
    private readonly ITelegramService _telegram;
    private readonly IStorageService _storage;
    private readonly ISettingsService _settings;
    private readonly IMetricsService _metrics;
    private readonly ILogger<PipelineCoordinator> _log;
    private readonly BandwidthLimiter _bandwidth;
    private readonly PauseGate _pauseGate = new();
    private readonly DownloadPipeline _download;
    private readonly ProcessingPipeline _processing;
    private readonly IDownloadDeduplicator _dedup;
    private readonly DataVortex.Core.Passculture.PasscultureClient? _passClient;
    private readonly IAccountTestRegistry _accounts;
    private readonly IPendingDownloadStore _pending;
    private readonly ILicenseGate _gate;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public bool IsPaused => _pauseGate.IsPaused;

    public event Action<DownloadJob>? DownloadJobChanged;
    public event Action<ProcessingJob>? ProcessingJobChanged;
    public event Action<FileRecord>? FileArchived;

    public int DownloadQueueDepth => _download.QueueDepth;
    public int ProcessingQueueDepth => _processing.QueueDepth;
    public int ActiveDownloads => _download.ActiveDownloads;

    public PipelineCoordinator(
        ITelegramService telegram, IStorageService storage, IMetricsService metrics,
        IArchiveExtractor extractor, ISettingsService settings, ILoggerFactory loggerFactory,
        IDownloadDeduplicator dedup, IAccountTestRegistry accounts, IPendingDownloadStore pending,
        ILicenseGate gate, DataVortex.Core.Passculture.PasscultureClient? passClient)
    {
        _telegram = telegram;
        _storage = storage;
        _settings = settings;
        _metrics = metrics;
        _dedup = dedup;
        _accounts = accounts;
        _pending = pending;
        _gate = gate;
        _log = loggerFactory.CreateLogger<PipelineCoordinator>();
        _passClient = passClient;

        var s = settings.Current;
        _bandwidth = new BandwidthLimiter(s.BandwidthLimitBytesPerSecond);

        _download = new DownloadPipeline(
            telegram, storage, metrics, _bandwidth, _pauseGate,
            loggerFactory.CreateLogger<DownloadPipeline>(),
            s.MaxParallelDownloads, s.DownloadQueueCapacity, s.MaxDownloadRetries, s.RetryBaseDelayMs);

        _processing = new ProcessingPipeline(
            extractor, storage, metrics, settings, _pauseGate,
            loggerFactory.CreateLogger<ProcessingPipeline>(),
            s.MaxParallelProcessing, s.ProcessingQueueCapacity, passClient, _accounts);

        _download.JobChanged += OnDownloadJobChanged;
        _processing.JobChanged += j => ProcessingJobChanged?.Invoke(j);
        _processing.FileArchived += r => FileArchived?.Invoke(r);
        _download.OnDownloaded = EnqueueProcessing;
    }

    private void OnDownloadJobChanged(DownloadJob job)
    {
        // Persist the dedup key only once the download succeeds; release it otherwise so it can be retried.
        if (job.Status == DownloadStatus.Completed)
            _dedup.Commit(job.DocumentId, job.SizeBytes, job.FileName);
        else if (job.Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.CanceledByUser)
            _dedup.RemoveReservation(job.DocumentId, job.SizeBytes, job.FileName);

        // Forget it from the resume store once it is done, permanently failed, or cancelled by the user.
        // Plain Canceled = shutdown of an in-flight job → keep it so it resumes next launch.
        if (job.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.CanceledByUser)
            _pending.Remove(job.ChannelId, job.MessageId, job.DocumentId);

        DownloadJobChanged?.Invoke(job);
    }

    public void Start()
    {
        if (IsRunning) return;

        // Licence gate at the real execution site (not a shared boolean): no entitlement ⇒ the pipeline never
        // spins up. In a Release build the gate is only ever fed by the licence guard, so a bypassed startup check
        // lands here with Entitlements.None and the pipeline simply refuses.
        if (!_gate.Allows(Capability.RunPipeline))
        {
            _log.LogWarning("Pipeline not started — this build is not licensed to run it.");
            return;
        }

        _cts = new CancellationTokenSource();
        _bandwidth.BytesPerSecond = _settings.Current.BandwidthLimitBytesPerSecond;
        _metrics.Start();
        _download.Start(_cts.Token);
        _processing.Start(_cts.Token);
        _telegram.FileDetected += OnFileDetected;
        IsRunning = true;
        _log.LogInformation("Pipeline coordinator started");

        ResumePendingDownloads(_cts.Token);

        // Background catch-up: test credentials in existing metadata that were never tested. Routed through
        // the registry so an account is never sent to the backend twice (no wasted captchas). Records are scanned
        // in PARALLEL (the per-account gate in AccountTester enforces the real concurrency cap from settings), so a
        // backlog of never-tested accounts is no longer retried one-by-one on launch.
        if (_passClient is not null)
        {
            var client = _passClient;       // local keeps the non-null flow into the lambdas
            var scanCt = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var records = _storage.LoadRecords().Where(r => r.Credentials is { Count: > 0 }).ToList();
                    await Parallel.ForEachAsync(records,
                        new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = scanCt },
                        async (r, token) =>
                        {
                            var creds = r.Credentials!;
                            var changed = false;
                            for (int i = 0; i < creds.Count; i++)
                            {
                                var before = creds[i];
                                var after = await AccountTester.TestOnceAsync(client, _accounts, before, token).ConfigureAwait(false);
                                if (!ReferenceEquals(before, after)) { creds[i] = after; changed = true; }
                            }
                            if (changed)
                            {
                                try { await _storage.SaveRecordAsync(r).ConfigureAwait(false); }
                                catch (Exception ex) { _log.LogWarning(ex, "Failed to save updated metadata for {File}", r.OriginalFileName); }
                            }
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* pipeline stopped */ }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Background credentials scan failed");
                }
            });
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _telegram.FileDetected -= OnFileDetected;
        _cts?.Cancel();
        await _download.StopAsync().ConfigureAwait(false);
        await _processing.StopAsync().ConfigureAwait(false);
        _metrics.Stop();
        IsRunning = false;
        _log.LogInformation("Pipeline coordinator stopped");
    }

    public void Pause()
    {
        _pauseGate.Pause();
        _log.LogInformation("Pipeline paused");
    }

    public void Resume()
    {
        _pauseGate.Resume();
        _log.LogInformation("Pipeline resumed");
    }

    public void UpdateBandwidthLimit(long bytesPerSecond) => _bandwidth.BytesPerSecond = bytesPerSecond;

    public void CancelDownload(DownloadJob job) => _download.Cancel(job);
    public void RetryDownload(DownloadJob job) => _download.Retry(job);
    public void SetMaxConcurrentDownloads(int count) => _download.SetMaxConcurrent(count);

    /// <summary>Re-queues archives detected but not finished before the last shutdown. Documents are re-fetched
    /// (file_reference may have expired); a job whose message is gone is kept in the store and skipped. Dialogs
    /// are loaded first so channels resolve.</summary>
    private void ResumePendingDownloads(CancellationToken ct)
    {
        var pendings = _pending.Snapshot();
        if (pendings.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try { await _telegram.EnsureDialogsLoadedAsync(ct).ConfigureAwait(false); } catch { }
            _log.LogInformation("Resuming {Count} pending download(s) from the previous session", pendings.Count);
            foreach (var p in pendings)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var job = await _telegram.RebuildPendingAsync(p, ct).ConfigureAwait(false);
                    if (job is null)
                    {
                        _log.LogWarning("Pending download {File} could not be re-fetched (kept for next try)", p.FileName);
                        continue;
                    }
                    await _download.EnqueueAsync(job, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to resume pending download {File}", p.FileName); }
            }
        }, ct);
    }

    private async void OnFileDetected(DownloadJob job)
    {
        // Dispersed second gate on the hot path — a licence lapse mid-run quietly stops accepting new work. The
        // non-uniform effect (silent drop here vs. a logged refusal in Start) means there is no single pattern to
        // pattern-match and patch out.
        if (!_gate.Allows(Capability.RunPipeline)) return;
        try
        {
            _pending.Add(job); // remember it so the download queue survives a restart
            await _download.EnqueueAsync(job, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enqueue download for {File}", job.FileName);
        }
    }

    private async ValueTask EnqueueProcessing(DownloadJob job)
    {
        var pj = new ProcessingJob
        {
            ChannelId = job.ChannelId,
            ChannelTitle = job.ChannelTitle,
            MessageId = job.MessageId,
            FileName = job.FileName,
            LocalPath = job.LocalPath!,
            SizeBytes = job.SizeBytes,
            MimeType = job.MimeType,
            ReceivedUtc = job.ReceivedUtc,
            MessageText = job.MessageText
        };
        await _processing.EnqueueAsync(pj, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
