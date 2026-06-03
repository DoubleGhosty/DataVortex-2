using DataVortex.Core.Models;

namespace DataVortex.Core.Abstractions;

public sealed record TelegramCredentials(int ApiId, string ApiHash, string PhoneNumber);

/// <summary>Result of scanning one page of a channel's history during backfill.</summary>
public sealed record HistoryPage(int Scanned, int Enqueued, int NextOffsetId, bool Exhausted, int TotalInChannel);

public interface ITelegramService
{
    ConnectionState State { get; }
    string? LoggedInUser { get; }

    event Action<ConnectionState>? StateChanged;

    /// <summary>Raised when interactive login input is required: argument is "verification_code" or "password".</summary>
    event Action<string>? VerificationRequested;

    /// <summary>Raised for every new message that carries a file in a *watched* channel (push, not polled).</summary>
    event Action<DownloadJob>? FileDetected;

    Task ConnectAsync(TelegramCredentials credentials, CancellationToken ct = default);
    void ProvideVerificationCode(string code);
    void ProvidePassword(string password);

    Task<IReadOnlyList<ChannelInfo>> GetDialogsAsync(CancellationToken ct = default);
    void SetWatchedChannels(IEnumerable<long> channelIds);

    /// <summary>Ensures the channel/chat cache is populated (needed before scanning history).</summary>
    Task EnsureDialogsLoadedAsync(CancellationToken ct = default);

    /// <summary>Scans one page of a watched channel's history, enqueuing any new (non-duplicate) archives.</summary>
    Task<HistoryPage> ScanHistoryPageAsync(long channelId, int offsetId, int pageSize, CancellationToken ct = default);

    Task DownloadAsync(DownloadJob job, Stream destination, IProgress<long>? progress, CancellationToken ct = default);

    /// <summary>Pushes download-tuning settings (parallel chunks per file) onto the live client. No-op if not connected.</summary>
    void ApplyTransferTuning();

    Task DisconnectAsync();
}
