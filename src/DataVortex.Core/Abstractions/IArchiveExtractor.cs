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
    /// Inspects <paramref name="filePath"/>; if it is a supported archive (zip/rar/7z) it extracts only the
    /// <c>*.txt</c> entries whose filename matches the keyword filter into <paramref name="destinationDir"/>.
    /// If the archive is encrypted, a password is looked up in <paramref name="messageText"/> and used to
    /// unlock it. <paramref name="onFileExtracted"/> is invoked once per extracted file (for live metrics).
    /// Never throws for bad/corrupt archives — failures are reported in <see cref="ExtractionResult.Errors"/>.
    /// </summary>
    Task<ExtractionResult> ExtractTextFilesAsync(
        string filePath,
        string destinationDir,
        string? messageText = null,
        Action? onFileExtracted = null,
        CancellationToken ct = default);
}
