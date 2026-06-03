using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

public interface IStorageService
{
    AppPaths Paths { get; }
    Task SaveRecordAsync(FileRecord record, CancellationToken ct = default);
    IReadOnlyList<FileRecord> LoadRecords();
    IEnumerable<string> EnumerateExtractedFiles(string? search = null);
}
