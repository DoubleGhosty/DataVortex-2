using System.Security.Cryptography;
using System.Text;
using DataVortex.Licensing;

namespace DataVortex.LicenseServer;

/// <summary>Generates and hashes licence keys. Keys are high-entropy (~100 bits) so brute force is infeasible;
/// only the hash is ever stored.</summary>
public static class LicenseKeys
{
    // Crockford Base32 (no I, L, O, U) — 32 symbols, so a byte % 32 has no modulo bias (256 is a multiple of 32).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Grouped key of the form <c>XXXXX-XXXXX-XXXXX-XXXXX</c> (20 symbols = 100 bits).</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(23);
        for (int i = 0; i < 20; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(Alphabet[bytes[i] % Alphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>Normalises (strip dashes, upper-case) then hashes a key for storage/lookup.</summary>
    public static string NormalizeAndHash(string key)
        => LicenseHash.Compute(key.Replace("-", "").Trim().ToUpperInvariant());
}
