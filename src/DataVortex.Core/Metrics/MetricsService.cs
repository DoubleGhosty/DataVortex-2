using System.Diagnostics;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;

namespace DataVortex.Core.Metrics;

/// <summary>
/// Lock-free throughput counters sampled once per second by a background timer. The two live rates are
/// download <b>bytes/second</b> and <b>extracted files/second</b>, computed from sliding windows that are
/// atomically reset on each tick — so the graphs move during long downloads/extractions.
/// </summary>
public sealed class MetricsService : IMetricsService, IDisposable
{
    private long _totalDownloaded;
    private long _totalProcessed;
    private long _totalBytes;
    private long _totalExtracted;
    private long _windowBytes;
    private long _windowExtracted;
    private int _activeDownloads;
    private int _downloadQueueDepth;
    private int _processingQueueDepth;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastTickMs;
    private Timer? _timer;

    public event Action<MetricsSnapshot>? SnapshotProduced;
    public MetricsSnapshot Current { get; private set; }

    public void ReportDownloadedBytes(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _windowBytes, bytes);
        Interlocked.Add(ref _totalBytes, bytes);
    }

    public void ReportDownloadCompleted() => Interlocked.Increment(ref _totalDownloaded);
    public void ReportProcessingCompleted() => Interlocked.Increment(ref _totalProcessed);

    public void ReportExtractedFile()
    {
        Interlocked.Increment(ref _windowExtracted);
        Interlocked.Increment(ref _totalExtracted);
    }

    public void SetActiveDownloads(int count) => Volatile.Write(ref _activeDownloads, count);
    public void SetDownloadQueueDepth(int count) => Volatile.Write(ref _downloadQueueDepth, count);
    public void SetProcessingQueueDepth(int count) => Volatile.Write(ref _processingQueueDepth, count);

    public void Start()
    {
        _lastTickMs = _sw.ElapsedMilliseconds;
        _timer ??= new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick()
    {
        try { TickCore(); }
        catch { /* a timer callback that throws would terminate the process */ }
    }

    private void TickCore()
    {
        var now = _sw.ElapsedMilliseconds;
        var dt = Math.Max(1, now - _lastTickMs) / 1000.0;
        _lastTickMs = now;

        var bytes = Interlocked.Exchange(ref _windowBytes, 0);
        var extracted = Interlocked.Exchange(ref _windowExtracted, 0);

        var snapshot = new MetricsSnapshot(
            DateTime.UtcNow,
            bytes / dt,
            extracted / dt,
            Volatile.Read(ref _activeDownloads),
            Volatile.Read(ref _downloadQueueDepth),
            Volatile.Read(ref _processingQueueDepth),
            Interlocked.Read(ref _totalDownloaded),
            Interlocked.Read(ref _totalProcessed),
            Interlocked.Read(ref _totalBytes),
            Interlocked.Read(ref _totalExtracted));

        Current = snapshot;
        SnapshotProduced?.Invoke(snapshot);
    }

    public void Dispose() => Stop();
}
