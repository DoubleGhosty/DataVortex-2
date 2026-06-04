using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Storage;

/// <summary>
/// Safety-net disk cleanup. The processing pipeline already deletes each archive + its extracted *.txt right
/// after a file is handled, but if that ever fails (crash, lock, bug) the residue would pile up. This timer
/// periodically removes files left in <c>downloads/</c> and <c>extracted/</c> that are older than
/// <see cref="AppSettings.CleanupResidueMinutes"/>. Files still in use (e.g. an in-flight download) are
/// locked, so <c>File.Delete</c> throws and they are skipped — never deleting an active file.
/// </summary>
public sealed class CleanupService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<CleanupService> _log;
    private Timer? _timer;

    public CleanupService(AppPaths paths, ISettingsService settings, ILogger<CleanupService> log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        // First sweep shortly after startup, then every 15 minutes. Timer callback is guarded so a fault
        // never kills the process.
        _timer = new Timer(_ => SafeSweep(), null, TimeSpan.FromMinutes(2), Interval);
    }

    private void SafeSweep()
    {
        try { Sweep(); }
        catch (Exception ex) { _log.LogWarning(ex, "Cleanup sweep failed"); }
    }

    private void Sweep()
    {
        var minutes = _settings.Current.CleanupResidueMinutes;
        if (minutes <= 0) return; // disabled

        var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
        int deleted = 0;
        long freed = 0;

        foreach (var root in new[] { _paths.Downloads, _paths.Extracted })
        {
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc > cutoff) continue; // too recent — may still be in use
                    var size = fi.Length;
                    File.Delete(file); // throws if locked (active download/extraction) → caught + skipped
                    deleted++;
                    freed += size;
                }
                catch { /* locked / in use / already gone — skip */ }
            }

            RemoveEmptyDirectories(root);
        }

        if (deleted > 0)
            _log.LogInformation("Cleanup: removed {Count} residual file(s), freed {MB:N1} MB", deleted, freed / 1024.0 / 1024.0);
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            // Deepest first so parents become empty too.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* not empty / locked — skip */ }
            }
        }
        catch { }
    }

    public void Dispose() => _timer?.Dispose();
}
