using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>One weighted component of a hardware fingerprint. The raw <see cref="Value"/> never leaves the
/// machine — only its hash is compared or sent to the server.</summary>
public sealed record FingerprintComponent(string Id, string Value, int Weight)
{
    public string ValueHash => LicenseHash.Compute(Value);
    public ComponentHash ToHash() => new(Id, ValueHash, Weight);
}

/// <summary>A set of weighted components identifying a machine (client-side collection form). Convert to a
/// <see cref="FingerprintSnapshot"/> (shared) to transmit or store it. Matching is fuzzy — see
/// <see cref="FingerprintSnapshot.MatchScore"/>.</summary>
public sealed class Fingerprint
{
    public IReadOnlyList<FingerprintComponent> Components { get; }

    public Fingerprint(IEnumerable<FingerprintComponent> components)
        => Components = components.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToArray();

    public FingerprintSnapshot Snapshot() => new(Components.Select(c => c.ToHash()));

    public string Hash => Snapshot().Hash;

    public double MatchScore(Fingerprint reference) => Snapshot().MatchScore(reference.Snapshot());
    public double MatchScore(FingerprintSnapshot reference) => Snapshot().MatchScore(reference);
    public bool Matches(Fingerprint reference, double threshold) => MatchScore(reference) >= threshold;
    public bool Matches(FingerprintSnapshot reference, double threshold) => MatchScore(reference) >= threshold;
}

/// <summary>Collects a hardware fingerprint from the current machine. This first cut uses only dependency-free,
/// framework-native signals; stronger, higher-weight components (Windows MachineGuid via registry, baseboard /
/// disk serials via WMI, TPM endorsement key) plug in later as additional <see cref="FingerprintComponent"/>s
/// without touching the scoring logic.</summary>
public static class HardwareFingerprint
{
    public static Fingerprint Collect()
    {
        var components = new List<FingerprintComponent>
        {
            new("machine-name", Environment.MachineName, 1),
            new("os", RuntimeInformation.OSDescription, 1),
            new("cpu-arch", RuntimeInformation.OSArchitecture.ToString(), 1),
            new("cpu-count", Environment.ProcessorCount.ToString(), 1),
        };
        var mac = PrimaryMacAddress();
        if (mac is not null) components.Add(new("mac", mac, 3));
        return new Fingerprint(components);
    }

    /// <summary>MAC of the first non-virtual, non-loopback interface — a stable, discriminating signal. Virtual
    /// adapters (VPN / VM / Hyper-V / TAP …) are skipped so toggling a VPN doesn't change the fingerprint.</summary>
    private static string? PrimaryMacAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                  && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                         .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var label = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (label.Contains("virtual") || label.Contains("vmware") || label.Contains("hyper-v")
                    || label.Contains("vethernet") || label.Contains("vpn") || label.Contains("tap")
                    || label.Contains("loopback") || label.Contains("pseudo")) continue;

                var mac = ni.GetPhysicalAddress().ToString();
                if (!string.IsNullOrEmpty(mac) && mac != "000000000000") return mac;
            }
        }
        catch { /* best-effort — the licence still works with the remaining components */ }
        return null;
    }
}
