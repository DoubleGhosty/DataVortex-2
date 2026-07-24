using System.Security.Cryptography;
using System.Text.Json;
using DataVortex.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DataVortex.LicenseServer;

/// <summary>Palier B authority: issues and refreshes short-lived, hardware-bound runtime sessions. A client must
/// hold a live session to use the online-gated capabilities; because sessions expire fast and are counted per
/// licence, revocation, seat overflow and network lapses all cut the online features quickly — independent of the
/// long offline lease. Every decision (licence status, hardware binding, seat count) is made here, server-side.</summary>
public sealed class SessionService
{
    /// <summary>How long a session is valid before it must be refreshed. Short on purpose (the "always-online"
    /// window): a client that can't refresh loses the online features within this.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly SigningService _signing;
    private readonly IConfiguration _cfg;

    public SessionService(LicenseDbContext db, SigningService signing, IConfiguration cfg)
    {
        _db = db;
        _signing = signing;
        _cfg = cfg;
    }

    /// <summary>The operational recipe the checker needs, read from server config (<c>Recipe:*</c>). Delivered to
    /// clients only inside a live session, sealed under the session key — never present in the client binary.</summary>
    private OperationalRecipe Recipe() => new()
    {
        BaseUrl = _cfg["Recipe:BaseUrl"] ?? "",
        SiteKey = _cfg["Recipe:SiteKey"] ?? "",
        PageUrl = _cfg["Recipe:PageUrl"] ?? "",
        SignInPath = _cfg["Recipe:SignInPath"] ?? "",
        RefreshPath = _cfg["Recipe:RefreshPath"] ?? "",
        UnsuspendPath = _cfg["Recipe:UnsuspendPath"] ?? "",
        MePath = _cfg["Recipe:MePath"] ?? "",
    };

    public async Task<SessionApiResponse> StartAsync(SessionStartDto dto, string? ip)
    {
        if (string.IsNullOrWhiteSpace(dto.Token) || dto.Fingerprint is null)
            return new("ServerError", message: "requête invalide");

        // Authenticate the client's lease token and resolve its licence.
        var verifier = await _signing.VerifierAsync();
        var res = verifier.Verify(dto.Token);
        if (!res.Valid || res.Claims is null || !Guid.TryParse(res.Claims.LicenseId, out var licId))
            return new("ServerError", message: "jeton non authentifié");

        var lic = await _db.Licenses.Include(l => l.Activations).FirstOrDefaultAsync(l => l.Id == licId);
        if (lic is null) return new("Revoked", message: "licence introuvable");

        var statusError = StatusError(lic);
        if (statusError is not null) { await LogAsync(lic.Id, "session_start", statusError.ToLowerInvariant(), ip); return new(statusError); }

        // The session must run on a machine that holds an ACTIVE activation of this licence (authoritative HW binding).
        var snapshot = ToSnapshot(dto.Fingerprint);
        var fph = snapshot.Hash;
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.FingerprintHash == fph);
        var activation = device is null ? null : lic.Activations.FirstOrDefault(a => a.Active && a.DeviceId == device.Id);
        if (device is null || activation is null)
        {
            await LogAsync(lic.Id, "session_start", "hardware_mismatch", ip);
            return new("HardwareMismatch", message: "cette licence est liée à une autre machine");
        }

        // Seat enforcement: reuse this machine's live session if any (idempotent); otherwise cap concurrent live
        // sessions at MaxActivations. Expired sessions don't count toward the cap.
        var now = DateTimeOffset.UtcNow;
        var live = await _db.Sessions.Where(s => s.LicenseId == lic.Id && s.Active && s.ExpiresAt > now).ToListAsync();
        var mine = live.FirstOrDefault(s => s.FingerprintHash == fph);
        if (mine is null && live.Count >= lic.MaxActivations)
        {
            await LogAsync(lic.Id, "session_start", "seat_limit", ip);
            return new("ActivationLimit", message: "nombre maximal de sessions simultanées atteint");
        }

        var session = mine ?? new Session { LicenseId = lic.Id, FingerprintHash = fph };
        session.Active = true;
        session.LastRefreshAt = now;
        session.ExpiresAt = now + SessionLifetime;
        session.Ip = ip;
        if (string.IsNullOrEmpty(session.SessionKey))
            session.SessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (mine is null) _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        var bundle = RecipeCrypto.Protect(Recipe(), Convert.FromBase64String(session.SessionKey));
        await LogAsync(lic.Id, "session_start", "ok", ip);
        return new("Ok", session_token: session.Id.ToString(), expires_at: session.ExpiresAt,
            session_key: session.SessionKey, operational_bundle: bundle);
    }

    public async Task<SessionApiResponse> RefreshAsync(SessionRefreshDto dto, string? ip)
    {
        if (!Guid.TryParse(dto.SessionToken, out var sid))
            return new("ServerError", message: "requête invalide");

        var now = DateTimeOffset.UtcNow;
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sid);
        if (session is null || !session.Active || session.ExpiresAt <= now)
            return new("Expired", message: "session expirée");

        var lic = await _db.Licenses.FirstOrDefaultAsync(l => l.Id == session.LicenseId);
        if (lic is null) { session.Active = false; await _db.SaveChangesAsync(); return new("Revoked", message: "licence introuvable"); }

        // Re-check licence status on every refresh → a revocation/suspension kills the session within one window.
        var statusError = StatusError(lic);
        if (statusError is not null)
        {
            session.Active = false;
            await _db.SaveChangesAsync();
            await LogAsync(lic.Id, "session_refresh", statusError.ToLowerInvariant(), ip);
            return new(statusError);
        }

        // Optional HW re-check when a fingerprint is provided (cheap, catches a moved session token).
        if (dto.Fingerprint is not null && !string.Equals(ToSnapshot(dto.Fingerprint).Hash, session.FingerprintHash, StringComparison.Ordinal))
        {
            session.Active = false;
            await _db.SaveChangesAsync();
            await LogAsync(lic.Id, "session_refresh", "hardware_mismatch", ip);
            return new("HardwareMismatch", message: "session liée à une autre machine");
        }

        session.LastRefreshAt = now;
        session.ExpiresAt = now + SessionLifetime;
        session.Ip = ip;
        if (string.IsNullOrEmpty(session.SessionKey))
            session.SessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await _db.SaveChangesAsync();

        var bundle = RecipeCrypto.Protect(Recipe(), Convert.FromBase64String(session.SessionKey));
        await LogAsync(lic.Id, "session_refresh", "ok", ip);
        return new("Ok", session_token: session.Id.ToString(), expires_at: session.ExpiresAt,
            session_key: session.SessionKey, operational_bundle: bundle);
    }

    // ------------------------------------------------------------------ helpers (mirror LicenseService)

    private static string? StatusError(License lic)
    {
        if (lic.Status == LicenseState.Revoked) return "Revoked";
        if (lic.Status == LicenseState.Suspended) return "Suspended";
        if (lic.Status == LicenseState.Expired) return "Expired";
        if (lic.ExpiresAt is { } exp && DateTimeOffset.UtcNow >= exp) return "Expired";
        return null;
    }

    private static FingerprintSnapshot ToSnapshot(FingerprintDto dto)
        => new((dto.Components ?? new List<ComponentDto>()).Select(c => new ComponentHash(c.Id ?? "", c.H ?? "", c.W)));

    private async Task LogAsync(Guid? licenseId, string action, string result, string? ip)
    {
        _db.AuthLogs.Add(new AuthLog { LicenseId = licenseId, Action = action, Result = result, Ip = ip });
        await _db.SaveChangesAsync();
    }
}
