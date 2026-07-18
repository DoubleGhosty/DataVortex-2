using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>Request to activate a licence key on this machine.</summary>
public sealed record ActivationRequest(string LicenseKey, FingerprintSnapshot Fingerprint, string AppVersion);

/// <summary>Coarse server outcome, mapped by the client to a user-facing state/message. Transport-level failures
/// (no HTTP response reached the server) surface as <see cref="Offline"/> so the manager can enter the grace
/// path rather than treat the licence as invalid.</summary>
public enum LicenseServerStatus
{
    Ok,
    InvalidKey,
    Revoked,
    Suspended,
    Expired,
    ActivationLimit,
    HardwareMismatch,
    RateLimited,
    ServerError,
    Offline
}

/// <summary>Server response to an activation / verification / renewal: a freshly signed lease token when
/// granted, plus a status the client can act on.</summary>
public sealed record LicenseResponse(bool Success, string? Token, LicenseServerStatus Status, string? Message);

/// <summary>Server response to a session start/refresh (Palier B): the opaque session token and its short expiry
/// when granted, else a status (Revoked/Suspended/Expired/HardwareMismatch/ActivationLimit/Offline).</summary>
public sealed record SessionResponse(bool Success, string? SessionToken, DateTimeOffset? ExpiresAt, LicenseServerStatus Status, string? Message);

/// <summary>Contract the client uses to talk to the licence server. Kept abstract so the licence manager (and
/// its tests) never depend on transport; the HTTPS implementation (TLS pinning, request HMAC, nonce/timestamp,
/// signed-response verification) is a later slice.</summary>
public interface ILicenseApiClient
{
    Task<LicenseResponse> ActivateAsync(ActivationRequest request, CancellationToken ct = default);
    Task<LicenseResponse> VerifyAsync(string token, FingerprintSnapshot fingerprint, CancellationToken ct = default);
    Task<LicenseResponse> RenewAsync(string token, CancellationToken ct = default);
    Task DeactivateAsync(string token, CancellationToken ct = default);

    /// <summary>Opens a short-lived runtime session for the current licence + machine (Palier B).</summary>
    Task<SessionResponse> StartSessionAsync(string token, FingerprintSnapshot fingerprint, CancellationToken ct = default);

    /// <summary>Extends a live session; fails once the licence is revoked/suspended or the session lapsed.</summary>
    Task<SessionResponse> RefreshSessionAsync(string sessionToken, FingerprintSnapshot fingerprint, CancellationToken ct = default);
}
