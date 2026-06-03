using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
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
        IDownloadDeduplicator dedup, IAccountTestRegistry accounts,
        DataVortex.Core.Passculture.PasscultureClient? passClient)
    {
        _telegram = telegram;
        _storage = storage;
        _settings = settings;
        _metrics = metrics;
        _dedup = dedup;
        _accounts = accounts;
        _log = loggerFactory.CreateLogger<PipelineCoordinator>();
        _passClient = passClient;

        var s = settings.Current;
        _bandwidth = new BandwidthLimiter(s.BandwidthLimitBytesPerSecond);

        _download = new DownloadPipeline(
            telegram, storage, metrics, _bandwidth, _pauseGate,
            loggerFactory.CreateLogger<DownloadPipeline>(),
            s.MaxParallelDownloads, s.DownloadQueueCapacity, s.MaxDownloadRetries, s.RetryBaseDelayMs);

        _processing = new ProcessingPipeline(
            extractor, storage, metrics, _pauseGate,
            loggerFactory.CreateLogger<ProcessingPipeline>(),
            s.MaxParallelProcessing, s.ProcessingQueueCapacity, passClient, _accounts);

        _download.JobChanged += OnDownloadJobChanged;
        _processing.JobChanged += j => ProcessingJobChanged?.Invoke(j);
        _processing.FileArchived += r => FileArchived?.Invoke(r);
        _download.OnDownloaded = EnqueueProcessing;
    }

    private void OnDownloadJobChanged(DownloadJob job)
    {
        // Persist the dedup key only once the download succeeds; release it on failure so it can be retried.
        if (job.Status == DownloadStatus.Completed)
            _dedup.Commit(job.DocumentId, job.SizeBytes, job.FileName);
        else if (job.Status is DownloadStatus.Failed or DownloadStatus.Canceled)
            _dedup.RemoveReservation(job.DocumentId, job.SizeBytes, job.FileName);

        DownloadJobChanged?.Invoke(job);
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _bandwidth.BytesPerSecond = _settings.Current.BandwidthLimitBytesPerSecond;
        _metrics.Start();
        _download.Start(_cts.Token);
        _processing.Start(_cts.Token);
        _telegram.FileDetected += OnFileDetected;
        IsRunning = true;
        _log.LogInformation("Pipeline coordinator started");

        // Background catch-up: test credentials in existing metadata that were never tested. Routed through
        // the registry so an account is never sent to the backend twice (no wasted captchas).
        if (_passClient is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var r in _storage.LoadRecords())
                    {
                        if (r.Credentials is null || r.Credentials.Count == 0) continue;
                        var changed = false;
                        for (int i = 0; i < r.Credentials.Count; i++)
                        {
                            var before = r.Credentials[i];
                            var after = await AccountTester.TestOnceAsync(_passClient, _accounts, before).ConfigureAwait(false);
                            if (!ReferenceEquals(before, after)) { r.Credentials[i] = after; changed = true; }
                        }
                        if (changed)
                        {
                            try { await _storage.SaveRecordAsync(r).ConfigureAwait(false); }
                            catch (Exception ex) { _log.LogWarning(ex, "Failed to save updated metadata for {File}", r.OriginalFileName); }
                        }
                    }
                }
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

    private async void OnFileDetected(DownloadJob job)
    {
        try
        {
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
