using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataVortex.Licensing;

/// <summary>Outcome of verifying a lease token's signature and parsing its claims. <see cref="Valid"/> reports
/// SIGNATURE validity only — lease/licence expiry and fingerprint binding are decided by the client's manager.</summary>
public sealed record TokenVerificationResult(bool Valid, LicenseClaims? Claims, string? Error);

/// <summary>Compact signed licence token — <c>base64url(payload) + "." + base64url(signature)</c> — signed with
/// ECDSA P-256 / SHA-256 (ES256). The server holds the private key and calls <see cref="Sign"/>; the client holds
/// only public keys and calls <see cref="Verify"/>, so a token can never be forged client-side.
/// <para>ECDSA P-256 is used rather than Ed25519 because it is native on .NET and on every server stack (no
/// third-party crypto dependency). The security property is identical: the private key never leaves the server.</para></summary>
public static class LicenseToken
{
    /// <summary>Builds and signs a token from claims — SERVER side, needs the private key. Shared here so the
    /// server and the tests use one canonical wire format; the client never calls it.</summary>
    public static string Sign(LicenseClaims claims, ECDsa privateKey, string kid)
    {
        var payloadB64 = B64UrlEncode(Encoding.UTF8.GetBytes(Serialize(claims with { Kid = kid })));
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(payloadB64),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return payloadB64 + "." + B64UrlEncode(signature);
    }

    /// <summary>Verifies the signature against ANY of the supplied public keys (the ring supports rotation), then
    /// parses the claims. Trying each key avoids trusting the token's own <c>kid</c> before verification.</summary>
    public static TokenVerificationResult Verify(string token, IEnumerable<ECDsa> publicKeys)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(false, null, "jeton vide");
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1) return new(false, null, "format de jeton invalide");

        var payloadB64 = token[..dot];
        byte[] payload, signature;
        try { payload = B64UrlDecode(payloadB64); signature = B64UrlDecode(token[(dot + 1)..]); }
        catch { return new(false, null, "encodage base64url invalide"); }

        var signed = Encoding.ASCII.GetBytes(payloadB64);
        var verified = false;
        foreach (var key in publicKeys)
        {
            try
            {
                if (key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                { verified = true; break; }
            }
            catch { /* malformed key or signature length — try the next key */ }
        }
        if (!verified) return new(false, null, "signature invalide");

        try { return new(true, ParseClaims(Encoding.UTF8.GetString(payload)), null); }
        catch { return new(false, null, "claims illisibles"); }
    }

    private static string Serialize(LicenseClaims c)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["lid"] = c.LicenseId,
            ["type"] = c.Type.ToString(),
            ["feat"] = c.Features,
            ["fph"] = c.FingerprintHash,
            ["iat"] = c.IssuedAt.ToUnixTimeSeconds(),
            ["lexp"] = c.LeaseExpiresAt.ToUnixTimeSeconds(),
            ["exp"] = c.LicenseExpiresAt?.ToUnixTimeSeconds(),
            ["kid"] = c.Kid,
        });

    private static LicenseClaims ParseClaims(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var features = new List<string>();
        if (r.TryGetProperty("feat", out var f) && f.ValueKind == JsonValueKind.Array)
            foreach (var x in f.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) features.Add(x.GetString()!);

        var type = LicenseType.Trial;
        if (r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            Enum.TryParse(t.GetString(), ignoreCase: true, out type);

        return new LicenseClaims
        {
            LicenseId = Str(r, "lid"),
            Type = type,
            Features = features,
            FingerprintHash = Str(r, "fph"),
            IssuedAt = Unix(r, "iat") ?? default,
            LeaseExpiresAt = Unix(r, "lexp") ?? default,
            LicenseExpiresAt = Unix(r, "exp"),
            Kid = r.TryGetProperty("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
        };
    }

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static DateTimeOffset? Unix(JsonElement r, string name)
        => r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
            ? DateTimeOffset.FromUnixTimeSeconds(v) : null;

    internal static string B64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] B64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

/// <summary>Holds a public-key ring (SPKI base64) and verifies tokens against it. Used by the client (embedded
/// keys) and anywhere else that needs to validate a token without the private key.</summary>
public sealed class LicenseTokenVerifier
{
    private readonly string[] _publicKeysSpkiB64;

    /// <param name="publicKeysBase64Spki">Base64 of each key's SubjectPublicKeyInfo (SPKI) DER — the active key
    /// plus any "next" key during a rotation window.</param>
    public LicenseTokenVerifier(IEnumerable<string> publicKeysBase64Spki)
        => _publicKeysSpkiB64 = publicKeysBase64Spki.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

    public TokenVerificationResult Verify(string token)
    {
        var keys = new List<ECDsa>();
        try
        {
            foreach (var b64 in _publicKeysSpkiB64)
            {
                try
                {
                    var ec = ECDsa.Create();
                    ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
                    keys.Add(ec);
                }
                catch { /* skip a malformed embedded key */ }
            }
            return keys.Count == 0
                ? new(false, null, "aucune clé publique valide")
                : LicenseToken.Verify(token, keys);
        }
        finally { foreach (var k in keys) k.Dispose(); }
    }
}
