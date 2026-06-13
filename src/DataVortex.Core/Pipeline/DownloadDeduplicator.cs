using DataVortex.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

public interface IDownloadDeduplicator
{
    /// <summary>Reserves an archive in memory; returns false if already seen (reserved this run or committed).</summary>
    bool TryReserve(long documentId, long sizeBytes, string fileName);

    /// <summary>Persists an archive as permanently handled. Call once its download has succeeded.</summary>
    void Commit(long documentId, long sizeBytes, string fileName);

    /// <summary>Drops an in-flight reservation (call on a failed/cancelled download) so it can be retried.</summary>
    bool RemoveReservation(long documentId, long sizeBytes, string fileName);

    /// <summary>Wipes the whole dedup store (memory + DB). Exposed as a button in the UI.</summary>
    void Clear();

    /// <summary>Approximate number of distinct archives currently remembered.</summary>
    int Count { get; }
}

/// <summary>
/// Prevents downloading the same archive twice — even across different channels and times. Keys on the
/// Telegram document id (catches forwards/reposts) AND on size+filename (catches identical re-uploads).
///
/// Reservations are in-memory only; a key is <b>persisted (to the SQLite dedup table) only when the
/// download succeeds</b> (<see cref="Commit"/>). So a failed/interrupted download is retried on the next
/// detection/restart. At startup the committed set is seeded with a single SQL query (no per-file reads).
/// </summary>
public sealed class DownloadDeduplicator : IDownloadDeduplicator
{
    private readonly object _gate = new();
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal); // persisted, survives restart
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);  // in-flight, this run only
    private readonly IStorageService _storage;
    private readonly ILogger<DownloadDeduplicator> _log;

    public DownloadDeduplicator(IStorageService storage, ILogger<DownloadDeduplicator> log)
    {
        _storage = storage;
        _log = log;

        foreach (var key in storage.LoadDedupKeys()) _committed.Add(key);
        foreach (var (size, name) in storage.GetArchiveSizeNames()) _committed.Add(SnKey(size, name));
        _log.LogInformation("Deduplicator initialised ({Count} archive key(s) known)", _committed.Count);
    }

    public int Count
    {
        get { lock (_gate) return (_committed.Count + _reserved.Count) / 2; }
    }

    public bool TryReserve(long documentId, long sizeBytes, string fileName)
    {
        var idKey = IdKey(documentId);
        var snKey = SnKey(sizeBytes, fileName);
        lock (_gate)
        {
            if (Seen(idKey) || Seen(snKey)) return false;
            _reserved.Add(idKey);
            _reserved.Add(snKey);
            return true;
        }
    }

    public void Commit(long documentId, long sizeBytes, string fileName)
    {
        var idKey = IdKey(documentId);
        var snKey = SnKey(sizeBytes, fileName);
        bool persist;
        lock (_gate)
        {
            _reserved.Remove(idKey);
            _reserved.Remove(snKey);
            persist = _committed.Add(idKey) | _committed.Add(snKey);
        }
        if (persist) _storage.AddDedupKeys(new[] { idKey, snKey }); // DB write outside the lock
    }

    public bool RemoveReservation(long documentId, long sizeBytes, string fileName)
    {
        var idKey = IdKey(documentId);
        var snKey = SnKey(sizeBytes, fileName);
        lock (_gate)
        {
            var removed = _reserved.Remove(idKey);
            removed |= _reserved.Remove(snKey);
            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _committed.Clear();
            _reserved.Clear();
        }
        _storage.ClearDedupKeys();
        _log.LogInformation("Deduplication store cleared");
    }

    private bool Seen(string key) => _committed.Contains(key) || _reserved.Contains(key);
    private static string IdKey(long id) => "id:" + id;
    private static string SnKey(long size, string name) => "sn:" + size + "|" + (name ?? string.Empty).Trim().ToLowerInvariant();
}
