namespace DataVortex.Core.Models;

/// <summary>Metadata persisted as JSON for every file the pipeline processes.</summary>
public sealed class FileRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SourceChannelId { get; set; }
    public string SourceChannelTitle { get; set; } = "";
    public long MessageId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? MimeType { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime ProcessedUtc { get; set; }
    public string DownloadPath { get; set; } = "";
    public ArchiveKind Kind { get; set; }
    public ProcessingStatus Status { get; set; }
    public List<string> ExtractedTextFiles { get; set; } = new();
    public List<CredentialEntry> Credentials { get; set; } = new();
    public string? Error { get; set; }
}
