using System.Security.Cryptography;
using System.Text;

namespace DataVortex.Core.Security;

/// <summary>
/// Thin wrapper over the Windows Data Protection API (DPAPI), scoped to the current user.
/// Used to protect the Telegram api_hash at rest so it never touches disk in plaintext and can only
/// be decrypted by the same Windows account on the same machine.
/// </summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DataVortex.v1.credential");

    public static byte[] Protect(string plaintext)
        => ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public static string Unprotect(byte[] ciphertext)
        => Encoding.UTF8.GetString(ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser));
}
