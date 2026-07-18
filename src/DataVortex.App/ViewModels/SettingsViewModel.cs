using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.App.Themes;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Backfill;
using DataVortex.Core.Configuration;
using DataVortex.Core.Licensing;
using DataVortex.Core.Pipeline;
using DataVortex.Core.Updates;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.ViewModels;

/// <summary>
/// Edits <see cref="AppSettings"/> from the UI instead of hand-editing settings.json.
/// Numeric fields are bound as strings and parsed/clamped on Save so bad input can never crash the
/// pipeline or trip on culture (comma vs dot). Settings that are re-read per item apply immediately;
/// pipeline pool sizes are read once in the coordinator's constructor, so they only take effect on
/// the next app launch (flagged in the view with a "restart required" badge).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IPipelineCoordinator _coordinator;
    private readonly IBackfillService _backfill;
    private readonly ITelegramService _telegram;
    private readonly IUpdateService _update;
    private readonly IDialogService _dialogs;
    private readonly IDownloadDeduplicator _dedup;
    private readonly LicenseGuard _license;
    private readonly ILogger<SettingsViewModel> _log;

    // ---- Pipeline (restart required) ----
    [ObservableProperty] private string maxParallelDownloads = "";
    [ObservableProperty] private string maxParallelProcessing = "";
    [ObservableProperty] private string downloadQueueCapacity = "";
    [ObservableProperty] private string processingQueueCapacity = "";
    [ObservableProperty] private string maxDownloadRetries = "";
    [ObservableProperty] private string retryBaseDelayMs = "";

    // ---- Network (live) ----
    [ObservableProperty] private string bandwidthMBps = "";
    [ObservableProperty] private string parallelTransfers = "";

    // ---- Extraction (live) ----
    [ObservableProperty] private bool extractOnlyMatchingTxt;
    [ObservableProperty] private bool keepExtractedFiles;
    [ObservableProperty] private string extractKeywords = "";

    // ---- Download filter (live) ----
    [ObservableProperty] private string downloadExtensions = "";

    // ---- Backfill (live) ----
    [ObservableProperty] private bool backfillEnabled;
    [ObservableProperty] private string backfillIdleSeconds = "";
    [ObservableProperty] private string backfillPageSize = "";

    // ---- Appearance (applied on Save) ----
    [ObservableProperty] private bool isLightTheme;

    // ---- Passculture / captcha (restart required) ----
    [ObservableProperty] private string twoCaptchaApiKey = "";
    [ObservableProperty] private bool useCapMonster;
    [ObservableProperty] private string capMonsterApiKey = "";

    // ---- Notifications (live) ----
    [ObservableProperty] private bool notifyOnTelegram;
    [ObservableProperty] private string notifyTarget = "";

    // ---- Proxy for Passculture (restart required) ----
    [ObservableProperty] private bool proxyEnabled;
    [ObservableProperty] private string proxyStatus = "";
    private List<string>? _importedProxies;   // non-null only after an import → replaces the saved list on Save

    // ---- Account checker (applied immediately) ----
    [ObservableProperty] private string parallelAccountChecks = "";

    [ObservableProperty] private string statusText = "";

    // ---- Licence (read-only status for the user) ----
    [ObservableProperty] private bool licenseVisible;
    [ObservableProperty] private string licenseStateText = "—";
    [ObservableProperty] private string licenseTypeText = "—";
    [ObservableProperty] private string licenseExpiryText = "—";
    [ObservableProperty] private string licenseFeaturesText = "—";

    // ---- Updates ----
    [ObservableProperty] private string currentVersionText = "";
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool updateAvailable;
    private UpdateInfo? _pendingUpdate;

    public SettingsViewModel(ISettingsService settings, IPipelineCoordinator coordinator,
        IBackfillService backfill, ITelegramService telegram, IUpdateService update, IDialogService dialogs,
        IDownloadDeduplicator dedup, LicenseGuard license, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _coordinator = coordinator;
        _backfill = backfill;
        _telegram = telegram;
        _update = update;
        _dialogs = dialogs;
        _dedup = dedup;
        _license = license;
        _log = log;
        CurrentVersionText = $"Version {_update.CurrentVersion}";
#if DEBUG
        LicenseVisible = false;   // dev build runs unlicensed (no server) → hide the licence panel
#else
        LicenseVisible = true;    // Release always enforces licensing → show the licence panel
#endif
        _license.StatusChanged += UpdateLicense;
        UpdateLicense(_license.Current);
        LoadFromSettings();
    }

    private void UpdateLicense(LicenseStatus s)
    {
        LicenseStateText = s.State switch
        {
            LicenseState.Active => "Active",
            LicenseState.Degraded => "Active (offline — grace period)",
            LicenseState.Expired => "Expired",
            LicenseState.Revoked => "Revoked",
            LicenseState.Blocked => "Blocked — reconnection required",
            LicenseState.HardwareChanged => "Hardware changed",
            LicenseState.NotActivated => "Not activated",
            _ => "—",
        };
        var c = s.Claims;
        LicenseTypeText = c?.Type.ToString() ?? "—";
        LicenseFeaturesText = c is not null && c.Features.Count > 0 ? string.Join(", ", c.Features) : "—";
        if (c?.LicenseExpiresAt is { } exp)
        {
            var left = exp - DateTimeOffset.UtcNow;
            LicenseExpiryText = left > TimeSpan.Zero
                ? $"{exp.LocalDateTime:dd/MM/yyyy} — {(int)left.TotalDays} d left"
                : $"{exp.LocalDateTime:dd/MM/yyyy} — expired";
        }
        else LicenseExpiryText = c is not null ? "Perpetual" : "—";
    }

    /// <summary>Mirrors the persisted settings into the editable fields. Also used after Save to show
    /// the clamped/normalised values back to the user.</summary>
    public void LoadFromSettings()
    {
        var s = _settings.Current;

        MaxParallelDownloads = s.MaxParallelDownloads.ToString();
        MaxParallelProcessing = s.MaxParallelProcessing.ToString();
        DownloadQueueCapacity = s.DownloadQueueCapacity.ToString();
        ProcessingQueueCapacity = s.ProcessingQueueCapacity.ToString();
        MaxDownloadRetries = s.MaxDownloadRetries.ToString();
        RetryBaseDelayMs = s.RetryBaseDelayMs.ToString();

        BandwidthMBps = s.BandwidthLimitBytesPerSecond <= 0
            ? "0"
            : (s.BandwidthLimitBytesPerSecond / (1024.0 * 1024.0)).ToString("0.###", CultureInfo.InvariantCulture);
        ParallelTransfers = s.ParallelTransfersPerFile.ToString();

        ExtractOnlyMatchingTxt = s.ExtractOnlyMatchingTxt;
        KeepExtractedFiles = s.KeepExtractedFiles;
        ExtractKeywords = string.Join(Environment.NewLine, s.ExtractKeywords);
        DownloadExtensions = string.Join(Environment.NewLine, s.DownloadExtensions);

        BackfillEnabled = s.BackfillEnabled;
        BackfillIdleSeconds = s.BackfillIdleSeconds.ToString();
        BackfillPageSize = s.BackfillPageSize.ToString();

        IsLightTheme = s.Theme == AppTheme.Light;
        TwoCaptchaApiKey = s.TwoCaptchaApiKey ?? "";
        CapMonsterApiKey = s.CapMonsterApiKey ?? "";
        UseCapMonster = string.Equals(s.CaptchaProvider, "CapMonster", StringComparison.OrdinalIgnoreCase);
        NotifyOnTelegram = s.NotifyOnTelegram;
        NotifyTarget = s.NotifyTarget ?? "";

        ProxyEnabled = s.ProxyEnabled;
        _importedProxies = null;
        ProxyStatus = $"{s.Proxies.Count} proxy(ies) saved.";

        ParallelAccountChecks = s.MaxParallelAccountChecks.ToString();

        StatusText = "";
    }

    [RelayCommand]
    private void Reload()
    {
        LoadFromSettings();
        StatusText = "Settings reloaded from disk.";
    }

    /// <summary>Clears the download de-duplication memory so already-seen archives can be downloaded again.
    /// A maintenance action — lives here rather than in the main toolbar.</summary>
    [RelayCommand]
    private void ClearDedup()
    {
        if (!_dialogs.Confirm(
                $"Clear the de-duplication memory ({_dedup.Count} archive(s))?\nArchives may then be downloaded again.",
                "Clear dedup store"))
            return;
        _dedup.Clear();
        StatusText = "De-duplication memory cleared.";
        _log.LogInformation("Dedup store cleared from settings");
    }

    /// <summary>Imports a proxy list (.txt), one "http://user:pass@host:port" per line. Held in memory and
    /// persisted on Save; the rotating pool is rebuilt at the next launch (restart required).</summary>
    [RelayCommand]
    private void ImportProxies()
    {
        var path = _dialogs.PickFile("Proxy list (*.txt)|*.txt|All files (*.*)|*.*");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var lines = System.IO.File.ReadLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Contains("://"))
                .Distinct()
                .ToList();
            _importedProxies = lines;
            ProxyStatus = $"{lines.Count} proxy(ies) imported — Save to apply (restart required).";
        }
        catch (Exception ex)
        {
            ProxyStatus = "Read failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatus = "Checking…";
        UpdateAvailable = false;
        var info = await _update.CheckForUpdateAsync();
        _pendingUpdate = info;
        if (info is null)
        {
            UpdateStatus = $"Up to date (version {_update.CurrentVersion}).";
        }
        else
        {
            UpdateAvailable = true;
            UpdateStatus = $"New version {info.Version} available.";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        UpdateStatus = "Downloading and installing…";
        var ok = await _update.PrepareAndLaunchUpdaterAsync(_pendingUpdate);
        if (ok)
        {
            UpdateStatus = "Update ready — restarting…";
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            UpdateStatus = "Failed: auto-update requires a published build (.exe). See the logs.";
        }
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;

        // Snapshot the restart-only settings (+ note a proxy import) so we can prompt for a relaunch if they change.
        var beforeRestart = RestartSignature(s);
        var proxyImported = _importedProxies is not null;

        // Pipeline (restart required) — clamp to sane ranges; keep current value on unparsable input.
        s.MaxParallelDownloads = ParseInt(MaxParallelDownloads, 1, 64, s.MaxParallelDownloads);
        s.MaxParallelProcessing = ParseInt(MaxParallelProcessing, 1, 64, s.MaxParallelProcessing);
        s.DownloadQueueCapacity = ParseInt(DownloadQueueCapacity, 1, 100_000, s.DownloadQueueCapacity);
        s.ProcessingQueueCapacity = ParseInt(ProcessingQueueCapacity, 1, 100_000, s.ProcessingQueueCapacity);
        s.MaxDownloadRetries = ParseInt(MaxDownloadRetries, 0, 100, s.MaxDownloadRetries);
        s.RetryBaseDelayMs = ParseInt(RetryBaseDelayMs, 0, 600_000, s.RetryBaseDelayMs);

        // Network (live).
        s.BandwidthLimitBytesPerSecond = ParseBandwidthBytes(BandwidthMBps, s.BandwidthLimitBytesPerSecond);
        s.ParallelTransfersPerFile = ParseInt(ParallelTransfers, 1, 32, s.ParallelTransfersPerFile);

        // Extraction (live).
        s.ExtractOnlyMatchingTxt = ExtractOnlyMatchingTxt;
        s.KeepExtractedFiles = KeepExtractedFiles;
        s.ExtractKeywords = ParseLines(ExtractKeywords);

        // Download filter (live) — normalise to lowercase ".ext".
        s.DownloadExtensions = ParseLines(DownloadExtensions)
            .Select(NormalizeExtension)
            .Where(e => e.Length > 1)
            .Distinct()
            .ToList();

        // Backfill (live).
        s.BackfillEnabled = BackfillEnabled;
        s.BackfillIdleSeconds = ParseInt(BackfillIdleSeconds, 10, 86_400, s.BackfillIdleSeconds);
        s.BackfillPageSize = ParseInt(BackfillPageSize, 1, 100, s.BackfillPageSize);

        // Appearance + Passculture.
        var newTheme = IsLightTheme ? AppTheme.Light : AppTheme.Dark;
        s.Theme = newTheme;
        s.TwoCaptchaApiKey = (TwoCaptchaApiKey ?? "").Trim();
        s.CapMonsterApiKey = (CapMonsterApiKey ?? "").Trim();
        s.CaptchaProvider = UseCapMonster ? "CapMonster" : "TwoCaptcha";
        s.NotifyOnTelegram = NotifyOnTelegram;
        s.NotifyTarget = (NotifyTarget ?? "").Trim();

        // Proxy (restart required — the rotating HttpClient pool is built once at startup).
        s.ProxyEnabled = ProxyEnabled;
        if (_importedProxies is not null) s.Proxies = _importedProxies;

        // Account checker (applied immediately).
        s.MaxParallelAccountChecks = ParseInt(ParallelAccountChecks, 1, 10, s.MaxParallelAccountChecks);

        _settings.Save();

        // Apply everything that can take effect without a restart.
        _coordinator.UpdateBandwidthLimit(s.BandwidthLimitBytesPerSecond);
        _telegram.ApplyTransferTuning();
        AccountTester.ConfigureParallelism(s.MaxParallelAccountChecks);
        if (BackfillEnabled != _backfill.IsEnabled)
            _backfill.SetEnabled(BackfillEnabled); // SetEnabled persists + republishes state on its own
        ThemeManager.Apply(newTheme);

        // Did anything that's only read at startup change? If so, tell the user and offer to relaunch.
        var restartNeeded = proxyImported || !RestartSignature(s).Equals(beforeRestart);

        // Reflect clamped/normalised values back into the fields.
        LoadFromSettings();
        _log.LogInformation("Settings saved from the settings panel");

        if (!restartNeeded)
        {
            StatusText = "Saved.";
            return;
        }

        StatusText = "Saved — a restart is needed for some changes to take effect.";
        if (_dialogs.Confirm(
                "Some settings only take effect after a restart:\n\n" +
                "• Pipeline workers, queue capacities and retries\n" +
                "• Captcha provider and API keys\n" +
                "• Proxy list and toggle\n\n" +
                "Restart DataVortex now?",
                "Restart required"))
        {
            RestartApp();
        }
    }

    /// <summary>The settings that are only read at startup — used to detect whether a Save needs a relaunch.</summary>
    private static (int, int, int, int, int, int, string, string, string, bool) RestartSignature(AppSettings s)
        => (s.MaxParallelDownloads, s.MaxParallelProcessing, s.DownloadQueueCapacity, s.ProcessingQueueCapacity,
            s.MaxDownloadRetries, s.RetryBaseDelayMs, s.TwoCaptchaApiKey, s.CapMonsterApiKey, s.CaptchaProvider,
            s.ProxyEnabled);

    /// <summary>Relaunches the app cleanly: a tiny batch WAITS for this process to fully exit (releasing the SQLite
    /// DB + settings files) before starting the exe again, so the two instances never fight over those files. The
    /// exe path travels through Arguments (UTF-16), and the batch body is pure ASCII, so an accented install path
    /// (e.g. a "Léo" username) survives — same fix as the self-updater. This instance then shuts down.</summary>
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                var pid = Environment.ProcessId;
                var bat = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"datavortex-restart-{pid}.bat");
                var script =
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    "tasklist /FI \"PID eq %~2\" 2>nul | find \"%~2\" >nul\r\n" +
                    "if not errorlevel 1 (\r\n" +
                    "  timeout /t 1 /nobreak >nul\r\n" +
                    "  goto wait\r\n" +
                    ")\r\n" +
                    "start \"\" \"%~1\"\r\n" +
                    "del \"%~f0\"\r\n";
                System.IO.File.WriteAllText(bat, script);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/s /c \"\"{bat}\" \"{exe}\" {pid}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to schedule app restart"); }
        }
        System.Windows.Application.Current.Shutdown();
    }

    private static int ParseInt(string? text, int min, int max, int fallback)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : fallback;

    /// <summary>Parses a MB/s value (accepting both '.' and ',' as the decimal separator) into bytes/second.
    /// Blank or ≤0 means unlimited (0).</summary>
    private static long ParseBandwidthBytes(string? text, long fallback)
    {
        var t = (text ?? "").Trim().Replace(',', '.');
        if (t.Length == 0) return 0;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
            return mb <= 0 ? 0 : (long)Math.Round(mb * 1024 * 1024);
        return fallback;
    }

    private static List<string> ParseLines(string? text)
        => (text ?? "")
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();

    private static string NormalizeExtension(string raw)
    {
        var e = raw.Trim().ToLowerInvariant();
        if (e.Length == 0) return e;
        return e.StartsWith('.') ? e : "." + e;
    }
}
