using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

public sealed record ExtractionResult(
    ArchiveKind Kind,
    IReadOnlyList<string> ExtractedFiles,
    IReadOnlyList<string> Errors,
    bool IsEncrypted = false,
    bool PasswordFound = false)
{
    public bool Success => Errors.Count == 0;
}

public interface IArchiveExtractor
{
    /// <summary>
    /// Inspects <paramref name="filePath"/>; if it is a supported archive (zip/rar/7z) it handles only the
    /// <c>*.txt</c> entries whose filename matches the keyword filter. If the archive is encrypted, a password
    /// is looked up in <paramref name="messageText"/> and used to unlock it. <paramref name="onFileExtracted"/>
    /// is invoked once per handled entry (for live metrics). Never throws for bad/corrupt archives — failures
    /// are reported in <see cref="ExtractionResult.Errors"/>.
    ///
    /// When <paramref name="onTextEntry"/> is provided, each matching entry is streamed to that callback and
    /// <b>never written to disk</b> (in-memory mode: no per-message folder is created and
    /// <see cref="ExtractionResult.ExtractedFiles"/> holds entry names, not paths). When it is null, matching
    /// entries are written under <paramref name="destinationDir"/> and their paths are returned.
    /// </summary>
    Task<ExtractionResult> ExtractTextFilesAsync(
        string filePath,
        string destinationDir,
        string? messageText = null,
        Action? onFileExtracted = null,
        Func<string, Stream, CancellationToken, Task>? onTextEntry = null,
        CancellationToken ct = default);
}
