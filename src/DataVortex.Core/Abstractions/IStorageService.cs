using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

/// <summary>Aggregate counters over all stored records.</summary>
public sealed record StorageStats(long TotalRecords, long TotalBytes, long Completed, long Ignored, long Failed);

/// <summary>Per-channel rollup.</summary>
public sealed record ChannelStat(long ChannelId, string Title, long Count, long TotalBytes, long Failed);

/// <summary>One tested account, stored with queryable columns so the registry never rewrites a whole file
/// and the UI can search/filter/paginate. <paramref name="Key"/> is the normalized identity (email+password).</summary>
public sealed record AccountRecord(
    string Key, string Email, string Password, string? Url,
    bool Success, int StatusCode, string? AccountState, string Category,
    decimal? Credit, string? BirthDate, string? Message, DateTime TestedUtc,
    string? AccessToken, string? RefreshToken);

/// <summary>Account count per derived category (VALIDE / BAN / CUSTOM / INVALIDE).</summary>
public sealed record AccountCategoryCount(string Category, int Count);

public interface IStorageService
{
    AppPaths Paths { get; }

    Task SaveRecordAsync(FileRecord record, CancellationToken ct = default);
    IReadOnlyList<FileRecord> LoadRecords();
    IEnumerable<string> EnumerateExtractedFiles(string? search = null);

    // ---- Stats & search (indexed SQL) ----
    StorageStats GetStats();
    IReadOnlyList<ChannelStat> GetChannelStats();
    /// <summary>Filtered, paged record search. <paramref name="text"/> matches the filename (contains).</summary>
    IReadOnlyList<FileRecord> SearchRecords(string? text = null, ProcessingStatus? status = null,
        long? channelId = null, int limit = 300, int offset = 0);
    /// <summary>Count of records matching the same filters (for pagination).</summary>
    int CountRecords(string? text = null, ProcessingStatus? status = null, long? channelId = null);

    // ---- De-duplication store (backs IDownloadDeduplicator) ----
    IReadOnlyCollection<string> LoadDedupKeys();
    void AddDedupKeys(IEnumerable<string> keys);
    void ClearDedupKeys();
    IEnumerable<(long SizeBytes, string FileName)> GetArchiveSizeNames();

    // ---- Tested-account store (backs IAccountTestRegistry) ----
    /// <summary>Inserts or updates one account (single-row write — no whole-file rewrite).</summary>
    void UpsertAccount(AccountRecord account);
    /// <summary>Every stored account — used once at startup to seed the in-memory dedup index.</summary>
    IReadOnlyList<AccountRecord> LoadAccounts();
    /// <summary>Filtered, paged account browse. <paramref name="text"/> matches email (contains);
    /// <paramref name="categories"/> null/empty = all categories.</summary>
    IReadOnlyList<AccountRecord> SearchAccounts(string? text = null,
        IReadOnlyCollection<string>? categories = null, int limit = 200, int offset = 0);
    /// <summary>Count of accounts matching the same filters (for pagination).</summary>
    int CountAccounts(string? text = null, IReadOnlyCollection<string>? categories = null);
    /// <summary>Account totals grouped by category (for the live counters).</summary>
    IReadOnlyList<AccountCategoryCount> GetAccountCategoryCounts();
    /// <summary>Every successful account that still holds a refresh token — re-checkable without a captcha
    /// (credit, status/expiry and suspension) via the refresh-token flow.</summary>
    IReadOnlyList<AccountRecord> LoadAccountsToRecheck();
    /// <summary>Deletes every stored account (used by a full re-test from scratch).</summary>
    void ClearAccounts();
}
