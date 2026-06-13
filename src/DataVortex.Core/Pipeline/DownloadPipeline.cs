using System.Collections.Concurrent;
using System.Threading.Channels;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

/// <summary>
/// Stage A of the pipeline: a bounded <see cref="Channel{T}"/> feeding a fixed pool of download workers.
/// Supports per-job cancel/retry and an adaptive concurrency cap that automatically backs off when downloads
/// start failing in a burst (e.g. throttling) and recovers after a calm period. On success the job is handed
/// to <see cref="OnDownloaded"/>; transient failures are retried with exponential backoff.
/// </summary>
public sealed class DownloadPipeline
{
    private const int FailureBurstThreshold = 5;
    private const int ThrottleCooldownMs = 60_000;

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

    // Per-job cancellation + adaptive concurrency
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelRequested = new();
    // Hard concurrency gate: a worker must take a permit before downloading; the cap is shrunk by *parking*
    // permits (holding them) and grown by handing them back. _capAdjustLock serialises those adjustments.
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _capAdjustLock = new(1, 1);
    private readonly object _throttleLock = new();
    private int _heldPermits;        // permits currently parked to keep concurrency below the worker count
    private int _maxConcurrent;      // logical cap, for logging/inspection
    private int _consecutiveFailures;
    private Timer? _restoreTimer;

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
        _maxConcurrent = _workerCount;
        _slots = new SemaphoreSlim(_workerCount, _workerCount);
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
        lock (_throttleLock) { _restoreTimer?.Dispose(); _restoreTimer = null; }
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

    /// <summary>Cancels a job whether it is queued (skipped on dequeue) or actively downloading (aborted).</summary>
    public void Cancel(DownloadJob job)
    {
        _cancelRequested[job.Id] = 0;
        if (_active.TryGetValue(job.Id, out var cts))
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>Re-queues a failed/cancelled job from the start.</summary>
    public void Retry(DownloadJob job)
    {
        _cancelRequested.TryRemove(job.Id, out _);
        job.Attempts = 0;
        job.Error = null;
        _ = EnqueueAsync(job, _ct);
    }

    /// <summary>Sets the live cap on concurrent downloads (1..workerCount). Fire-and-forget: never blocks
    /// the caller (UI thread) while in-flight downloads drain.</summary>
    public void SetMaxConcurrent(int count) => _ = AdjustConcurrencyAsync(count);

    /// <summary>Resizes the concurrency gate by parking or releasing permits. Shrinking waits asynchronously
    /// for active downloads to free permits, so it never blocks a worker or the UI thread. All adjustments are
    /// serialised, so <see cref="_heldPermits"/> is only mutated here.</summary>
    private async Task AdjustConcurrencyAsync(int newMax)
    {
        newMax = Math.Clamp(newMax, 1, _workerCount);
        await _capAdjustLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _maxConcurrent, newMax);
            int desiredHeld = _workerCount - newMax;
            while (_heldPermits > desiredHeld) { _slots.Release(); _heldPermits--; }            // grow
            while (_heldPermits < desiredHeld) { await _slots.WaitAsync().ConfigureAwait(false); _heldPermits++; } // shrink
        }
        finally { _capAdjustLock.Release(); }
    }

    private async Task WorkerLoop(int id, CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                _metrics.SetDownloadQueueDepth(QueueDepth);

                // Fast path: a job cancelled while still queued never starts (and never takes a permit).
                if (_cancelRequested.TryRemove(job.Id, out _))
                {
                    job.Status = DownloadStatus.CanceledByUser;
                    JobChanged?.Invoke(job);
                    continue;
                }

                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);

                // Hard concurrency gate: acquire a permit, always release it once the job is done.
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                try { await ProcessJob(job, ct).ConfigureAwait(false); }
                finally { _slots.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Download worker {Id} crashed", id); }
    }

    private async Task ProcessJob(DownloadJob job, CancellationToken ct)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[job.Id] = jobCts;
        // A cancel requested while the job was queued/waiting for a permit must take effect now.
        if (_cancelRequested.ContainsKey(job.Id)) jobCts.Cancel();
        var token = jobCts.Token;

        Interlocked.Increment(ref _activeDownloads);
        _metrics.SetActiveDownloads(ActiveDownloads);
        try
        {
            token.ThrowIfCancellationRequested();
            job.Status = DownloadStatus.Downloading;
            job.Error = null;
            JobChanged?.Invoke(job);

            var dir = Path.Combine(_storage.Paths.Downloads, $"{Sanitize(job.ChannelTitle)}_{job.ChannelId}");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{job.MessageId}_{Sanitize(job.FileName)}");

            await WaitForDiskSpaceAsync(target, job.SizeBytes, token).ConfigureAwait(false);

            var progress = new Progress<long>(b => job.BytesDownloaded = b);

            await using (var file = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true))
            await using (var throttled = new ThrottledStream(file, _bandwidth, progress, _metrics.ReportDownloadedBytes))
            {
                await _telegram.DownloadAsync(job, throttled, progress, token).ConfigureAwait(false);
            }

            var actualSize = new FileInfo(target).Length;
            if (job.SizeBytes > 0 && actualSize != job.SizeBytes)
                throw new IOException($"Size mismatch for {job.FileName}: expected {job.SizeBytes}, got {actualSize}");

            job.LocalPath = target;
            job.Status = DownloadStatus.Completed;
            JobChanged?.Invoke(job);
            _metrics.ReportDownloadCompleted();
            Interlocked.Exchange(ref _consecutiveFailures, 0); // healthy download resets the burst counter
            _log.LogInformation("Downloaded {File} ({Size} bytes) from {Channel}", job.FileName, job.SizeBytes, job.ChannelTitle);

            if (OnDownloaded is not null)
                await OnDownloaded(job).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A user cancel left a flag on this job; its absence means we were cancelled by app shutdown.
            job.Status = _cancelRequested.TryRemove(job.Id, out _)
                ? DownloadStatus.CanceledByUser
                : DownloadStatus.Canceled;
            JobChanged?.Invoke(job);
        }
        catch (Exception ex)
        {
            job.Attempts++;
            job.Error = ex.Message;
            _log.LogWarning(ex, "Download failed for {File} (attempt {Attempt}/{Max})", job.FileName, job.Attempts, _maxRetries);
            if (Interlocked.Increment(ref _consecutiveFailures) >= FailureBurstThreshold)
                Throttle();

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
            _active.TryRemove(job.Id, out _);
            Interlocked.Decrement(ref _activeDownloads);
            _metrics.SetActiveDownloads(ActiveDownloads);
        }
    }

    /// <summary>Halves the concurrency cap after a failure burst, then schedules a restore after a calm period.</summary>
    private void Throttle()
    {
        var current = Volatile.Read(ref _maxConcurrent);
        var next = Math.Max(1, current / 2);
        if (next < current)
        {
            _log.LogWarning("Repeated download failures — throttling concurrency to {N}", next);
            _ = AdjustConcurrencyAsync(next);
        }
        lock (_throttleLock)
        {
            _restoreTimer?.Dispose();
            _restoreTimer = new Timer(_ =>
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                _ = AdjustConcurrencyAsync(_workerCount);
                _log.LogInformation("Download concurrency restored to {N}", _workerCount);
            }, null, ThrottleCooldownMs, Timeout.Infinite);
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
