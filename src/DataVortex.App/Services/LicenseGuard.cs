using System.Windows.Threading;
using DataVortex.Core.Licensing;
using DataVortex.Licensing;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.Services;

/// <summary>Runtime licence watchdog. While the app runs it re-checks the licence with the server on a timer
/// (a "heartbeat"). The check is FORCED — it contacts the server even while the local lease is still valid — so a
/// suspension or revocation done in the admin console takes effect within one interval instead of only at the
/// next lease renewal. On loss of access it stops itself and raises <see cref="AccessLost"/>; the app then halts
/// work and sends the user back to the activation screen. A network outage does NOT count as loss (the lease
/// still covers the user — grace).</summary>
public sealed class LicenseGuard
{
    private readonly ILicenseManager _manager;
    private readonly ILicenseGate _gate;
    private readonly SessionManager _session;
    private readonly ILogger<LicenseGuard> _log;
    private DispatcherTimer? _timer;
    private bool _busy;

    public LicenseStatus Current { get; private set; } = new() { State = LicenseState.Unknown };

    /// <summary>Raised (UI thread) whenever the status is refreshed — the Settings panel listens to this.</summary>
    public event Action<LicenseStatus>? StatusChanged;

    /// <summary>Raised (UI thread) when the licence is no longer usable (revoked/suspended/expired/blocked).</summary>
    public event Action<LicenseStatus>? AccessLost;

    public LicenseGuard(ILicenseManager manager, ILicenseGate gate, SessionManager session, ILogger<LicenseGuard> log)
    {
        _manager = manager;
        _gate = gate;
        _session = session;
        _log = log;
        // A session gained/lost flips the online-only capabilities without any status change — re-feed the gate.
        _session.OnlineChanged += _ => FeedGate(Current);
    }

    /// <summary>Records the status established at activation time (no server round-trip).</summary>
    public void SetStatus(LicenseStatus status) => Update(status);

    public void Start(TimeSpan interval)
    {
        Stop();
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await BeatAsync();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Forces one refresh now (e.g. after a re-activation) and returns the new status.</summary>
    public async Task<LicenseStatus> RefreshAsync(bool forceServerCheck)
    {
        var s = await _manager.EvaluateAsync(forceServerCheck);
        Update(s);
        return s;
    }

    private async Task BeatAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = await _manager.EvaluateAsync(forceServerCheck: true);
            Update(s);
            if (s.State is not (LicenseState.Active or LicenseState.Degraded))
            {
                _log.LogWarning("Licence no longer usable ({State}): {Message}", s.State, s.Message);
                Stop();
                AccessLost?.Invoke(s);
            }
        }
        catch (Exception ex) { _log.LogDebug("Licence heartbeat failed: {Error}", ex.Message); }
        finally { _busy = false; }
    }

    private void Update(LicenseStatus status)
    {
        Current = status;
        FeedGate(status);
        StatusChanged?.Invoke(status);
    }

    /// <summary>Pushes entitlements derived from the SIGNED claims + the live-session flag into the gate. This is the
    /// ONLY thing that unlocks the gated features in a Release build — bypassing the startup check (which is what
    /// makes this run) leaves the gate on <see cref="Entitlements.None"/> and every feature denied. Online-only
    /// capabilities additionally require a live session (Palier B). Touches only the thread-safe gate, so it is safe
    /// to call from the session worker thread when the online state flips.</summary>
    private void FeedGate(LicenseStatus status)
    {
        _gate.Set(status.Claims is { } c && status.State is LicenseState.Active or LicenseState.Degraded
            ? Entitlements.From(c, online: _session.IsOnline)
            : Entitlements.None);
    }
}
