using System.Threading.Channels;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

/// <summary>
/// Stage A of the pipeline: a bounded <see cref="Channel{T}"/> feeding a fixed pool of download workers.
/// Parallelism equals the worker count. On success the job is handed to <see cref="OnDownloaded"/>
/// (wired by the coordinator to the *separate* processing pipeline) so downloading and processing never
/// block one another. Transient failures are retried with exponential backoff.
/// </summary>
public sealed class DownloadPipeline
{
    private readonly ITelegramService _telegram;
    private readonly IStorageService _storage;
    private readonly IMetricsService _metrics;
    private readonly BandwidthLimiter _bandwidth;
    private readonly PauseGate _pauseGate;
    private readonly ILogger<DownloadPipeline> _log;

    private readonly Channel<DownloadJob> _channel;
    private readonly int _workerCount;
    private readonly int _maxRetries;
    private readonly int _retryBaseDelayMs;

    private Task[] _workers = Array.Empty<Task>();
    private int _activeDownloads;
    private int _queueDepth;
    private CancellationToken _ct;

    public event Action<DownloadJob>? JobChanged;

    /// <summary>Invoked once a file is fully downloaded; the coordinator routes it to processing.</summary>
    public Func<DownloadJob, ValueTask>? OnDownloaded { get; set; }

    public int QueueDepth => Volatile.Read(ref _queueDepth);
    public int ActiveDownloads => Volatile.Read(ref _activeDownloads);

    public DownloadPipeline(
        ITelegramService telegram, IStorageService storage, IMetricsService metrics,
        BandwidthLimiter bandwidth, PauseGate pauseGate, ILogger<DownloadPipeline> log,
        int workerCount, int queueCapacity, int maxRetries, int retryBaseDelayMs)
    {
        _telegram = telegram;
        _storage = storage;
        _metrics = metrics;
        _bandwidth = bandwidth;
        _pauseGate = pauseGate;
        _log = log;
        _workerCount = Math.Max(1, workerCount);
        _maxRetries = Math.Max(0, maxRetries);
        _retryBaseDelayMs = Math.Max(250, retryBaseDelayMs);
        _channel = Channel.CreateBounded<DownloadJob>(new BoundedChannelOptions(Math.Max(16, queueCapacity))
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Start(CancellationToken ct)
    {
        _ct = ct;
        _workers = Enumerable.Range(0, _workerCount)
            .Select(i => Task.Run(() => WorkerLoop(i, ct), ct))
            .ToArray();
        _log.LogInformation("Download pipeline started with {Workers} workers", _workerCount);
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch { /* cancellation during shutdown */ }
    }

    public async ValueTask EnqueueAsync(DownloadJob job, CancellationToken ct)
    {
        Interlocked.Increment(ref _queueDepth);
        _metrics.SetDownloadQueueDepth(QueueDepth);
        job.Status = DownloadStatus.Queued;
        JobChanged?.Invoke(job);
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
    }

    private async Task WorkerLoop(int id, CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                _metrics.SetDownloadQueueDepth(QueueDepth);
                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                await ProcessJob(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Download worker {Id} crashed", id); }
    }

    private async Task ProcessJob(DownloadJob job, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeDownloads);
        _metrics.SetActiveDownloads(ActiveDownloads);
        try
        {
            job.Status = DownloadStatus.Downloading;
            job.Error = null;
            JobChanged?.Invoke(job);

            var dir = Path.Combine(_storage.Paths.Downloads, $"{Sanitize(job.ChannelTitle)}_{job.ChannelId}");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{job.MessageId}_{Sanitize(job.FileName)}");

            await WaitForDiskSpaceAsync(target, job.SizeBytes, ct).ConfigureAwait(false);

            var progress = new Progress<long>(b => job.BytesDownloaded = b);

            await using (var file = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true))
            await using (var throttled = new ThrottledStream(file, _bandwidth, progress, _metrics.ReportDownloadedBytes))
            {
                await _telegram.DownloadAsync(job, throttled, progress, ct).ConfigureAwait(false);
            }

            // Integrity: the bytes on disk must match the size Telegram advertised (catches truncation).
            var actualSize = new FileInfo(target).Length;
            if (job.SizeBytes > 0 && actualSize != job.SizeBytes)
                throw new IOException($"Size mismatch for {job.FileName}: expected {job.SizeBytes}, got {actualSize}");

            job.LocalPath = target;
            job.Status = DownloadStatus.Completed;
            JobChanged?.Invoke(job);
            _metrics.ReportDownloadCompleted();
            _log.LogInformation("Downloaded {File} ({Size} bytes) from {Channel}", job.FileName, job.SizeBytes, job.ChannelTitle);

            if (OnDownloaded is not null)
                await OnDownloaded(job).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadStatus.Canceled;
            JobChanged?.Invoke(job);
        }
        catch (Exception ex)
        {
            job.Attempts++;
            job.Error = ex.Message;
            _log.LogWarning(ex, "Download failed for {File} (attempt {Attempt}/{Max})", job.FileName, job.Attempts, _maxRetries);
            if (job.Attempts <= _maxRetries && !ct.IsCancellationRequested)
            {
                job.Status = DownloadStatus.Retrying;
                JobChanged?.Invoke(job);
                _ = RetryLater(job);
            }
            else
            {
                job.Status = DownloadStatus.Failed;
                JobChanged?.Invoke(job);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
            _metrics.SetActiveDownloads(ActiveDownloads);
        }
    }

    private async Task RetryLater(DownloadJob job)
    {
        try
        {
            var delay = _retryBaseDelayMs * (int)Math.Pow(2, Math.Min(6, job.Attempts - 1));
            await Task.Delay(delay, _ct).ConfigureAwait(false);
            await EnqueueAsync(job, _ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Blocks (without spinning the CPU) until there is room on the target drive, so a full disk
    /// pauses downloads instead of failing them.</summary>
    private async Task WaitForDiskSpaceAsync(string target, long needed, CancellationToken ct)
    {
        const long margin = 512L * 1024 * 1024; // keep 512 MB of headroom
        var root = Path.GetPathRoot(Path.GetFullPath(target));
        if (string.IsNullOrEmpty(root)) return;
        DriveInfo drive;
        try { drive = new DriveInfo(root); }
        catch { return; }

        bool warned = false;
        while (!ct.IsCancellationRequested && drive.IsReady && drive.AvailableFreeSpace < needed + margin)
        {
            if (!warned)
            {
                _log.LogWarning("Low disk space: waiting before downloading {File} (need {Need} bytes, {Free} free)",
                    Path.GetFileName(target), needed, drive.AvailableFreeSpace);
                warned = true;
            }
            await Task.Delay(5000, ct).ConfigureAwait(false);
        }
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "unknown" : clean;
    }
}
