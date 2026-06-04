using System.Text.Json;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

public interface IPendingDownloadStore
{
    /// <summary>Remembers a detected download so it survives a restart.</summary>
    void Add(DownloadJob job);

    /// <summary>Forgets a download once it has completed or permanently failed.</summary>
    void Remove(long channelId, long messageId, long documentId);

    /// <summary>Everything detected but not yet finished, for resume at startup.</summary>
    IReadOnlyList<PendingDownload> Snapshot();
}

/// <summary>
/// Persists the set of archives detected but not yet downloaded to <c>data/pending-downloads.json</c>, so the
/// download queue survives a restart. Updated on every enqueue (Add) and on completion/permanent failure
/// (Remove). Keyed by channel+message+document so duplicates collapse. The file is small JSON; thread-safe.
/// </summary>
public sealed class PendingDownloadStore : IPendingDownloadStore
{
    private readonly string _path;
    private readonly ILogger<PendingDownloadStore> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingDownload> _pending = new();

    public PendingDownloadStore(AppPaths paths, ILogger<PendingDownloadStore> log)
    {
        _path = Path.Combine(paths.Root, "pending-downloads.json");
        _log = log;
        Load();
    }

    private static string Key(long channelId, long messageId, long documentId)
        => $"{channelId}:{messageId}:{documentId}";

    public void Add(DownloadJob job)
    {
        var p = new PendingDownload(job.ChannelId, job.ChannelTitle, job.MessageId, job.FileName,
            job.SizeBytes, job.MimeType, job.ReceivedUtc, job.MessageText, job.DocumentId);
        lock (_gate)
        {
            _pending[Key(job.ChannelId, job.MessageId, job.DocumentId)] = p;
            SaveNoLock();
        }
    }

    public void Remove(long channelId, long messageId, long documentId)
    {
        lock (_gate)
        {
            if (_pending.Remove(Key(channelId, messageId, documentId)))
                SaveNoLock();
        }
    }

    public IReadOnlyList<PendingDownload> Snapshot()
    {
        lock (_gate) return _pending.Values.ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<PendingDownload>>(File.ReadAllText(_path));
            if (list is not null)
                foreach (var p in list)
                    _pending[Key(p.ChannelId, p.MessageId, p.DocumentId)] = p;
            if (_pending.Count > 0)
                _log.LogInformation("Pending-download store loaded: {Count} to resume", _pending.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to load pending downloads"); }
    }

    private void SaveNoLock()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_pending.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save pending downloads"); }
    }
}
