namespace DataVortex.Core.Licensing;

/// <summary>Build-time licensing constants. The server public keys are EMBEDDED here (not in settings) so they
/// can't be swapped via a config file — that's what makes a forged / self-hosted licence server useless against
/// the client. Fill <see cref="PublicKeys"/> with the server's key(s) — base64 of the ECDSA P-256
/// SubjectPublicKeyInfo — before enabling licensing in production.</summary>
public static class LicensingConstants
{
    /// <summary>Embedded server public keys (SPKI, base64): the active key plus any "next" key during rotation.
    /// EMPTY until the licence server is provisioned — while empty, activation always fails closed (no token can
    /// be verified), which is the safe default.</summary>
    public static readonly IReadOnlyList<string> PublicKeys = Array.Empty<string>();

    /// <summary>Default licence-server base URL. Overridable via <c>AppSettings.LicenseServerUrl</c>.</summary>
    public const string DefaultServerUrl = "https://licences.datavortex.app/";

    /// <summary>Shared app-authentication key (HMAC), added to each request as X-Signature/X-Timestamp/X-Nonce and
    /// matched by the server's <c>Security:AppHmacKey</c>. EMPTY disables request signing (dev). Embed a real key
    /// for production — a defence-in-depth layer (bars requests forged outside the app), not a root secret.</summary>
    public const string AppHmacKey = "";

    /// <summary>SPKI pin of the server's TLS certificate (base64 SHA-256 of its SubjectPublicKeyInfo). When set,
    /// the client rejects any TLS connection whose leaf public key doesn't match — defeating MITM via a rogue CA.
    /// EMPTY disables pinning (dev / before the cert exists). Keep a backup pin to survive certificate rotation.</summary>
    public const string ServerCertSpkiPin = "";
}
