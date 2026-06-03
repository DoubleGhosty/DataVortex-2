namespace DataVortex.Core.Models;

/// <summary>An immutable, once-per-second sample of pipeline throughput and depth, pushed to the UI.
/// Download is measured as <b>bytes/second</b> and processing as <b>extracted files/second</b> so the
/// graphs move continuously during large archives (rather than only spiking when a file completes).</summary>
public readonly record struct MetricsSnapshot(
    DateTime TimestampUtc,
    double DownloadBytesPerSecond,
    double ExtractedFilesPerSecond,
    int ActiveDownloads,
    int DownloadQueueDepth,
    int ProcessingQueueDepth,
    long TotalFilesDownloaded,
    long TotalFilesProcessed,
    long TotalBytesDownloaded,
    long TotalExtractedFiles);
