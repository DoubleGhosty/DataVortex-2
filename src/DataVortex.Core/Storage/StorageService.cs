using System.Text.Json;
using System.Text.Json.Serialization;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;

namespace DataVortex.Core.Storage;

/// <summary>Writes one JSON metadata record per processed file and enumerates extracted text files.</summary>
public sealed class StorageService : IStorageService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    public AppPaths Paths { get; }

    public StorageService(AppPaths paths) => Paths = paths.EnsureCreated();

    public async Task SaveRecordAsync(FileRecord record, CancellationToken ct = default)
    {
        var file = Path.Combine(Paths.Metadata, $"{record.ProcessedUtc:yyyyMMdd}_{record.Id:N}.json");
        var json = JsonSerializer.Serialize(record, Json);
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public IReadOnlyList<FileRecord> LoadRecords()
    {
        var list = new List<FileRecord>();
        if (!Directory.Exists(Paths.Metadata)) return list;
        foreach (var f in Directory.EnumerateFiles(Paths.Metadata, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<FileRecord>(File.ReadAllText(f), Json);
                if (r is not null) list.Add(r);
            }
            catch
            {
                // Skip unreadable record files.
            }
        }
        return list.OrderByDescending(r => r.ProcessedUtc).ToList();
    }

    public IEnumerable<string> EnumerateExtractedFiles(string? search = null)
    {
        if (!Directory.Exists(Paths.Extracted)) yield break;
        foreach (var f in Directory.EnumerateFiles(Paths.Extracted, "*.txt", SearchOption.AllDirectories))
        {
            if (string.IsNullOrWhiteSpace(search) ||
                Path.GetFileName(f).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                yield return f;
            }
        }
    }
}
