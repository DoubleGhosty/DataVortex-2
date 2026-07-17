using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>What the client persists locally between runs: the signed lease token, the hardware reference it is
/// bound to (for local change detection), and the last time we trusted a server clock (anti-rollback).</summary>
public sealed record LicenseStoreData(string Token, FingerprintSnapshot Reference, DateTimeOffset LastSeen);

/// <summary>Local persistence of the licence state. Abstracted so the manager and its tests don't depend on the
/// filesystem/DPAPI.</summary>
public interface ILicenseStore
{
    LicenseStoreData? Load();
    void Save(LicenseStoreData data);
    void Clear();
}

/// <summary>Stores the licence state encrypted at rest with DPAPI (per Windows user). Confidentiality comes from
/// DPAPI; integrity comes for free from the token's server signature (any edit invalidates it). The stored data
/// is small JSON — the token and the reference component hashes (never raw hardware values).</summary>
public sealed class DpapiLicenseStore : ILicenseStore
{
    private readonly string _path;

    public DpapiLicenseStore(string path) => _path = path;

    public LicenseStoreData? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return Deserialize(Encoding.UTF8.GetString(plain));
        }
        catch { return null; } // corrupt / not decryptable by this user → treat as not activated
    }

    public void Save(LicenseStoreData data)
    {
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(Serialize(data)), null, DataProtectionScope.CurrentUser);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(_path, cipher);
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
    }

    private static string Serialize(LicenseStoreData d)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["token"] = d.Token,
            ["ref"] = d.Reference.Components.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["h"] = c.ValueHash, ["w"] = c.Weight
            }),
            ["seen"] = d.LastSeen.ToUnixTimeSeconds(),
        });

    private static LicenseStoreData? Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var token = r.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";

        var comps = new List<ComponentHash>();
        if (r.TryGetProperty("ref", out var refs) && refs.ValueKind == JsonValueKind.Array)
            foreach (var c in refs.EnumerateArray())
                comps.Add(new ComponentHash(
                    c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    c.TryGetProperty("h", out var h) ? h.GetString() ?? "" : "",
                    c.TryGetProperty("w", out var w) && w.TryGetInt32(out var wi) ? wi : 0));

        var seen = r.TryGetProperty("seen", out var s) && s.TryGetInt64(out var v)
            ? DateTimeOffset.FromUnixTimeSeconds(v) : DateTimeOffset.MinValue;

        return new LicenseStoreData(token, new FingerprintSnapshot(comps), seen);
    }
}
