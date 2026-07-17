using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>Tunables for the client licence policy. <see cref="PublicKeys"/> is the embedded signing-key ring
/// (SPKI base64); the rest are the grace/tolerance knobs.</summary>
public sealed class LicenseOptions
{
    /// <summary>Embedded server public keys (SubjectPublicKeyInfo, base64) — active + next, for rotation.</summary>
    public IReadOnlyList<string> PublicKeys { get; init; } = Array.Empty<string>();

    /// <summary>Weighted fingerprint match required to still count as the same machine (0..1).</summary>
    public double FingerprintThreshold { get; init; } = 0.6;

    /// <summary>How long the app keeps working after the lease expires while the server is unreachable.</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromDays(5);

    public string AppVersion { get; init; } = "";
}

/// <summary>The result of evaluating the licence: the state to drive the UI, the claims when available, and a
/// user-facing message.</summary>
public sealed class LicenseStatus
{
    public LicenseState State { get; init; }
    public LicenseClaims? Claims { get; init; }
    public string? Message { get; init; }

    /// <summary>The app may run (fully or in grace) in these states.</summary>
    public bool IsUsable => State is LicenseState.Active or LicenseState.Degraded;
}

/// <summary>Abstraction over the licence lifecycle so view-models and the startup gate depend on an interface
/// (and can be faked) rather than the concrete manager.</summary>
public interface ILicenseManager
{
    Task<LicenseStatus> ActivateAsync(string licenseKey, CancellationToken ct = default);
    Task<LicenseStatus> EvaluateAsync(CancellationToken ct = default);
    Task<LicenseStatus> RenewAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
}

/// <summary>Orchestrates the client licence lifecycle: activation, evaluation at startup / on a timer, renewal
/// and deactivation. It layers policy (lease/expiry, grace period, fingerprint binding, clock anti-rollback) on
/// top of a cryptographically-verified token. The clock is injectable so the state machine is fully testable.</summary>
public sealed class LicenseManager : ILicenseManager
{
    private readonly ILicenseStore _store;
    private readonly ILicenseApiClient _api;
    private readonly LicenseTokenVerifier _verifier;
    private readonly LicenseOptions _opts;
    private readonly Func<DateTimeOffset> _now;

    public LicenseManager(ILicenseStore store, ILicenseApiClient api, LicenseOptions options, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _api = api;
        _opts = options;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _verifier = new LicenseTokenVerifier(options.PublicKeys);
    }

    /// <summary>Activates a licence key on this machine: binds the current fingerprint, obtains a signed lease
    /// and persists it. A transport failure yields <see cref="LicenseState.NotActivated"/> with a reason.</summary>
    public async Task<LicenseStatus> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        var fp = HardwareFingerprint.Collect();

        LicenseResponse resp;
        try { resp = await _api.ActivateAsync(new ActivationRequest(licenseKey, fp.Snapshot(), _opts.AppVersion), ct).ConfigureAwait(false); }
        catch { return Status(LicenseState.NotActivated, "serveur injoignable — réessayez"); }

        if (resp.Status != LicenseServerStatus.Ok || resp.Token is null)
            return Status(LicenseState.NotActivated, ServerMessage(resp));

        var v = _verifier.Verify(resp.Token);
        if (!v.Valid || v.Claims is null)
            return Status(LicenseState.NotActivated, "réponse serveur non authentifiée");

        _store.Save(new LicenseStoreData(resp.Token, fp.Snapshot(), _now()));
        return Status(LicenseState.Active, null, v.Claims);
    }

    /// <summary>Evaluates the current licence (call at startup and periodically). Uses the local signed lease
    /// while it is valid (works offline), re-verifies online once it expires, and falls back to a grace period
    /// on a network outage before blocking.</summary>
    public async Task<LicenseStatus> EvaluateAsync(CancellationToken ct = default)
    {
        var data = _store.Load();
        if (data is null) return Status(LicenseState.NotActivated);

        var v = _verifier.Verify(data.Token);
        if (!v.Valid || v.Claims is null) return Status(LicenseState.NotActivated, "jeton local illisible");
        var claims = v.Claims;
        var now = _now();

        // Anti-rollback: the clock was set back well before the last server time we trusted.
        if (now + TimeSpan.FromHours(1) < data.LastSeen)
            return Status(LicenseState.Blocked, "horloge système incohérente", claims);

        // Hardware binding — local UX guard; the server re-checks authoritatively at /verify.
        var fp = HardwareFingerprint.Collect();
        if (!fp.Matches(data.Reference, _opts.FingerprintThreshold))
            return Status(LicenseState.HardwareChanged, "matériel modifié — réactivation requise", claims);

        if (claims.IsLicenseExpired(now))
            return Status(LicenseState.Expired, "licence expirée", claims);

        // Lease still valid → Active, fully offline. (Opportunistic renewal is a separate call on a timer.)
        if (claims.IsLeaseValid(now))
            return Status(LicenseState.Active, null, claims);

        // Lease expired → must re-verify online.
        LicenseResponse resp;
        try { resp = await _api.VerifyAsync(data.Token, fp.Snapshot(), ct).ConfigureAwait(false); }
        catch { resp = Offline; }

        switch (resp.Status)
        {
            case LicenseServerStatus.Ok when resp.Token is not null:
                var v2 = _verifier.Verify(resp.Token);
                if (v2.Valid && v2.Claims is not null)
                {
                    _store.Save(new LicenseStoreData(resp.Token, data.Reference, now));
                    return Status(LicenseState.Active, null, v2.Claims);
                }
                break;

            case LicenseServerStatus.Revoked:
            case LicenseServerStatus.Suspended:
                _store.Clear();
                return Status(LicenseState.Revoked, "licence révoquée", claims);

            case LicenseServerStatus.Expired:
                return Status(LicenseState.Expired, "licence expirée", claims);
        }

        // Server unreachable (or a transient error) → grace period measured from lease expiry.
        var graceEnd = claims.LeaseExpiresAt + _opts.GracePeriod;
        return now < graceEnd
            ? Status(LicenseState.Degraded, "hors ligne — période de grâce", claims)
            : Status(LicenseState.Blocked, "période de grâce épuisée — reconnexion requise", claims);
    }

    /// <summary>Opportunistic online renewal (call on a timer while running). Extends the lease when the server
    /// grants it, clears the licence on a confirmed revocation, and otherwise defers to <see cref="EvaluateAsync"/>.</summary>
    public async Task<LicenseStatus> RenewAsync(CancellationToken ct = default)
    {
        var data = _store.Load();
        if (data is null) return Status(LicenseState.NotActivated);

        LicenseResponse resp;
        try { resp = await _api.RenewAsync(data.Token, ct).ConfigureAwait(false); }
        catch { return await EvaluateAsync(ct).ConfigureAwait(false); }

        if (resp.Status == LicenseServerStatus.Ok && resp.Token is not null)
        {
            var v = _verifier.Verify(resp.Token);
            if (v.Valid && v.Claims is not null)
            {
                _store.Save(new LicenseStoreData(resp.Token, data.Reference, _now()));
                return Status(LicenseState.Active, null, v.Claims);
            }
        }

        if (resp.Status is LicenseServerStatus.Revoked or LicenseServerStatus.Suspended)
        {
            _store.Clear();
            return Status(LicenseState.Revoked, "licence révoquée");
        }

        return await EvaluateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Releases this machine's activation slot (best-effort server call) and wipes the local licence.</summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        var data = _store.Load();
        if (data is not null)
        {
            try { await _api.DeactivateAsync(data.Token, ct).ConfigureAwait(false); } catch { /* best-effort */ }
        }
        _store.Clear();
    }

    private static readonly LicenseResponse Offline = new(false, null, LicenseServerStatus.Offline, null);

    private static string ServerMessage(LicenseResponse r) => r.Message ?? r.Status switch
    {
        LicenseServerStatus.InvalidKey => "clé de licence invalide",
        LicenseServerStatus.ActivationLimit => "nombre maximal d'activations atteint pour cette clé",
        LicenseServerStatus.Revoked => "licence révoquée",
        LicenseServerStatus.Suspended => "licence suspendue",
        LicenseServerStatus.Expired => "licence expirée",
        LicenseServerStatus.HardwareMismatch => "cette clé est déjà liée à une autre machine",
        LicenseServerStatus.RateLimited => "trop de tentatives — réessayez plus tard",
        LicenseServerStatus.Offline => "serveur injoignable — réessayez",
        _ => "activation impossible",
    };

    private LicenseStatus Status(LicenseState state, string? message = null, LicenseClaims? claims = null)
        => new() { State = state, Message = message, Claims = claims };
}
