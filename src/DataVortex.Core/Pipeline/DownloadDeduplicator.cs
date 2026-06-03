using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
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

    /// <summary>Wipes the whole dedup store (memory + disk). Exposed as a button in the UI.</summary>
    void Clear();

    /// <summary>Approximate number of distinct archives currently remembered.</summary>
    int Count { get; }
}

/// <summary>
/// Prevents downloading the same archive twice — even across different channels and times. Keys on the
/// Telegram document id (catches forwards/reposts) AND on size+filename (catches identical re-uploads).
///
/// Reservations are in-memory only; a key is <b>persisted to disk only when the download succeeds</b>
/// (<see cref="Commit"/>). So a download that fails or is interrupted is <i>not</i> permanently skipped —
/// it is retried on the next detection/restart. The on-disk store survives restarts and the pipeline's
/// post-processing file cleanup.
/// </summary>
public sealed class DownloadDeduplicator : IDownloadDeduplicator
{
    private readonly object _gate = new();
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal); // persisted, survives restart
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);  // in-flight, this run only
    private readonly string _path;
    private readonly ILogger<DownloadDeduplicator> _log;

    public DownloadDeduplicator(AppPaths paths, IStorageService storage, ILogger<DownloadDeduplicator> log)
    {
        _path = Path.Combine(paths.Root, "dedup.keys");
        _log = log;
        LoadFromFile();
        SeedFromRecords(storage.LoadRecords());
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
        lock (_gate)
        {
            _reserved.Remove(idKey);
            _reserved.Remove(snKey);
            var added = _committed.Add(idKey);
            added |= _committed.Add(snKey);
            if (added) Append(idKey, snKey);
        }
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
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete dedup store"); }
        }
        _log.LogInformation("Deduplication store cleared");
    }

    private bool Seen(string key) => _committed.Contains(key) || _reserved.Contains(key);
    private static string IdKey(long id) => "id:" + id;
    private static string SnKey(long size, string name) => "sn:" + size + "|" + (name ?? string.Empty).Trim().ToLowerInvariant();

    private void SeedFromRecords(IReadOnlyList<FileRecord> records)
    {
        foreach (var r in records) _committed.Add(SnKey(r.SizeBytes, r.OriginalFileName));
    }

    private void LoadFromFile()
    {
        try
        {
            if (File.Exists(_path))
                foreach (var line in File.ReadAllLines(_path))
                    if (!string.IsNullOrWhiteSpace(line)) _committed.Add(line.Trim());
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to load dedup store"); }
    }

    private void Append(params string[] keys)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllLines(_path, keys);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to persist dedup keys"); }
    }
}
