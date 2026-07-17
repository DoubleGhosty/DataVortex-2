using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace DataVortex.LicenseServer;

/// <summary>Validates the per-request app authentication: an HMAC over <c>timestamp.nonce.body</c> with the shared
/// app key, a timestamp within a tolerance window, and a single-use nonce. Together these stop requests forged
/// outside the app and block replays. TLS still carries confidentiality; this is the app-identity + anti-replay
/// layer on top.</summary>
public static class RequestAuth
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    public static bool Validate(HttpContext ctx, string body, byte[] key, IMemoryCache nonces)
    {
        var ts = ctx.Request.Headers["X-Timestamp"].ToString();
        var nonce = ctx.Request.Headers["X-Nonce"].ToString();
        var sig = ctx.Request.Headers["X-Signature"].ToString();
        if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(sig)) return false;

        if (!long.TryParse(ts, out var unix)) return false;
        var delta = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
        if (delta > Skew || delta < -Skew) return false; // outside the anti-replay window

        var cacheKey = "nonce:" + nonce;
        if (nonces.TryGetValue(cacheKey, out _)) return false; // already used → replay

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ts + "." + nonce + "." + body));
        byte[] provided;
        try { provided = Convert.FromBase64String(sig); }
        catch { return false; }

        if (!CryptographicOperations.FixedTimeEquals(expected, provided)) return false;

        nonces.Set(cacheKey, true, NonceTtl);
        return true;
    }
}
