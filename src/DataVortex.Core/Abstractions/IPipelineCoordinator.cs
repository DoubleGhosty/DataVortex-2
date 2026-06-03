using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

/// <summary>
/// Owns the two decoupled worker pools (download + processing) and the channels between them.
/// Subscribes to <see cref="ITelegramService.FileDetected"/> and drives files all the way to archived.
/// </summary>
public interface IPipelineCoordinator
{
    bool IsRunning { get; }
    bool IsPaused { get; }

    event Action<DownloadJob>? DownloadJobChanged;
    event Action<ProcessingJob>? ProcessingJobChanged;
    event Action<FileRecord>? FileArchived;

    void Start();
    Task StopAsync();
    void Pause();
    void Resume();

    void UpdateBandwidthLimit(long bytesPerSecond);

    int DownloadQueueDepth { get; }
    int ProcessingQueueDepth { get; }
    int ActiveDownloads { get; }
}
