using System.Security.Cryptography;

namespace DataVortex.Core.Security;

/// <summary>Persists the Telegram api_hash DPAPI-encrypted on disk (current-user scope).</summary>
public sealed class CredentialStore
{
    private readonly string _path;
    public CredentialStore(string path) => _path = path;

    public bool HasApiHash => File.Exists(_path);

    public void SaveApiHash(string apiHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Dpapi.Protect(apiHash));
    }

    public string? LoadApiHash()
    {
        try
        {
            return File.Exists(_path) ? Dpapi.Unprotect(File.ReadAllBytes(_path)) : null;
        }
        catch (CryptographicException)
        {
            // Protected by a different user / corrupted — force re-entry.
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
