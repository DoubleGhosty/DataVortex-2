using Microsoft.EntityFrameworkCore;

namespace DataVortex.LicenseServer;

/// <summary>Flags suspicious usage for an admin to review — never auto-revokes (a false positive would punish a
/// legitimate customer). Current rule set: a licence bound to more distinct active devices than it is allowed, or
/// one racking up hardware-mismatch rejections (a hallmark of a shared key hopping machines).</summary>
public sealed class AnomalyService
{
    private readonly LicenseDbContext _db;

    public AnomalyService(LicenseDbContext db) => _db = db;

    public async Task<IReadOnlyList<AnomalyReport>> DetectSharingAsync(int lookbackDays = 30, int mismatchThreshold = 5)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        var reports = new List<AnomalyReport>();

        var licenses = await _db.Licenses.Include(l => l.Activations).ToListAsync();
        foreach (var l in licenses)
        {
            var activeDevices = l.Activations.Where(a => a.Active).Select(a => a.DeviceId).Distinct().Count();
            var mismatches = await _db.AuthLogs.CountAsync(x =>
                x.LicenseId == l.Id && x.Result == "hardware_mismatch" && x.At >= since);

            if (activeDevices > l.MaxActivations || mismatches >= mismatchThreshold)
                reports.Add(new AnomalyReport(l.Id, activeDevices, l.MaxActivations, mismatches));
        }
        return reports;
    }
}

public sealed record AnomalyReport(Guid LicenseId, int ActiveDevices, int MaxActivations, int HardwareMismatches);
