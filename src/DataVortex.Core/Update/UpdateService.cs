using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Updates;

/// <summary>A newer release available on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string? Notes, long Size);

public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>Queries the latest GitHub release; returns it only if newer than the running version and it
    /// carries a downloadable .exe asset. Returns <c>null</c> otherwise (already up to date / offline / no asset).</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Downloads the release exe and launches an external updater that waits for this process to exit,
    /// swaps the executable and relaunches it. Returns true once the updater is running — the caller should then
    /// shut the app down. Self-update only works on a published single-file build.</summary>
    Task<bool> PrepareAndLaunchUpdaterAsync(UpdateInfo info, CancellationToken ct = default);
}

/// <summary>
/// GitHub Releases updater for the public repo. No token needed (public repo, anonymous API is enough for a
/// version check). The actual swap is done by a tiny generated .bat so the running exe can be replaced.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private const string Owner = "DoubleGhosty";
    private const string Repo = "DataVortex-2";

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<GitHubUpdateService> _log;

    public Version CurrentVersion { get; } =
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public GitHubUpdateService(HttpClient http, AppPaths paths, ILogger<GitHubUpdateService> log)
    {
        _http = http;
        _paths = paths;
        _log = log;
        // GitHub's API requires a User-Agent.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DataVortex-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogInformation("Update check: GitHub returned {Code}", (int)resp.StatusCode);
                return null;
            }

            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = tag is null ? null : ParseVersion(tag);
            if (latest is null) return null;

            string? dl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            if (latest <= CurrentVersion || string.IsNullOrEmpty(dl))
            {
                _log.LogInformation("Update check: latest {Latest} vs current {Current}, asset={HasAsset}",
                    latest, CurrentVersion, dl is not null);
                return null;
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            return new UpdateInfo(latest, tag!, dl!, notes, size);
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

    /// <summary>Parses a tag like "v1.2.3" into a 3-part Version, ignoring any suffix.</summary>
    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        var numeric = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(numeric, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major < 0 ? 0 : v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
}
