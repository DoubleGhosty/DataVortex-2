namespace DataVortex.Licensing;

/// <summary>The commercial tiers. Each maps to a server-side policy (slots, duration, offline, features,
/// fingerprint tolerance).</summary>
public enum LicenseType { Trial, Standard, Pro, Enterprise, Lifetime }

/// <summary>Claims carried by the signed lease token — the payload the server signs and the client verifies.
/// Shared by client and server so both agree on the exact shape.</summary>
public sealed record LicenseClaims
{
    /// <summary>Opaque server-side identifier of the licence (never the raw licence key).</summary>
    public string LicenseId { get; init; } = "";

    public LicenseType Type { get; init; }

    /// <summary>Feature flags this licence unlocks (server-authoritative for sensitive ones).</summary>
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

    /// <summary>Hash of the hardware fingerprint this activation is bound to (privacy-preserving).</summary>
    public string FingerprintHash { get; init; } = "";

    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>End of the short-lived lease. The client must renew before this to stay online-verified.</summary>
    public DateTimeOffset LeaseExpiresAt { get; init; }

    /// <summary>End of the licence's own validity. <c>null</c> for a perpetual (Lifetime) licence.</summary>
    public DateTimeOffset? LicenseExpiresAt { get; init; }

    /// <summary>Identifier of the signing key that produced the token (supports key rotation).</summary>
    public string? Kid { get; init; }

    public bool HasFeature(string feature)
    {
        for (int i = 0; i < Features.Count; i++)
            if (string.Equals(Features[i], feature, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True while the (short-lived) lease has not expired at <paramref name="now"/>.</summary>
    public bool IsLeaseValid(DateTimeOffset now) => now < LeaseExpiresAt;

    /// <summary>True once the licence itself has passed its expiry (never for a perpetual licence).</summary>
    public bool IsLicenseExpired(DateTimeOffset now) => LicenseExpiresAt is { } exp && now >= exp;
}
