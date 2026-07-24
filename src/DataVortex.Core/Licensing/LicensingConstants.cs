namespace DataVortex.Core.Licensing;

/// <summary>Build-time licensing constants. The server public keys are EMBEDDED here (not in settings) so they
/// can't be swapped via a config file — that's what makes a forged / self-hosted licence server useless against
/// the client. Fill <see cref="PublicKeys"/> with the server's key(s) — base64 of the ECDSA P-256
/// SubjectPublicKeyInfo — before enabling licensing in production.</summary>
public static class LicensingConstants
{
    // NOTE: there is deliberately NO "LicensingEnforced" flag any more. A single bool that turns licensing on/off is
    // a constant-foldable one-line patch (or, as a setting, a one-character edit). Enforcement is now tied to the
    // BUILD: a Release build always runs the licence gate (App startup), a Debug build compiles a dev bypass in its
    // place. There is no in-binary switch to flip in the shipped product.

    /// <summary>Embedded server public keys (SPKI, base64): the active key plus any "next" key during rotation.
    /// EMPTY until the licence server is provisioned — while empty, activation always fails closed (no token can
    /// be verified), which is the safe default.</summary>
    public static readonly IReadOnlyList<string> PublicKeys = new[]
    {
        // LOCAL TEST server key. Replace with the production server's /keys value before shipping.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtDqMd3jYs/Ke83iz/Ut+BGV2Ut+Ed9KY/vbQiSlwIwC40vo+xAwsLMXN8CtvVooCikCD7Eh7RGLXlppWKND2dw==",
    };

    /// <summary>Default licence-server base URL. Overridable via <c>AppSettings.LicenseServerUrl</c>.</summary>
    public const string DefaultServerUrl = "http://localhost:5000"; // LOCAL TEST — restore the prod URL before shipping

    /// <summary>Shared app-authentication key (HMAC), added to each request as X-Signature/X-Timestamp/X-Nonce and
    /// matched by the server's <c>Security:AppHmacKey</c>. EMPTY disables request signing. The mechanism (client
    /// <c>SignRequest</c> + server <c>RequestAuth</c>) is implemented and validated (unsigned ⇒ 401, correctly
    /// signed ⇒ accepted). To ENABLE it, INJECT a real 32-byte key here at BUILD time (CI secret) and set the
    /// server's <c>Security:AppHmacKey</c> to the same value — never commit the key in clear (see §8). Defence in
    /// depth (bars requests forged outside the app + replays), not a root secret.</summary>
    public const string AppHmacKey = "";

    /// <summary>SPKI pin of the server's TLS certificate (base64 SHA-256 of its SubjectPublicKeyInfo). When set,
    /// the client rejects any TLS connection whose leaf public key doesn't match — defeating MITM via a rogue CA.
    /// EMPTY disables pinning (dev / before the cert exists). Keep a backup pin to survive certificate rotation.</summary>
    public const string ServerCertSpkiPin = "";
}
