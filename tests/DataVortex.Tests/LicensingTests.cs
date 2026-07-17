using System.Security.Cryptography;
using DataVortex.Core.Licensing;
using DataVortex.Licensing;
using Xunit;

namespace DataVortex.Tests;

/// <summary>Covers the two backend-independent foundations of the client licence module: the fuzzy hardware
/// fingerprint scoring, and ECDSA P-256 lease-token verification (valid / tampered / wrong key / key ring).</summary>
public sealed class LicensingTests
{
    // ---------------------------------------------------------------- fingerprint scoring

    private static Fingerprint Fp(params (string id, string val, int w)[] comps)
        => new(comps.Select(c => new FingerprintComponent(c.id, c.val, c.w)));

    [Fact]
    public void IdenticalFingerprints_scoreOne_andShareHash()
    {
        var a = Fp(("machine-name", "PC1", 1), ("mac", "AABBCCDDEEFF", 3), ("os", "Win", 1));
        var b = Fp(("machine-name", "PC1", 1), ("mac", "AABBCCDDEEFF", 3), ("os", "Win", 1));
        Assert.Equal(1.0, a.MatchScore(b), 3);
        Assert.True(a.Matches(b, 0.8));
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void LowWeightChange_stillMatches()
    {
        // OS changed (weight 1 of 6) — a Windows feature update must not break the licence.
        var reference = Fp(("machine-name", "PC1", 1), ("mac", "AABBCC", 3), ("os", "Win10", 1), ("cpu-count", "8", 1));
        var changed = Fp(("machine-name", "PC1", 1), ("mac", "AABBCC", 3), ("os", "Win11", 1), ("cpu-count", "8", 1));
        Assert.True(changed.MatchScore(reference) >= 0.8);
        Assert.True(changed.Matches(reference, 0.75));
        Assert.NotEqual(reference.Hash, changed.Hash);
    }

    [Fact]
    public void HighWeightChange_failsStrictThreshold()
    {
        // The MAC (weight 3 of 6) differs → 3/6 = 0.5: this reads as a different machine at a strict threshold.
        var reference = Fp(("machine-name", "PC1", 1), ("mac", "AABBCC", 3), ("os", "Win", 1), ("cpu-count", "8", 1));
        var moved = Fp(("machine-name", "PC1", 1), ("mac", "ZZZZZZ", 3), ("os", "Win", 1), ("cpu-count", "8", 1));
        Assert.Equal(0.5, moved.MatchScore(reference), 3);
        Assert.False(moved.Matches(reference, 0.75));
        Assert.True(moved.Matches(reference, 0.5));
    }

    [Fact]
    public void MissingComponent_isNotCounted()
    {
        var reference = Fp(("machine-name", "PC1", 1), ("mac", "AABBCC", 3));
        var partial = Fp(("machine-name", "PC1", 1)); // no mac reported
        Assert.Equal(1.0, partial.MatchScore(reference), 3);   // all of partial's weight matches
        Assert.Equal(0.25, reference.MatchScore(partial), 3);  // reference's mac (3/4) has no counterpart
    }

    [Fact]
    public void Collect_isNonEmpty_andStableAcrossCalls()
    {
        var a = HardwareFingerprint.Collect();
        var b = HardwareFingerprint.Collect();
        Assert.NotEmpty(a.Components);
        Assert.Equal(a.Hash, b.Hash);
    }

    // ---------------------------------------------------------------- token verification

    private static LicenseClaims SampleClaims() => new()
    {
        LicenseId = "LIC-123",
        Type = LicenseType.Pro,
        Features = new[] { "checker", "backfill" },
        FingerprintHash = "abc123",
        IssuedAt = DateTimeOffset.UtcNow,
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
        LicenseExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
    };

    private static string Spki(ECDsa ec) => Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());

    [Fact]
    public void ValidToken_verifies_andRoundtripsClaims()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LicenseToken.Sign(SampleClaims(), key, "k1");

        var res = new LicenseTokenVerifier(new[] { Spki(key) }).Verify(token);

        Assert.True(res.Valid);
        Assert.NotNull(res.Claims);
        Assert.Equal("LIC-123", res.Claims!.LicenseId);
        Assert.Equal(LicenseType.Pro, res.Claims.Type);
        Assert.True(res.Claims.HasFeature("checker"));
        Assert.False(res.Claims.HasFeature("admin"));
        Assert.Equal("k1", res.Claims.Kid);
        Assert.True(res.Claims.IsLeaseValid(DateTimeOffset.UtcNow));
        Assert.False(res.Claims.IsLicenseExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TamperedPayload_fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LicenseToken.Sign(SampleClaims(), key, "k1");
        var flipped = (token[0] == 'A' ? 'B' : 'A') + token[1..]; // corrupt the payload segment

        Assert.False(new LicenseTokenVerifier(new[] { Spki(key) }).Verify(flipped).Valid);
    }

    [Fact]
    public void WrongKey_fails()
    {
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LicenseToken.Sign(SampleClaims(), signing, "k1");

        Assert.False(new LicenseTokenVerifier(new[] { Spki(other) }).Verify(token).Valid);
    }

    [Fact]
    public void KeyRing_acceptsTokenSignedByAnyKnownKey()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LicenseToken.Sign(SampleClaims(), newKey, "k2"); // signed by the rotated-in key

        var verifier = new LicenseTokenVerifier(new[] { Spki(oldKey), Spki(newKey) });
        Assert.True(verifier.Verify(token).Valid);
    }

    [Fact]
    public void GarbageToken_failsCleanly()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new LicenseTokenVerifier(new[] { Spki(key) });
        Assert.False(verifier.Verify("").Valid);
        Assert.False(verifier.Verify("not-a-token").Valid);
        Assert.False(verifier.Verify("aaaa.bbbb").Valid);
    }
}
