namespace DataVortex.Core.Models;

/// <summary>A channel/group the user has opted to archive. Persisted in settings.</summary>
public sealed class WatchedChannel
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsChannel { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}
