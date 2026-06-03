using System.IO.Compression;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DataVortex.Core.Extraction;

/// <summary>
/// Extracts <c>*.txt</c> entries from archives. ZIP uses <see cref="System.IO.Compression"/>; RAR and 7z
/// (and encrypted ZIP) use SharpCompress. Archive type is detected by magic bytes (then extension).
///
/// Encryption: after detecting an encrypted archive it logs the fact, looks up a password in the
/// accompanying Telegram message (<see cref="PasswordExtractor"/>) and, if found, unlocks the archive with
/// it. A password present in the message of a NON-encrypted archive is ignored.
///
/// Selective extraction (default on): an entry is kept only if its <b>filename</b> contains one of
/// <see cref="AppSettings.ExtractKeywords"/>; non-matching entries are never decompressed.
/// </summary>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    private readonly ISettingsService _settings;
    private readonly ILogger<ArchiveExtractor> _log;

    public ArchiveExtractor(ISettingsService settings, ILogger<ArchiveExtractor> log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<ExtractionResult> ExtractTextFilesAsync(string filePath, string destinationDir,
        string? messageText = null, Action? onFileExtracted = null, CancellationToken ct = default)
    {
        var extracted = new List<string>();
        var errors = new List<string>();
        var kind = DetectKind(filePath);
        var matcher = new KeywordMatcher(_settings.Current.ExtractOnlyMatchingTxt, _settings.Current.ExtractKeywords);
        var name = Path.GetFileName(filePath);

        bool isEncrypted = false;
        string? password = null;

        try
        {
            Directory.CreateDirectory(destinationDir);

            if (kind is ArchiveKind.Zip or ArchiveKind.Rar or ArchiveKind.SevenZip)
            {
                isEncrypted = DetectEncrypted(filePath);
                _log.LogInformation("Archive {File} is {State}", name, isEncrypted ? "ENCRYPTED" : "not encrypted");

                if (isEncrypted)
                {
                    password = PasswordExtractor.FromMessage(messageText);
                    if (password is not null)
                        _log.LogInformation("Password found in message for {File} — attempting extraction with it", name);
                    else
                        _log.LogWarning("Archive {File} is encrypted but no password was found in its message — skipping", name);
                }
            }

            switch (kind)
            {
                case ArchiveKind.PlainText:
                    await ProcessTxtAsync(() => File.OpenRead(filePath), name, destinationDir, matcher, extracted, errors, onFileExtracted, ct).ConfigureAwait(false);
                    break;

                case ArchiveKind.Zip when !isEncrypted:
                    await ExtractZipAsync(filePath, destinationDir, matcher, extracted, errors, onFileExtracted, ct).ConfigureAwait(false);
                    break;

                case ArchiveKind.Zip:        // encrypted ZIP -> SharpCompress (with the password)
                case ArchiveKind.Rar:
                case ArchiveKind.SevenZip:
                    if (isEncrypted && password is null) break; // cannot extract without the password
                    await ExtractWithSharpCompressAsync(filePath, destinationDir, matcher, password, extracted, errors, onFileExtracted, ct).ConfigureAwait(false);
                    break;
            }

            if (isEncrypted && password is not null && errors.Count == 0)
                _log.LogInformation("Unlocked {File} with the message password ({Count} txt extracted)", name, extracted.Count);
            else if (isEncrypted && password is not null && errors.Count > 0)
                _log.LogWarning("Password for {File} did not work (wrong password?)", name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            _log.LogWarning(ex, "Extraction failed for {File}", filePath);
        }

        return new ExtractionResult(kind, extracted, errors, isEncrypted, password is not null);
    }

    /// <summary>True if any entry is encrypted, or if the archive can't even be listed (encrypted headers).</summary>
    private static bool DetectEncrypted(string path)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path, null);
            foreach (var entry in archive.Entries)
                if (!entry.IsDirectory && entry.IsEncrypted) return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static async Task ExtractZipAsync(string path, string destDir, KeywordMatcher matcher,
        List<string> extracted, List<string> errors, Action? onFileExtracted, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            if (!IsTxt(entry.Name)) continue;
            await ProcessTxtAsync(entry.Open, entry.Name, destDir, matcher, extracted, errors, onFileExtracted, ct).ConfigureAwait(false);
        }
    }

    private static async Task ExtractWithSharpCompressAsync(string path, string destDir, KeywordMatcher matcher,
        string? password, List<string> extracted, List<string> errors, Action? onFileExtracted, CancellationToken ct)
    {
        var options = string.IsNullOrEmpty(password) ? null : new ReaderOptions { Password = password };
        using var archive = ArchiveFactory.OpenArchive(path, options);

        // Solid archives (RAR/7z) must be read sequentially: random-access OpenEntryStream() would
        // re-decompress from the start for every entry (O(n²)). A forward-only reader is O(n).
        if (archive.IsSolid)
        {
            ExtractSolid(archive, destDir, matcher, extracted, errors, onFileExtracted, ct);
            return;
        }

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            var name = Path.GetFileName((entry.Key ?? string.Empty).Replace('\\', '/'));
            if (!IsTxt(name)) continue;
            await ProcessTxtAsync(entry.OpenEntryStream, name, destDir, matcher, extracted, errors, onFileExtracted, ct).ConfigureAwait(false);
        }
    }

    private static void ExtractSolid(IArchive archive, string destDir, KeywordMatcher matcher,
        List<string> extracted, List<string> errors, Action? onFileExtracted, CancellationToken ct)
    {
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            var name = Path.GetFileName((entry.Key ?? string.Empty).Replace('\\', '/'));
            if (!IsTxt(name)) continue;
            if (matcher.Enabled && !matcher.MatchesText(name)) continue;
            try
            {
                var dest = UniquePath(destDir, name);
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    reader.WriteEntryTo(fs);
                extracted.Add(dest);
                onFileExtracted?.Invoke();
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
    }

    /// <summary>Keeps the entry only if its filename matches the keyword filter; non-matching entries are
    /// skipped without being opened. Path-traversal is defeated by flattening to the bare filename.</summary>
    private static async Task ProcessTxtAsync(Func<Stream> openStream, string entryName, string destDir,
        KeywordMatcher matcher, List<string> extracted, List<string> errors, Action? onFileExtracted, CancellationToken ct)
    {
        var fileName = Path.GetFileName(entryName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file.txt";

        if (matcher.Enabled && !matcher.MatchesText(fileName)) return;

        try
        {
            var dest = UniquePath(destDir, fileName);
            await using (var src = openStream())
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await src.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            extracted.Add(dest);
            onFileExtracted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"{fileName}: {ex.Message}");
        }
    }

    private static ArchiveKind DetectKind(string path)
    {
        try
        {
            var head = new byte[8];
            int n;
            using (var fs = File.OpenRead(path))
                n = fs.Read(head, 0, head.Length);

            if (n >= 4 && (Match(head, 0x50, 0x4B, 0x03, 0x04) || Match(head, 0x50, 0x4B, 0x05, 0x06)))
                return ArchiveKind.Zip;
            if (n >= 6 && Match(head, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07))
                return ArchiveKind.Rar;
            if (n >= 6 && Match(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
                return ArchiveKind.SevenZip;
        }
        catch
        {
            // Fall back to extension below.
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".zip" => ArchiveKind.Zip,
            ".rar" => ArchiveKind.Rar,
            ".7z" => ArchiveKind.SevenZip,
            ".txt" => ArchiveKind.PlainText,
            _ => ArchiveKind.Other
        };
    }

    private static bool Match(byte[] buffer, params byte[] signature)
    {
        for (int i = 0; i < signature.Length; i++)
            if (buffer[i] != signature[i]) return false;
        return true;
    }

    private static bool IsTxt(string name) => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
