using DataVortex.Core.Abstractions;
using DataVortex.Core.Configuration;
using DataVortex.Core.Licensing;
using DataVortex.Core.Models;
using DataVortex.Core.Pipeline;
using DataVortex.Licensing;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace DataVortex.Core.Telegram;

/// <summary>
/// WTelegramClient (MTProto user API) wrapper. Login is fully interactive: the synchronous WTelegram
/// <c>Config</c> callback blocks on a gate until the UI supplies the verification code / 2FA password.
/// Real-time messages arrive push-style through an <see cref="UpdateManager"/>, which also recovers any
/// updates missed while disconnected — so the archiver never has to poll.
/// </summary>
public sealed class TelegramService : ITelegramService, IDisposable
{
    private readonly AppPaths _paths;
    private readonly IDownloadDeduplicator _dedup;
    private readonly ISettingsService _settings;
    private readonly ILicenseGate _gate;
    private readonly ILogger<TelegramService> _log;

    private Client? _client;
    private UpdateManager? _manager;
    private TelegramCredentials? _creds;

    private readonly object _stateLock = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    // Login bridge between WTelegram's synchronous Config callback and the async UI.
    private readonly SemaphoreSlim _codeGate = new(0, 1);
    private readonly SemaphoreSlim _passwordGate = new(0, 1);
    private string? _verificationCode;
    private string? _password;

    private readonly object _watchedLock = new();
    private readonly HashSet<long> _watched = new();

    private Timer? _keepAlive;
    private volatile bool _disposed;

    public ConnectionState State { get { lock (_stateLock) return _state; } }
    public string? LoggedInUser { get; private set; }

    public event Action<ConnectionState>? StateChanged;
    public event Action<string>? VerificationRequested;
    public event Action<DownloadJob>? FileDetected;

    public TelegramService(AppPaths paths, IDownloadDeduplicator dedup, ISettingsService settings,
        ILicenseGate gate, ILogger<TelegramService> log)
    {
        _paths = paths;
        _dedup = dedup;
        _settings = settings;
        _gate = gate;
        _log = log;
    }

    // ---------------------------------------------------------------- connection

    public async Task ConnectAsync(TelegramCredentials credentials, CancellationToken ct = default)
    {
        _creds = credentials;
        SetState(ConnectionState.Connecting);
        try
        {
            _client = new Client(Config);
            _client.FloodRetryThreshold = 300;   // auto-wait FLOOD_WAIT up to 5 minutes instead of throwing
            _client.MaxAutoReconnects = 1000;    // keep reconnecting for a long-running archiver
            ApplyTransferTuning();               // parallel chunks per file — main download-speed lever
            _manager = _client.WithUpdateManager(OnUpdate, _paths.UpdateStateFile);
            var user = await _client.LoginUserIfNeeded().ConfigureAwait(false);
            LoggedInUser = string.IsNullOrWhiteSpace(user.first_name) ? user.id.ToString() : user.first_name;
            SetState(ConnectionState.Connected);
            _log.LogInformation("Logged in as {User} (id {Id})", LoggedInUser, user.id);
            StartKeepAlive();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Telegram connection failed");
            SetState(ConnectionState.Failed);
            throw;
        }
    }

    /// <summary>Applies download-tuning to the live client: how many file chunks WTelegram fetches in
    /// parallel per file. WTelegram's own default is a conservative 2; raising it is the main lever for
    /// download speed on high-latency links. Clamped to 1..32 to avoid FLOOD_WAIT. Safe to call anytime —
    /// a no-op until the client exists, and re-applied on every (re)connect.</summary>
    public void ApplyTransferTuning()
    {
        var c = _client;
        if (c is null) return;
        var n = Math.Clamp(_settings.Current.ParallelTransfersPerFile, 1, 32);
        c.ParallelTransfers = n;
        _log.LogInformation("Download ParallelTransfers set to {N}", n);
    }

    /// <summary>Synchronous callback invoked by WTelegram on its own thread; blocking here is expected.</summary>
    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id": return _creds!.ApiId.ToString();
            case "api_hash": return _creds!.ApiHash;
            case "phone_number": return _creds!.PhoneNumber;
            case "session_pathname": return _paths.SessionFile;

            case "verification_code":
                SetState(ConnectionState.WaitingForCode);
                VerificationRequested?.Invoke("verification_code");
                _codeGate.Wait();
                return _verificationCode;

            case "password":
                SetState(ConnectionState.WaitingForPassword);
                VerificationRequested?.Invoke("password");
                _passwordGate.Wait();
                return _password;

            default:
                return null; // use library default
        }
    }

    public void ProvideVerificationCode(string code)
    {
        _verificationCode = code?.Trim();
        if (_codeGate.CurrentCount == 0) _codeGate.Release();
    }

    public void ProvidePassword(string password)
    {
        _password = password;
        if (_passwordGate.CurrentCount == 0) _passwordGate.Release();
    }

    public async Task DisconnectAsync()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }
        SetState(ConnectionState.Disconnected);
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- updates / files

    private async Task OnUpdate(Update update)
    {
        try
        {
            // UpdateNewChannelMessage derives from UpdateNewMessage, so the base case covers both
            // channel/supergroup messages and regular group/private messages.
            if (update is UpdateNewMessage { message: Message m })
                HandleMessage(m);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error handling update");
        }
        await Task.CompletedTask;
    }

    /// <summary>Filters, de-duplicates and (if new) enqueues an archive message. Returns true if enqueued.
    /// Shared by live updates and history backfill.</summary>
    private bool HandleMessage(Message message)
    {
        if (message.media is not MessageMediaDocument { document: Document doc }) return false;

        long channelId = message.peer_id switch
        {
            PeerChannel pc => pc.channel_id,
            PeerChat pchat => pchat.chat_id,
            _ => 0
        };
        if (channelId == 0) return false;

        bool watched;
        lock (_watchedLock) watched = _watched.Contains(channelId);
        if (!watched) return false;

        ChatBase? chat = null;
        _manager?.Chats.TryGetValue(channelId, out chat);
        var title = ChatTitle(chat, channelId);
        var fileName = GetDocumentFileName(doc);

        // Only download the configured file types (archives by default) — skip plain .txt and other docs.
        var allowed = _settings.Current.DownloadExtensions;
        if (allowed.Count > 0 && !allowed.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            _log.LogDebug("Skipping non-archive {File} ({Mime}) in {Channel}", fileName, doc.mime_type, title);
            return false;
        }

        // Deduplicate: never download the same archive twice — even across different channels/times.
        if (!_dedup.TryReserve(doc.id, doc.size, fileName))
        {
            _log.LogInformation("Skipping duplicate archive {File} ({Size} bytes) in {Channel} — already handled",
                fileName, doc.size, title);
            return false;
        }

        var job = new DownloadJob
        {
            ChannelId = channelId,
            ChannelTitle = title,
            MessageId = message.id,
            FileName = fileName,
            SizeBytes = doc.size,
            MimeType = doc.mime_type,
            ReceivedUtc = DateTime.UtcNow,
            MessageText = message.message,
            DocumentId = doc.id,
            Document = doc
        };

        _log.LogInformation("File detected in {Channel}: {File} ({Size} bytes)", title, fileName, doc.size);
        FileDetected?.Invoke(job);
        return true;
    }

    public async Task DownloadAsync(DownloadJob job, Stream destination, IProgress<long>? progress, CancellationToken ct = default)
    {
        _gate.Require(Capability.ScanTelegram); // dispersed gate on the actual pull (throws → the pipeline marks it failed)
        if (_client is null) throw new InvalidOperationException("Telegram client is not connected.");
        try
        {
            // ThrottledStream already reports progress; WTelegram writes sequentially into it.
            await _client.DownloadFileAsync(job.Document, destination).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Message.Contains("FILE_REFERENCE"))
        {
            // The document's file_reference expired while the job sat in the queue. Re-fetch the message
            // to obtain a fresh Document, then restart the download.
            _log.LogWarning("file_reference expired for {File}; re-fetching the message", job.FileName);
            var fresh = await RefreshDocumentAsync(job).ConfigureAwait(false);
            if (fresh is null) throw;
            if (destination.CanSeek) { destination.SetLength(0); destination.Position = 0; }
            await _client.DownloadFileAsync(fresh, destination).ConfigureAwait(false);
        }
    }

    /// <summary>Rebuilds a persisted pending download: re-fetches a fresh Document (file_reference may have
    /// expired) and returns a ready-to-enqueue job, or null if the message/document is gone.</summary>
    public async Task<DownloadJob?> RebuildPendingAsync(PendingDownload pending, CancellationToken ct = default)
    {
        var doc = await RefreshDocumentAsync(ToJob(pending, null!)).ConfigureAwait(false);
        return doc is null ? null : ToJob(pending, doc);
    }

    private static DownloadJob ToJob(PendingDownload p, Document doc) => new()
    {
        ChannelId = p.ChannelId,
        ChannelTitle = p.ChannelTitle,
        MessageId = p.MessageId,
        FileName = p.FileName,
        SizeBytes = p.SizeBytes,
        MimeType = p.MimeType,
        ReceivedUtc = p.ReceivedUtc,
        MessageText = p.MessageText,
        DocumentId = p.DocumentId,
        Document = doc
    };

    /// <summary>Re-fetches the message carrying the archive to obtain a Document with a fresh file_reference.</summary>
    private async Task<Document?> RefreshDocumentAsync(DownloadJob job)
    {
        try
        {
            if (_client is null || _manager is null) return null;
            _manager.Chats.TryGetValue(job.ChannelId, out var chat);
            InputPeer? peer = chat switch
            {
                Channel ch => new InputPeerChannel(ch.id, ch.access_hash),
                Chat c => new InputPeerChat(c.id),
                _ => null
            };
            if (peer is null) return null;

            var res = await _client.GetMessages(peer, new InputMessage[] { new InputMessageID { id = (int)job.MessageId } }).ConfigureAwait(false);
            var msg = res.Messages.OfType<Message>().FirstOrDefault(m => m.id == job.MessageId);
            if (msg?.media is MessageMediaDocument { document: Document d }) return d;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh document for {File}", job.FileName);
        }
        return null;
    }

    // ---------------------------------------------------------------- history / backfill

    public async Task EnsureDialogsLoadedAsync(CancellationToken ct = default)
    {
        if (_client is null || _manager is null) return;
        if (_manager.Chats.Count > 0) return;
        var dialogs = await _client.Messages_GetAllDialogs().ConfigureAwait(false);
        dialogs.CollectUsersChats(_manager.Users, _manager.Chats);
    }

    public async Task SendHtmlToTargetAsync(string target, string html, CancellationToken ct = default)
    {
        if (_client is null || _manager is null || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(html)) return;
        await EnsureDialogsLoadedAsync(ct).ConfigureAwait(false);

        var t = target.Trim();
        var handle = t.TrimStart('@');

        // 1) a group/channel already in the dialog cache, matched by title or @username
        var chat = _manager.Chats.Values.FirstOrDefault(c =>
            string.Equals(c.Title, t, StringComparison.OrdinalIgnoreCase) ||
            (c is Channel ch && string.Equals(ch.MainUsername, handle, StringComparison.OrdinalIgnoreCase)));
        InputPeer? peer = chat switch
        {
            Channel ch => new InputPeerChannel(ch.id, ch.access_hash),
            Chat c => new InputPeerChat(c.id),
            _ => null
        };

        // 2) otherwise resolve it as a public @username (user, bot or channel)
        if (peer is null && !t.Contains(' '))
        {
            try
            {
                var r = await _client.Contacts_ResolveUsername(handle).ConfigureAwait(false);
                peer = r.peer switch
                {
                    PeerUser pu when r.users.TryGetValue(pu.user_id, out var u) => u.ToInputPeer(),
                    PeerChannel pc when r.chats.TryGetValue(pc.channel_id, out var c) => c.ToInputPeer(),
                    PeerChat pch when r.chats.TryGetValue(pch.chat_id, out var c) => c.ToInputPeer(),
                    _ => null
                };
            }
            catch (Exception ex) { _log.LogWarning(ex, "Notification: destinataire '{Target}' non résolu", t); }
        }

        if (peer is null) { _log.LogWarning("Notification: destinataire Telegram '{Target}' introuvable", t); return; }

        var entities = _client.HtmlToEntities(ref html);
        await _client.SendMessageAsync(peer, html, entities: entities).ConfigureAwait(false);
    }

    public async Task<HistoryPage> ScanHistoryPageAsync(long channelId, int offsetId, int pageSize, CancellationToken ct = default)
    {
        // Capability gate (dispersed, silent effect): no ScanTelegram ⇒ report an empty page so the caller quietly
        // stops digging, rather than throwing at the exact call the way DownloadAsync does.
        if (!_gate.Allows(Capability.ScanTelegram)) return new HistoryPage(0, 0, offsetId, false, 0);
        if (_client is null || _manager is null) return new HistoryPage(0, 0, offsetId, true, 0);
        if (!_manager.Chats.TryGetValue(channelId, out var chat))
            return new HistoryPage(0, 0, offsetId, false, 0); // not resolved yet — retry later

        InputPeer? peer = chat switch
        {
            Channel ch => new InputPeerChannel(ch.id, ch.access_hash),
            Chat c => new InputPeerChat(c.id),
            _ => null
        };
        if (peer is null) return new HistoryPage(0, 0, offsetId, true, 0);

        try
        {
            var history = await _client.Messages_GetHistory(peer, offset_id: offsetId, limit: Math.Clamp(pageSize, 1, 100)).ConfigureAwait(false);
            var messages = history.Messages.OfType<Message>().ToList();
            if (messages.Count == 0)
                return new HistoryPage(0, 0, offsetId, true, history.Count);

            int enqueued = 0;
            foreach (var m in messages)
            {
                ct.ThrowIfCancellationRequested();
                if (HandleMessage(m)) enqueued++;
            }
            return new HistoryPage(messages.Count, enqueued, messages.Min(m => m.id), false, history.Count);
        }
        catch (RpcException rpc) when (IsInaccessibleChannel(rpc))
        {
            // The channel is permanently out of reach for us (private / invalid / banned / left): Messages_GetHistory
            // will keep throwing 400. Signal it like an unresolved channel (Scanned 0, not Exhausted) so the backfill
            // marks it done and drops it from the round-robin instead of retrying every 10 s forever. Transient errors
            // (FLOOD_WAIT, network) are NOT caught here — they bubble up so the backfill can retry.
            _log.LogDebug("ScanHistory: canal {Channel} inaccessible ({Error}) → ignoré", channelId, rpc.Message);
            return new HistoryPage(0, 0, offsetId, false, 0);
        }
    }

    /// <summary>Telegram errors that mean a channel is permanently unreadable for this account (not a transient
    /// hiccup): re-requesting its history will always throw, so the backfill should drop it rather than retry.</summary>
    private static readonly string[] InaccessibleChannelErrors =
        { "CHANNEL_PRIVATE", "CHANNEL_INVALID", "CHAT_FORBIDDEN", "PEER_ID_INVALID", "CHAT_ID_INVALID", "USER_BANNED_IN_CHANNEL" };

    private static bool IsInaccessibleChannel(RpcException rpc)
        => InaccessibleChannelErrors.Any(code => rpc.Message?.Contains(code, StringComparison.OrdinalIgnoreCase) == true);

    // ---------------------------------------------------------------- dialogs / watched set

    public async Task<IReadOnlyList<ChannelInfo>> GetDialogsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Telegram client is not connected.");

        var dialogs = await _client.Messages_GetAllDialogs().ConfigureAwait(false);
        dialogs.CollectUsersChats(_manager!.Users, _manager.Chats);

        var list = new List<ChannelInfo>();
        foreach (var chat in dialogs.chats.Values)
        {
            switch (chat)
            {
                case Channel ch:
                    list.Add(new ChannelInfo
                    {
                        Id = ch.id,
                        Title = ch.title,
                        IsChannel = true,
                        ParticipantsCount = ch.participants_count
                    });
                    break;
                case Chat c:
                    list.Add(new ChannelInfo
                    {
                        Id = c.id,
                        Title = c.title,
                        IsChannel = false,
                        ParticipantsCount = c.participants_count
                    });
                    break;
            }
        }
        return list.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SetWatchedChannels(IEnumerable<long> channelIds)
    {
        lock (_watchedLock)
        {
            _watched.Clear();
            foreach (var id in channelIds) _watched.Add(id);
        }
        _log.LogInformation("Now watching {Count} channel(s)", _watched.Count);
    }

    // ---------------------------------------------------------------- keep-alive / state

    private void StartKeepAlive()
    {
        _keepAlive?.Dispose();
        // Connection keep-alive / health probe (NOT data polling). WTelegram auto-reconnects under the
        // hood; this surfaces the reconnect state to the UI and verifies the link is responsive.
        _keepAlive = new Timer(_ => _ = KeepAliveTick(), null, 60_000, 60_000);
    }

    private async Task KeepAliveTick()
    {
        if (_disposed || _client is null) return;
        try
        {
            await _client.Help_GetConfig().ConfigureAwait(false);
            if (State is ConnectionState.Reconnecting or ConnectionState.Failed)
                SetState(ConnectionState.Connected);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Keep-alive failed; WTelegram will attempt to reconnect");
            SetState(ConnectionState.Reconnecting);
        }
    }

    private void SetState(ConnectionState newState)
    {
        lock (_stateLock)
        {
            if (_state == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(newState);
        _log.LogInformation("Telegram state -> {State}", newState);
    }

    private static string ChatTitle(ChatBase? chat, long id) => chat switch
    {
        Channel ch => ch.title,
        Chat c => c.title,
        _ => id.ToString()
    };

    private static string GetDocumentFileName(Document doc)
    {
        var attr = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
        if (attr?.file_name is { Length: > 0 } name) return name;

        var ext = doc.mime_type switch
        {
            "application/zip" => ".zip",
            "application/x-7z-compressed" => ".7z",
            "application/vnd.rar" or "application/x-rar-compressed" => ".rar",
            "text/plain" => ".txt",
            _ => ".bin"
        };
        return $"{doc.id}{ext}";
    }

    public void Dispose()
    {
        _disposed = true;
        _keepAlive?.Dispose();
        _client?.Dispose();
    }
}
