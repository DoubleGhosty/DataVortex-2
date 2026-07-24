using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataVortex.Licensing;

/// <summary>Authenticated encryption for the <see cref="OperationalRecipe"/> (Palier C). The server seals the
/// recipe under a key derived from the live session key; the client can only open it while it holds that session
/// key (in memory). AES-256-GCM gives confidentiality + integrity: a tampered blob or the wrong key yields
/// <c>null</c> (fail closed), so a client with no valid session simply never gets a usable recipe — there is no
/// constant to patch, the data does not exist locally.</summary>
public static class RecipeCrypto
{
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;     // 128-bit auth tag
    private const int KeySize = 32;     // AES-256
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("datavortex/passculture-recipe/v1");

    /// <summary>Seals a recipe under a session key. Output layout: base64( nonce | ciphertext | tag ).</summary>
    public static string Protect(OperationalRecipe recipe, byte[] sessionKey)
    {
        var key = DeriveKey(sessionKey);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(recipe);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plaintext, cipher, tag);

        var outb = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, outb, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, outb, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, outb, NonceSize + cipher.Length, TagSize);
        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(outb);
    }

    /// <summary>Opens a sealed recipe. Returns <c>null</c> on any failure (wrong key, tampered, malformed) — the
    /// caller then has no recipe and the checker builds nothing.</summary>
    public static OperationalRecipe? Unprotect(string? blob, byte[] sessionKey)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        byte[]? key = null;
        try
        {
            var raw = Convert.FromBase64String(blob);
            if (raw.Length < NonceSize + TagSize) return null;
            key = DeriveKey(sessionKey);
            var nonce = raw.AsSpan(0, NonceSize);
            var cipher = raw.AsSpan(NonceSize, raw.Length - NonceSize - TagSize);
            var tag = raw.AsSpan(raw.Length - TagSize, TagSize);
            var plaintext = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Decrypt(nonce, cipher, tag, plaintext);
            return JsonSerializer.Deserialize<OperationalRecipe>(plaintext);
        }
        catch
        {
            return null; // fail closed
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(byte[] sessionKey)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, salt: null, info: Info);
}
