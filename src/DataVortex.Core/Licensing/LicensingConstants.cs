using System.Linq;
using System.Reflection;

namespace DataVortex.Core.Licensing;

/// <summary>Build-time licensing constants. The server public keys are EMBEDDED here (not in settings) so they
/// can't be swapped via a config file — that's what makes a forged / self-hosted licence server useless against
/// the client. <see cref="PublicKeys"/> holds the server's key(s) — base64 of the ECDSA P-256
/// SubjectPublicKeyInfo.</summary>
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
        // Production licence-server signing key (ECDSA P-256 SPKI, base64). The matching PRIVATE key lives only on
        // the VPS (never in this repo). Safe to embed: it only VERIFIES lease tokens, it cannot sign them.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESGHqgoiR8Jbrp1x8K5vaPINoYjw511Yxx8XAasyD1KK3L4hKVCU2FBYM/b+yHM41MRqLkwVHkdgxnyCtef+WZA==",
    };

    /// <summary>Default licence-server base URL. Overridable via <c>AppSettings.LicenseServerUrl</c>.</summary>
    public const string DefaultServerUrl = "https://217.128.139.122:8443";

    /// <summary>Shared app-authentication key (HMAC), added to each request as X-Signature/X-Timestamp/X-Nonce and
    /// matched by the server's <c>Security:AppHmacKey</c>. It is a SECRET and is <b>injected at build time, never
    /// committed</b>: pass <c>-p:DvHmacKey=&lt;key&gt;</c> (publish.ps1 does this) or set <c>DV_HMAC_KEY</c>; the
    /// value is stamped into this assembly's <c>DvHmacKey</c> metadata (see DataVortex.Core.csproj) and read back
    /// here. Empty (dev / no key) disables request signing. Defence in depth (bars requests forged outside the app
    /// + replays), not a root secret.</summary>
    public static readonly string AppHmacKey =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DvHmacKey")?.Value ?? "";

    /// <summary>SPKI pin of the server's TLS certificate (base64 SHA-256 of its SubjectPublicKeyInfo). The client
    /// rejects any TLS connection whose leaf public key doesn't match — so a self-signed server cert (bare IP, no
    /// public CA) is trusted by pin alone, and a rogue/MITM cert is rejected. Rotate together with the server cert
    /// (keep a backup pin to survive rotation).</summary>
    public const string ServerCertSpkiPin = "vbzovaDrVnQdcvbpcKNqwsYUoUzf7aHh7O19xhl3NXY=";
}
