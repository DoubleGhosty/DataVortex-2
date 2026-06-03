using DataVortex.Core.Abstractions;
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
        IDownloadDeduplicator dedup, DataVortex.Core.Passculture.PasscultureClient? passClient)
    {
        _telegram = telegram;
        _storage = storage;
        _settings = settings;
        _metrics = metrics;
        _dedup = dedup;
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
            s.MaxParallelProcessing, s.ProcessingQueueCapacity, passClient);

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

        // Kick off background scan to test any credentials found in persisted metadata that weren't auto-tested
        if (_processing is not null && _passClient is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var records = _storage.LoadRecords();
                    foreach (var r in records)
                    {
                        if (r.Credentials is null) continue;
                        var changed = false;
                        for (int i = 0; i < r.Credentials.Count; i++)
                        {
                            var c = r.Credentials[i];
                            if (c.Tested) continue;
                            try
                            {
                                var signin = await _passClient.SignInAsync(c.Username ?? string.Empty, c.Password ?? string.Empty, null);
                                _log.LogInformation("Passculture signin result for {User}: success={Success}", c.Username, signin.Success);
                                string? access = signin.AccessToken;
                                string? refresh = signin.RefreshToken;
                                decimal? credit = null;
                                string? birth = null;
                                if (signin.Success && access is not null)
                                {
                                    try
                                    {
                                        var me = await _passClient.GetMeAsync(access);
                                        credit = me.DomainsCreditRemaining;
                                        birth = me.BirthDate;
                                        _log.LogInformation("Passculture /me for {User}: credit={Credit}, birth={Birth}", c.Username, credit, birth);
                                    }
                                    catch (Exception ex) { _log.LogWarning(ex, "Failed to get /me for {User}", c.Username); }
                                }
                                var updated = c with { Tested = true, TestSuccess = signin.Success, TestMessage = signin.Raw, TestedUtc = DateTime.UtcNow, AccessToken = access, RefreshToken = refresh, Credit = credit, BirthDate = birth };
                                r.Credentials[i] = updated;
                                changed = true;
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Error testing credential {User} from metadata", c.Username);
                            }
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
