using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

/// <summary>Collects throughput counters and emits a <see cref="MetricsSnapshot"/> once per second.</summary>
public interface IMetricsService
{
    event Action<MetricsSnapshot>? SnapshotProduced;
    MetricsSnapshot Current { get; }

    /// <summary>Report bytes as they are written during a download (drives the live MB/s graph).</summary>
    void ReportDownloadedBytes(long bytes);
    /// <summary>Report that one file finished downloading (total counter).</summary>
    void ReportDownloadCompleted();
    /// <summary>Report that one archive finished processing (total counter).</summary>
    void ReportProcessingCompleted();
    /// <summary>Report that one *.txt was extracted (drives the live extracted-files/sec graph).</summary>
    void ReportExtractedFile();

    // Live gauges updated by the pipelines.
    void SetActiveDownloads(int count);
    void SetDownloadQueueDepth(int count);
    void SetProcessingQueueDepth(int count);

    void Start();
    void Stop();
}
