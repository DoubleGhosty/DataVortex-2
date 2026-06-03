using TL;

namespace DataVortex.Core.Models;

/// <summary>
/// A single file to download from Telegram. Carries the raw <see cref="TL.Document"/> needed by
/// WTelegramClient plus UI-friendly metadata. Implements <see cref="Observable"/> so the dashboard can
/// bind to a live instance while a pipeline worker mutates <see cref="Status"/>/<see cref="BytesDownloaded"/>.
/// </summary>
public sealed class DownloadJob : Observable
{
    public Guid Id { get; } = Guid.NewGuid();

    public required long ChannelId { get; init; }
    public required string ChannelTitle { get; init; }
    public required long MessageId { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public string? MimeType { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The text/caption of the Telegram message carrying the archive (used to find a password).</summary>
    public string? MessageText { get; init; }

    /// <summary>Telegram document id — used for de-duplication (stable across forwards/reposts).</summary>
    public long DocumentId { get; init; }

    /// <summary>The Telegram document reference used to perform the actual download (Core-internal).</summary>
    internal Document Document { get; init; } = null!;

    private DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status { get => _status; set => SetField(ref _status, value); }

    private long _bytesDownloaded;
    public long BytesDownloaded
    {
        get => _bytesDownloaded;
        set { if (SetField(ref _bytesDownloaded, value)) OnPropertyChanged(nameof(Progress)); }
    }

    /// <summary>0..1 download progress, used by the UI progress bar.</summary>
    public double Progress => SizeBytes > 0 ? Math.Clamp((double)BytesDownloaded / SizeBytes, 0, 1) : 0;

    private int _attempts;
    public int Attempts { get => _attempts; set => SetField(ref _attempts, value); }

    private string? _error;
    public string? Error { get => _error; set => SetField(ref _error, value); }

    /// <summary>Absolute path of the downloaded file once <see cref="Status"/> is Completed.</summary>
    public string? LocalPath { get; set; }
}
