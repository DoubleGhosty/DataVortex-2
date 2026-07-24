using System.Security.Cryptography;
using DataVortex.Licensing;
using Microsoft.EntityFrameworkCore;

namespace DataVortex.LicenseServer;

/// <summary>Owns the server signing key(s): generates one on first run, signs lease tokens with it, and exposes
/// the public key ring for the client (<c>/keys</c>) and for verifying tokens the client returns.
/// <para><b>MVP caveat:</b> the private key is kept in the database. In production it must live in a KMS/HSM and
/// never be exportable — <see cref="Sign"/> would then call the KMS instead of a local ECDsa.</para></summary>
public sealed class SigningService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly object _gate = new();
    private (string Kid, ECDsa Key)? _active;

    public SigningService(IServiceScopeFactory scopes, IConfiguration config)
    {
        _scopes = scopes;
        _config = config;
    }

    /// <summary>Loads the active signing key, creating one on first run. Call once at startup.</summary>
    public async Task InitializeAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var rec = await db.SigningKeys.FirstOrDefaultAsync(k => k.Active);
        if (rec is null)
        {
            // Prefer an operator-provided key (Signing:PrivateKeyPkcs8) so the client can embed the matching
            // public key BEFORE the server ever runs; otherwise generate a fresh one (dev / unmanaged).
            var providedPriv = _config["Signing:PrivateKeyPkcs8"];
            if (!string.IsNullOrWhiteSpace(providedPriv))
            {
                using var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(providedPriv.Trim()), out _);
                rec = new SigningKeyRecord
                {
                    Kid = _config["Signing:Kid"] is { Length: > 0 } k ? k : "k" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    PublicKeySpki = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
                    PrivateKeyPkcs8 = providedPriv.Trim(),
                    Active = true,
                };
            }
            else
            {
                using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                rec = new SigningKeyRecord
                {
                    Kid = "k" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    PublicKeySpki = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
                    PrivateKeyPkcs8 = Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
                    Active = true,
                };
            }
            db.SigningKeys.Add(rec);
            await db.SaveChangesAsync();
        }

        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(rec.PrivateKeyPkcs8), out _);
        lock (_gate) _active = (rec.Kid, key);
    }

    /// <summary>Signs claims with the active key. Serialised (ECDsa isn't guaranteed thread-safe) — fine for the
    /// modest throughput of a licence server.</summary>
    public string Sign(LicenseClaims claims)
    {
        lock (_gate)
        {
            var a = _active ?? throw new InvalidOperationException("Signing key not initialised.");
            return LicenseToken.Sign(claims, a.Key, a.Kid);
        }
    }

    /// <summary>All known public keys (SPKI base64) — the client embeds these; also used to verify returned tokens.</summary>
    public async Task<IReadOnlyList<string>> PublicKeysAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SigningKeys.Select(k => k.PublicKeySpki).ToListAsync();
    }

    public async Task<LicenseTokenVerifier> VerifierAsync() => new(await PublicKeysAsync());
}
