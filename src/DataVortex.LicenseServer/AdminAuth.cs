using System.Security.Cryptography;
using System.Text;

namespace DataVortex.LicenseServer;

/// <summary>Admin credential primitives: PBKDF2 password hashing, TOTP (RFC 6238) verification, and Base32 for
/// the shared TOTP secret. All native — no third-party crypto dependency.</summary>
public static class AdminAuth
{
    private const int Iterations = 100_000;
    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; // RFC 4648, authenticator-compatible

    // ---- password (PBKDF2-SHA256) ----

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    // ---- TOTP (RFC 6238, 30s step, 6 digits, SHA-1 per the standard) ----

    public static string GenerateTotpSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static bool VerifyTotp(string base32Secret, string code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        byte[] key;
        try { key = Base32Decode(base32Secret); } catch { return false; }

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (long i = -window; i <= window; i++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeTotp(key, step + i)), Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    private static string ComputeTotp(byte[] key, long counter)
    {
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }
#pragma warning disable CA5350 // TOTP (RFC 6238) is defined over HMAC-SHA1; required for authenticator compatibility.
        var hash = HMACSHA1.HashData(key, msg);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0f;
        int bin = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (bin % 1_000_000).ToString("D6");
    }

    // ---- Base32 ----

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(B32[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(B32[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        s = s.TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int bits = 0, value = 0;
        foreach (var c in s)
        {
            var idx = B32.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((value >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
