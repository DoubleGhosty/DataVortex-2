using System.Security.Cryptography;
using System.Text;

namespace DataVortex.Licensing;

/// <summary>Canonical hashing for licensing (SHA-256, hex, trimmed input) — shared so client and server derive
/// identical component/fingerprint hashes.</summary>
public static class LicenseHash
{
    public static string Compute(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
}

/// <summary>A single hashed component (id, value-hash, weight) — the privacy-preserving form that is stored and
/// transmitted. The raw value never appears here.</summary>
public sealed record ComponentHash(string Id, string ValueHash, int Weight);

/// <summary>Serialisable snapshot of a hardware fingerprint: the per-component value hashes + weights. The client
/// sends this to the server, which does the authoritative fuzzy match. Matching is deliberately weighted so
/// replacing one part doesn't break an otherwise-identical machine while a wholesale change does.</summary>
public sealed class FingerprintSnapshot
{
    public IReadOnlyList<ComponentHash> Components { get; }

    public FingerprintSnapshot(IEnumerable<ComponentHash> components)
        => Components = components.ToArray();

    /// <summary>Compact digest over the sorted id:value-hash pairs — a stable id for the whole snapshot.</summary>
    public string Hash
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var c in Components.OrderBy(c => c.Id, StringComparer.Ordinal))
                sb.Append(c.Id).Append(':').Append(c.ValueHash).Append(';');
            return LicenseHash.Compute(sb.ToString());
        }
    }

    /// <summary>Weighted match score in [0,1] of THIS snapshot against a reference: summed weight of components
    /// whose id exists in <paramref name="reference"/> with the same value-hash, over this snapshot's total
    /// weight. A missing or differing component simply doesn't count.</summary>
    public double MatchScore(FingerprintSnapshot reference)
    {
        if (Components.Count == 0) return 0;
        var refById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in reference.Components) refById[c.Id] = c.ValueHash;

        int total = 0, matched = 0;
        foreach (var c in Components)
        {
            total += c.Weight;
            if (refById.TryGetValue(c.Id, out var h) && h == c.ValueHash) matched += c.Weight;
        }
        return total == 0 ? 0 : (double)matched / total;
    }

    public bool Matches(FingerprintSnapshot reference, double threshold) => MatchScore(reference) >= threshold;
}
