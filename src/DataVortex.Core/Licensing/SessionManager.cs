using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Licensing;

/// <summary>Client half of Palier B: keeps a live, hardware-bound runtime session with the server. It opens a
/// session from the stored lease token, then refreshes it on a timer well before it expires. The session token
/// lives in memory ONLY (never on disk). <see cref="IsOnline"/> is what the online-gated capabilities key off — a
/// revocation, a seat overflow or a network lapse drops it within one session window, and the licence guard re-feeds
/// the gate so <c>RunPipeline</c>/<c>CheckPassculture</c> go dark. Losing the session never touches the offline
/// lease (Telegram scan / export keep working); it only gates the online features.</summary>
public sealed class SessionManager : IDisposable
{
    // Refresh comfortably inside the server's 15-minute window.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ILicenseApiClient _api;
    private readonly ILicenseStore _store;
    private readonly ILogger<SessionManager> _log;
    private readonly object _gate = new();
    private Timer? _timer;
    private string? _sessionToken;
    private bool _running;

    public bool IsOnline { get; private set; }

    /// <summary>Raised (on a worker thread) whenever the online state flips. The licence guard listens and re-feeds
    /// the entitlement gate; keep handlers thread-safe (don't touch the UI directly).</summary>
    public event Action<bool>? OnlineChanged;

    public SessionManager(ILicenseApiClient api, ILicenseStore store, ILogger<SessionManager> log)
    {
        _api = api;
        _store = store;
        _log = log;
    }

    /// <summary>Opens the first session now (synchronously awaited by the caller at startup) and starts the refresh
    /// loop. Safe to call once; further calls are ignored.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate) { if (_running) return; _running = true; }
        await EstablishAsync(ct).ConfigureAwait(false);
        _timer = new Timer(async _ => await TickAsync().ConfigureAwait(false), null, RefreshInterval, RefreshInterval);
    }

    public void Stop()
    {
        lock (_gate) _running = false;
        _timer?.Dispose();
        _timer = null;
        SetOnline(false);
        lock (_gate) _sessionToken = null;
    }

    /// <summary>Forces one refresh/establish now (e.g. right after a re-activation).</summary>
    public Task RefreshNowAsync() => TickAsync();

    private async Task EstablishAsync(CancellationToken ct)
    {
        var data = _store.Load();
        if (data is null) { SetOnline(false); return; }
        try
        {
            var fp = HardwareFingerprint.Collect().Snapshot();
            var r = await _api.StartSessionAsync(data.Token, fp, ct).ConfigureAwait(false);
            if (r.Success && !string.IsNullOrEmpty(r.SessionToken))
            {
                lock (_gate) _sessionToken = r.SessionToken;
                SetOnline(true);
            }
            else
            {
                lock (_gate) _sessionToken = null;
                _log.LogDebug("Session start refused: {Status}", r.Status);
                SetOnline(false);
            }
        }
        catch (Exception ex)
        {
            // Server unreachable → offline. The online features gate off; the offline lease still governs the state.
            _log.LogDebug("Session start failed: {Error}", ex.Message);
            SetOnline(false);
        }
    }

    private async Task TickAsync()
    {
        string? tok;
        lock (_gate) tok = _sessionToken;
        if (tok is null) { await EstablishAsync(CancellationToken.None).ConfigureAwait(false); return; }

        try
        {
            var fp = HardwareFingerprint.Collect().Snapshot();
            var r = await _api.RefreshSessionAsync(tok, fp).ConfigureAwait(false);
            if (r.Success && !string.IsNullOrEmpty(r.SessionToken))
            {
                lock (_gate) _sessionToken = r.SessionToken;
                SetOnline(true);
            }
            else
            {
                // Explicit server refusal (revoked/suspended/expired/seat) → drop, then try to re-establish once.
                lock (_gate) _sessionToken = null;
                SetOnline(false);
                await EstablishAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Session refresh failed: {Error}", ex.Message);
            SetOnline(false);
        }
    }

    private void SetOnline(bool value)
    {
        if (IsOnline == value) return;
        IsOnline = value;
        try { OnlineChanged?.Invoke(value); } catch { /* never let a handler break the loop */ }
    }

    public void Dispose() => Stop();
}
