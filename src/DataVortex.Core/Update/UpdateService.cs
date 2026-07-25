using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Updates;

/// <summary>A newer build available from the distribution host.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string? Notes, long Size);

public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>Reads the distribution manifest; returns the newest build only if it is newer than the running
    /// version and carries a download URL. Returns <c>null</c> otherwise (already up to date / offline).</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Downloads the new exe and launches an external updater that waits for this process to exit, swaps
    /// the executable and relaunches it. Returns true once the updater is running — the caller should then shut the
    /// app down. Self-update only works on a published single-file build.</summary>
    Task<bool> PrepareAndLaunchUpdaterAsync(UpdateInfo info, CancellationToken ct = default);
}

/// <summary>
/// Self-updater driven by a small JSON manifest hosted next to the exe on the distribution host (a Cloudflare R2
/// public bucket). <c>latest.json</c> declares the newest version + a direct download URL; the running build
/// compares it against its own embedded version. Each release is a version-named exe (DataVortex-X.Y.Z.exe) so its
/// URL is immutable — no stale-CDN-cache surprises on update. The swap is done by a tiny generated .bat so the
/// running exe can be replaced. No account/token needed (public bucket).
/// </summary>
public sealed class ManifestUpdateService : IUpdateService
{
    // Public distribution bucket. Change this if you move hosts. The manifest lives at <BaseUrl>/latest.json.
    private const string BaseUrl = "https://pub-564be2f53b364ef382926b5afb36fea0.r2.dev";
    private const string ManifestUrl = BaseUrl + "/latest.json";

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<ManifestUpdateService> _log;

    public Version CurrentVersion { get; } =
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public ManifestUpdateService(HttpClient http, AppPaths paths, ILogger<ManifestUpdateService> log)
    {
        _http = http;
        _paths = paths;
        _log = log;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DataVortex-Updater");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // Cache-bust so a check is never served a stale manifest from the CDN edge.
            var url = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogInformation("Update check: host returned {Code}", (int)resp.StatusCode);
                return null;
            }

            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(s);
            var r = doc.RootElement;

            var verStr = r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var dl = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var latest = verStr is null ? null : ParseVersion(verStr);
            if (latest is null || string.IsNullOrEmpty(dl)) return null;

            if (latest <= CurrentVersion)
            {
                _log.LogInformation("Update check: latest {Latest} vs current {Current}", latest, CurrentVersion);
                return null;
            }

            var notes = r.TryGetProperty("notes", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            long size = r.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            return new UpdateInfo(latest, verStr!, dl!, notes, size);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    public async Task<bool> PrepareAndLaunchUpdaterAsync(UpdateInfo info, CancellationToken ct = default)
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Self-update needs a single-file .exe; current process is {Exe}", currentExe);
                return false;
            }

            var dir = Path.Combine(_paths.Root, "update");
            Directory.CreateDirectory(dir);
            var newExe = Path.Combine(dir, "DataVortex.new.exe");

            _log.LogInformation("Downloading update {Tag} ({Size} bytes)", info.Tag, info.Size);
            using (var resp = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            var pid = Environment.ProcessId;
            var bat = Path.Combine(dir, "apply-update.bat");
            // The batch takes the paths as ARGUMENTS (%1 current exe, %2 new exe, %3 pid) instead of baking them in.
            // cmd.exe reads a .bat file in the console's OEM code page, so a hard-coded path with a non-ASCII char
            // (e.g. a Windows username like "Léo") gets mangled and the relaunch fails. Arguments are passed by
            // CreateProcessW as UTF-16, so accented paths survive — and the script body stays pure ASCII.
            var script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"PID eq %~3\" 2>nul | find \"%~3\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                "move /Y \"%~2\" \"%~1\" >nul\r\n" +
                "start \"\" \"%~1\"\r\n" +
                "del \"%~f0\"\r\n";
            await File.WriteAllTextAsync(bat, script, ct).ConfigureAwait(false);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /s + outer quotes: cmd strips only the outermost pair, so each quoted path is parsed as its own arg.
                Arguments = $"/s /c \"\"{bat}\" \"{currentExe}\" \"{newExe}\" {pid}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            });
            _log.LogInformation("Updater launched; the app will exit to be replaced.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to prepare/launch updater");
            return false;
        }
    }

    /// <summary>Parses a version like "1.2.3" (or "v1.2.3") into a 3-part Version, ignoring any suffix.</summary>
    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        var numeric = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(numeric, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major < 0 ? 0 : v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
}
