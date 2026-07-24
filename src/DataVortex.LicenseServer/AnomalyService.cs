using Microsoft.EntityFrameworkCore;

namespace DataVortex.LicenseServer;

/// <summary>Flags suspicious usage for an admin to review — never auto-revokes (a false positive would punish a
/// legitimate customer). Rules: a licence bound to more distinct active devices than it is allowed, one racking up
/// hardware-mismatch rejections, and — with runtime sessions (Palier B) — one opening sessions from many distinct
/// IPs or repeatedly hitting the concurrent-seat limit (both hallmarks of a shared key hopping machines).</summary>
public sealed class AnomalyService
{
    private readonly LicenseDbContext _db;

    public AnomalyService(LicenseDbContext db) => _db = db;

    public async Task<IReadOnlyList<AnomalyReport>> DetectSharingAsync(
        int lookbackDays = 30, int mismatchThreshold = 5, int ipSlack = 4, int seatRejectionThreshold = 5)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        var reports = new List<AnomalyReport>();

        var licenses = await _db.Licenses.Include(l => l.Activations).ToListAsync();
        foreach (var l in licenses)
        {
            var activeDevices = l.Activations.Where(a => a.Active).Select(a => a.DeviceId).Distinct().Count();
            var mismatches = await _db.AuthLogs.CountAsync(x =>
                x.LicenseId == l.Id && x.Result == "hardware_mismatch" && x.At >= since);

            // Session-era signals: a shared key surfaces as many distinct IPs opening/refreshing sessions, and
            // repeated seat-limit refusals (more concurrent machines than the licence has seats).
            var sessionIps = await _db.AuthLogs
                .Where(x => x.LicenseId == l.Id && x.At >= since && x.Ip != null && x.Result == "ok"
                            && (x.Action == "session_start" || x.Action == "session_refresh"))
                .Select(x => x.Ip)
                .Distinct()
                .CountAsync();
            var seatRejections = await _db.AuthLogs.CountAsync(x =>
                x.LicenseId == l.Id && x.Action == "session_start" && x.Result == "seat_limit" && x.At >= since);

            if (activeDevices > l.MaxActivations
                || mismatches >= mismatchThreshold
                || sessionIps > l.MaxActivations + ipSlack
                || seatRejections >= seatRejectionThreshold)
                reports.Add(new AnomalyReport(l.Id, activeDevices, l.MaxActivations, mismatches, sessionIps, seatRejections));
        }
        return reports;
    }
}

public sealed record AnomalyReport(
    Guid LicenseId, int ActiveDevices, int MaxActivations, int HardwareMismatches,
    int DistinctSessionIps = 0, int SeatRejections = 0);
