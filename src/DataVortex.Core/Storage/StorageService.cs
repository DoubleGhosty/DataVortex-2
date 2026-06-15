using System.Text.Json;
using System.Text.Json.Serialization;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Storage;

/// <summary>
/// SQLite-backed metadata + de-duplication store. Each <see cref="FileRecord"/> is stored with queryable
/// columns (channel, size, name, status, dates) plus a <c>Json</c> column holding the full record — so
/// nothing in the model is lost while still allowing fast indexed queries. Far more scalable than one JSON
/// file per record (the old design re-read every file on each call). Legacy JSON metadata and the old
/// <c>dedup.keys</c> file are imported automatically on first run.
/// </summary>
public sealed class StorageService : IStorageService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connString;
    // Single long-lived write connection reused by every write path (all serialised by _writeLock), so a
    // high-throughput workload no longer opens a fresh connection + re-runs pragmas on every single row.
    // Reads keep opening short-lived connections — WAL lets them run concurrently without blocking the writer.
    private readonly SqliteConnection _writeConn;
    private readonly ILogger? _log;
    public AppPaths Paths { get; }

    public StorageService(AppPaths paths, ILogger<StorageService>? log = null)
    {
        _log = log;
        Paths = paths.EnsureCreated();
        var dbPath = Path.Combine(Paths.Root, "datavortex.db");
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _writeConn = Open();
        Initialize();
        MigrateLegacyIfAny();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        // journal_mode=WAL is persistent (database-level), set once in Initialize; only these
        // connection-scoped pragmas need re-applying on every open.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var cmd = _writeConn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS records (
    Id TEXT PRIMARY KEY, SourceChannelId INTEGER, SourceChannelTitle TEXT, MessageId INTEGER,
    OriginalFileName TEXT, SizeBytes INTEGER, MimeType TEXT, ReceivedUtc TEXT, ProcessedUtc TEXT,
    Kind INTEGER, Status INTEGER, Error TEXT, Json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_records_processed ON records(ProcessedUtc DESC);
CREATE INDEX IF NOT EXISTS idx_records_sizename  ON records(SizeBytes, OriginalFileName);
CREATE INDEX IF NOT EXISTS idx_records_channel   ON records(SourceChannelId);
CREATE TABLE IF NOT EXISTS dedup (Key TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS accounts (
    Key TEXT PRIMARY KEY, Email TEXT, Password TEXT, Url TEXT,
    Success INTEGER, StatusCode INTEGER, AccountState TEXT, Category TEXT,
    Credit REAL, BirthDate TEXT, Message TEXT, TestedUtc TEXT,
    AccessToken TEXT, RefreshToken TEXT);
CREATE INDEX IF NOT EXISTS idx_accounts_category ON accounts(Category);
CREATE INDEX IF NOT EXISTS idx_accounts_credit   ON accounts(Credit DESC);
CREATE INDEX IF NOT EXISTS idx_accounts_email    ON accounts(Email);";
        cmd.ExecuteNonQuery();
    }

    public async Task SaveRecordAsync(FileRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { InsertRecord(_writeConn, null, record); }
        finally { _writeLock.Release(); }
    }

    private static void InsertRecord(SqliteConnection conn, SqliteTransaction? tx, FileRecord r)
    {
        using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR REPLACE INTO records
(Id, SourceChannelId, SourceChannelTitle, MessageId, OriginalFileName, SizeBytes, MimeType, ReceivedUtc, ProcessedUtc, Kind, Status, Error, Json)
VALUES ($id,$cid,$ct,$mid,$name,$size,$mime,$rec,$proc,$kind,$status,$err,$json);";
        cmd.Parameters.AddWithValue("$id", r.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$cid", r.SourceChannelId);
        cmd.Parameters.AddWithValue("$ct", r.SourceChannelTitle ?? "");
        cmd.Parameters.AddWithValue("$mid", r.MessageId);
        cmd.Parameters.AddWithValue("$name", r.OriginalFileName ?? "");
        cmd.Parameters.AddWithValue("$size", r.SizeBytes);
        cmd.Parameters.AddWithValue("$mime", (object?)r.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rec", r.ReceivedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$proc", r.ProcessedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$kind", (int)r.Kind);
        cmd.Parameters.AddWithValue("$status", (int)r.Status);
        cmd.Parameters.AddWithValue("$err", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(r, Json));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FileRecord> LoadRecords()
    {
        var list = new List<FileRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Json FROM records ORDER BY ProcessedUtc DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var r = JsonSerializer.Deserialize<FileRecord>(reader.GetString(0), Json);
                if (r is not null) list.Add(r);
            }
            catch { /* skip unreadable row */ }
        }
        return list;
    }

    public IReadOnlyCollection<string> LoadDedupKeys()
    {
        var keys = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM dedup;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    public void AddDedupKeys(IEnumerable<string> keys)
    {
        _writeLock.Wait();
        try
        {
            using var tx = _writeConn.BeginTransaction();
            using var cmd = _writeConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO dedup(Key) VALUES ($k);";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            cmd.Parameters.Add(p);
            foreach (var k in keys) { p.Value = k; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    public void ClearDedupKeys()
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = "DELETE FROM dedup;";
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public void UpsertAccount(AccountRecord a)
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO accounts
(Key, Email, Password, Url, Success, StatusCode, AccountState, Category, Credit, BirthDate, Message, TestedUtc, AccessToken, RefreshToken)
VALUES ($k,$e,$p,$u,$s,$sc,$st,$cat,$cr,$bd,$m,$t,$at,$rt);";
            cmd.Parameters.AddWithValue("$k", a.Key);
            cmd.Parameters.AddWithValue("$e", a.Email ?? "");
            cmd.Parameters.AddWithValue("$p", a.Password ?? "");
            cmd.Parameters.AddWithValue("$u", (object?)a.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", a.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("$sc", a.StatusCode);
            cmd.Parameters.AddWithValue("$st", (object?)a.AccountState ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cat", a.Category ?? "");
            cmd.Parameters.AddWithValue("$cr", a.Credit.HasValue ? (object)(double)a.Credit.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$bd", (object?)a.BirthDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", (object?)a.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", a.TestedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$at", (object?)a.AccessToken ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rt", (object?)a.RefreshToken ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public IReadOnlyList<AccountRecord> LoadAccounts()
        => QueryAccounts("SELECT " + AccountColumns + " FROM accounts;", _ => { });

    public IReadOnlyList<AccountRecord> SearchAccounts(string? text = null,
        IReadOnlyCollection<string>? categories = null, int limit = 200, int offset = 0)
    {
        var (where, bind) = AccountFilter(text, categories);
        return QueryAccounts(
            $"SELECT {AccountColumns} FROM accounts {where} ORDER BY Credit DESC, TestedUtc DESC LIMIT $limit OFFSET $offset;",
            cmd => { bind(cmd); cmd.Parameters.AddWithValue("$limit", limit); cmd.Parameters.AddWithValue("$offset", offset); });
    }

    public int CountAccounts(string? text = null, IReadOnlyCollection<string>? categories = null)
    {
        var (where, bind) = AccountFilter(text, categories);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM accounts {where};";
        bind(cmd);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<AccountCategoryCount> GetAccountCategoryCounts()
    {
        var list = new List<AccountCategoryCount>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Category, COUNT(*) FROM accounts GROUP BY Category;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new AccountCategoryCount(reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt32(1)));
        return list;
    }

    public void ClearAccounts()
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConn.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts;";
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public IReadOnlyList<AccountRecord> LoadAccountsToRecheck()
        => QueryAccounts(
            // Every account that is NOT a wrong password (INVALIDE) and still holds a refresh token — i.e. all
            // VALIDE / CUSTOM / BAN / EXPIRE / other with a usable token, so a recheck can revisit them without
            // a captcha. (A wrong-password account has no token anyway.)
            $"SELECT {AccountColumns} FROM accounts " +
            "WHERE COALESCE(Category,'') <> 'INVALIDE' AND RefreshToken IS NOT NULL AND RefreshToken <> '';",
            _ => { });

    private const string AccountColumns =
        "Key, Email, Password, Url, Success, StatusCode, AccountState, Category, Credit, BirthDate, Message, TestedUtc, AccessToken, RefreshToken";

    /// <summary>Builds the shared WHERE clause + a parameter binder for the account filters.</summary>
    private static (string where, Action<SqliteCommand> bind) AccountFilter(string? text, IReadOnlyCollection<string>? categories)
    {
        var clauses = new List<string>();
        var cats = categories?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (!string.IsNullOrWhiteSpace(text)) clauses.Add("Email LIKE $like");
        if (cats is { Count: > 0 })
        {
            var names = cats.Select((_, i) => "$cat" + i);
            clauses.Add($"Category IN ({string.Join(",", names)})");
        }
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        return (where, cmd =>
        {
            if (!string.IsNullOrWhiteSpace(text)) cmd.Parameters.AddWithValue("$like", "%" + text.Trim() + "%");
            if (cats is { Count: > 0 })
                for (int i = 0; i < cats.Count; i++) cmd.Parameters.AddWithValue("$cat" + i, cats[i]);
        });
    }

    private IReadOnlyList<AccountRecord> QueryAccounts(string sql, Action<SqliteCommand> bind)
    {
        var list = new List<AccountRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AccountRecord(
                Key: reader.GetString(0),
                Email: reader.IsDBNull(1) ? "" : reader.GetString(1),
                Password: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Url: reader.IsDBNull(3) ? null : reader.GetString(3),
                Success: reader.GetInt64(4) != 0,
                StatusCode: (int)reader.GetInt64(5),
                AccountState: reader.IsDBNull(6) ? null : reader.GetString(6),
                Category: reader.IsDBNull(7) ? "" : reader.GetString(7),
                Credit: reader.IsDBNull(8) ? null : (decimal)reader.GetDouble(8),
                BirthDate: reader.IsDBNull(9) ? null : reader.GetString(9),
                Message: reader.IsDBNull(10) ? null : reader.GetString(10),
                TestedUtc: reader.IsDBNull(11) ? default : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
                AccessToken: reader.IsDBNull(12) ? null : reader.GetString(12),
                RefreshToken: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return list;
    }

    public IEnumerable<(long SizeBytes, string FileName)> GetArchiveSizeNames()
    {
        var result = new List<(long, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT SizeBytes, OriginalFileName FROM records;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return result;
    }

    public StorageStats GetStats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COALESCE(SUM(SizeBytes),0),
            COALESCE(SUM(CASE WHEN Status=$c THEN 1 ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN Status=$i THEN 1 ELSE 0 END),0),
            COALESCE(SUM(CASE WHEN Status=$f THEN 1 ELSE 0 END),0)
            FROM records;";
        cmd.Parameters.AddWithValue("$c", (int)ProcessingStatus.Completed);
        cmd.Parameters.AddWithValue("$i", (int)ProcessingStatus.Ignored);
        cmd.Parameters.AddWithValue("$f", (int)ProcessingStatus.Failed);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new StorageStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4))
            : new StorageStats(0, 0, 0, 0, 0);
    }

    public IReadOnlyList<ChannelStat> GetChannelStats()
    {
        var list = new List<ChannelStat>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT SourceChannelId, MAX(SourceChannelTitle), COUNT(*), COALESCE(SUM(SizeBytes),0),
            COALESCE(SUM(CASE WHEN Status=$f THEN 1 ELSE 0 END),0)
            FROM records GROUP BY SourceChannelId ORDER BY COUNT(*) DESC;";
        cmd.Parameters.AddWithValue("$f", (int)ProcessingStatus.Failed);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ChannelStat(reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return list;
    }

    public IReadOnlyList<FileRecord> SearchRecords(string? text = null, ProcessingStatus? status = null,
        long? channelId = null, int limit = 300, int offset = 0)
    {
        var list = new List<FileRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Json FROM records
            WHERE ($text IS NULL OR OriginalFileName LIKE $like)
              AND ($status < 0 OR Status = $status)
              AND ($cid = 0 OR SourceChannelId = $cid)
            ORDER BY ProcessedUtc DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$like", text is null ? DBNull.Value : "%" + text + "%");
        cmd.Parameters.AddWithValue("$status", status.HasValue ? (int)status.Value : -1);
        cmd.Parameters.AddWithValue("$cid", channelId ?? 0);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var r = JsonSerializer.Deserialize<FileRecord>(reader.GetString(0), Json);
                if (r is not null) list.Add(r);
            }
            catch { /* skip */ }
        }
        return list;
    }

    public int CountRecords(string? text = null, ProcessingStatus? status = null, long? channelId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM records
            WHERE ($text IS NULL OR OriginalFileName LIKE $like)
              AND ($status < 0 OR Status = $status)
              AND ($cid = 0 OR SourceChannelId = $cid);";
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$like", text is null ? DBNull.Value : "%" + text + "%");
        cmd.Parameters.AddWithValue("$status", status.HasValue ? (int)status.Value : -1);
        cmd.Parameters.AddWithValue("$cid", channelId ?? 0);
        return Convert.ToInt32(cmd.ExecuteScalar());
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

    /// <summary>One-time import of legacy <c>metadata/*.json</c> records and the old <c>dedup.keys</c> file.
    /// Records are imported in batches (committing periodically) so a very large metadata folder never builds
    /// one giant transaction, and progress is logged.</summary>
    private void MigrateLegacyIfAny()
    {
        try
        {
            long recordCount;
            using (var cmd = _writeConn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM records;";
                recordCount = Convert.ToInt64(cmd.ExecuteScalar());
            }

            if (recordCount == 0 && Directory.Exists(Paths.Metadata))
            {
                var files = Directory.EnumerateFiles(Paths.Metadata, "*.json").ToList();
                if (files.Count > 0)
                {
                    _log?.LogInformation("Migrating {Count} legacy metadata record(s) into SQLite…", files.Count);
                    const int batch = 2000;
                    int imported = 0;

                    var tx = _writeConn.BeginTransaction();
                    try
                    {
                        for (int i = 0; i < files.Count; i++)
                        {
                            try
                            {
                                var r = JsonSerializer.Deserialize<FileRecord>(File.ReadAllText(files[i]), Json);
                                if (r is not null) { InsertRecord(_writeConn, tx, r); imported++; }
                            }
                            catch { /* skip bad file */ }

                            if ((i + 1) % batch == 0)
                            {
                                tx.Commit(); tx.Dispose();
                                _log?.LogInformation("  …migrated {Done}/{Total} record(s)", i + 1, files.Count);
                                tx = _writeConn.BeginTransaction();
                            }
                        }
                        tx.Commit();
                    }
                    finally { tx.Dispose(); }
                    _log?.LogInformation("Legacy metadata migration done: {Imported} record(s) imported.", imported);
                }
            }

            var dedupFile = Path.Combine(Paths.Root, "dedup.keys");
            if (File.Exists(dedupFile))
            {
                var keys = File.ReadAllLines(dedupFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim());
                AddDedupKeys(keys);
                try { File.Move(dedupFile, dedupFile + ".migrated", overwrite: true); } catch { }
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Legacy migration failed"); }
    }

    public void Dispose()
    {
        try { _writeConn.Dispose(); } catch { /* already closed */ }
        SqliteConnection.ClearAllPools();
    }
}
