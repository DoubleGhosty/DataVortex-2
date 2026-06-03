namespace DataVortex.Core.Models;

/// <summary>
/// A downloaded file handed to the (separate) processing pipeline for archive extraction. Decoupled
/// from <see cref="DownloadJob"/> on purpose: downloads and processing run on independent worker pools.
/// </summary>
public sealed class ProcessingJob : Observable
{
    public Guid Id { get; } = Guid.NewGuid();

    public required long ChannelId { get; init; }
    public required string ChannelTitle { get; init; }
    public required long MessageId { get; init; }
    public required string FileName { get; init; }
    public required string LocalPath { get; init; }
    public required long SizeBytes { get; init; }
    public string? MimeType { get; init; }
    public DateTime ReceivedUtc { get; init; }

    /// <summary>The text/caption of the Telegram message carrying the archive (used to find a password).</summary>
    public string? MessageText { get; init; }

    private ProcessingStatus _status = ProcessingStatus.Queued;
    public ProcessingStatus Status { get => _status; set => SetField(ref _status, value); }

    private ArchiveKind _kind;
    public ArchiveKind Kind { get => _kind; set => SetField(ref _kind, value); }

    private int _extractedCount;
    public int ExtractedCount { get => _extractedCount; set => SetField(ref _extractedCount, value); }

    private string? _error;
    public string? Error { get => _error; set => SetField(ref _error, value); }
}
