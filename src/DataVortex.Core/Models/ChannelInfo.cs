namespace DataVortex.Core.Models;

/// <summary>A dialog (channel or group) the logged-in user belongs to, returned by the dialog scan.</summary>
public sealed class ChannelInfo : Observable
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Username { get; init; }
    public bool IsChannel { get; init; }
    public int ParticipantsCount { get; init; }

    private bool _isWatched;
    public bool IsWatched { get => _isWatched; set => SetField(ref _isWatched, value); }

    public string Kind => IsChannel ? "Channel" : "Group";
}
