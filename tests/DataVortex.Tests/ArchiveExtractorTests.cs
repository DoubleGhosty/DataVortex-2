using System.IO.Compression;
using DataVortex.Core.Configuration;
using DataVortex.Core.Extraction;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataVortex.Tests;

/// <summary>Covers the two extraction modes: the default in-memory scan (writes nothing to disk) and the
/// opt-in disk mode (KeepExtractedFiles). The in-memory path is the high-volume optimisation.</summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvx_extract_" + Guid.NewGuid().ToString("N"));

    private ArchiveExtractor NewExtractor()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsService(Path.Combine(_dir, "settings.json")); // defaults: match *.txt named "password"
        return new ArchiveExtractor(settings, NullLogger<ArchiveExtractor>.Instance);
    }

    private string MakeZip()
    {
        var zipPath = Path.Combine(_dir, "logs.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteEntry(zip, "password.txt",
            "https://passculture.app/login\nUsername: john@example.com\nPassword: secret123\n");
        WriteEntry(zip, "readme.txt", "nothing interesting here\n"); // filtered out (name lacks "password")
        return zipPath;

        static void WriteEntry(ZipArchive zip, string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
    }

    [Fact]
    public async Task InMemory_scan_finds_credentials_and_writes_nothing()
    {
        var extractor = NewExtractor();
        var zip = MakeZip();
        var destDir = Path.Combine(_dir, "extracted", "chan");

        var creds = new List<CredentialEntry>();
        var result = await extractor.ExtractTextFilesAsync(
            zip, destDir, messageText: null, onFileExtracted: null,
            onTextEntry: (_, stream, _) =>
            {
                creds.AddRange(CredentialScanner.ScanStream(stream));
                return Task.CompletedTask;
            });

        Assert.True(result.Success);
        Assert.Single(result.ExtractedFiles);                  // only password.txt matched the keyword
        var c = Assert.Single(creds);
        Assert.Equal("john@example.com", c.Username);
        Assert.Equal("secret123", c.Password);
        Assert.False(Directory.Exists(destDir));               // in-memory mode never touches the disk
    }

    [Fact]
    public async Task Standalone_txt_is_scanned_even_when_its_name_lacks_the_keyword()
    {
        // Regression: a standalone .txt (downloaded because the user allowed the .txt extension) must be scanned
        // for content even if its filename doesn't match the keyword matcher — otherwise ULP combolists named e.g.
        // "123_combo.txt" were silently skipped and never tested.
        var extractor = NewExtractor(); // defaults: ExtractOnlyMatchingTxt on, keyword "password"
        var txt = Path.Combine(_dir, "123_combo.txt"); // name has NO "password"
        await File.WriteAllTextAsync(txt,
            "https://passculture.app/connexion:jane@gmail.com:Secr3t!\nhttps://netflix.com:x@y.fr:nope\n");

        var creds = new List<CredentialEntry>();
        var result = await extractor.ExtractTextFilesAsync(
            txt, Path.Combine(_dir, "out"), messageText: null, onFileExtracted: null,
            onTextEntry: (_, stream, _) => { creds.AddRange(CredentialScanner.ScanStream(stream)); return Task.CompletedTask; });

        Assert.True(result.Success);
        Assert.Single(result.ExtractedFiles);          // scanned despite the non-matching filename
        var c = Assert.Single(creds);                  // only the passculture ULP line (netflix is out of scope)
        Assert.Equal("jane@gmail.com", c.Username);
        Assert.Equal("Secr3t!", c.Password);
    }

    [Fact]
    public async Task Disk_mode_writes_the_matching_txt()
    {
        var extractor = NewExtractor();
        var zip = MakeZip();
        var destDir = Path.Combine(_dir, "extracted", "chan");

        var result = await extractor.ExtractTextFilesAsync(zip, destDir); // onTextEntry null => write to disk

        Assert.True(result.Success);
        Assert.True(Directory.Exists(destDir));
        var written = Directory.GetFiles(destDir);
        Assert.Single(written);
        Assert.EndsWith("password.txt", written[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
