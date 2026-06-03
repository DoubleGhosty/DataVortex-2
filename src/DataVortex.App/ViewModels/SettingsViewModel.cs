using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Themes;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Backfill;
using DataVortex.Core.Configuration;
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
    [ObservableProperty] private string extractKeywords = "";

    // ---- Download filter (live) ----
    [ObservableProperty] private string downloadExtensions = "";

    // ---- Backfill (live) ----
    [ObservableProperty] private bool backfillEnabled;
    [ObservableProperty] private string backfillIdleSeconds = "";
    [ObservableProperty] private string backfillPageSize = "";

    // ---- Appearance (applied on Save) ----
    [ObservableProperty] private bool isLightTheme;

    // ---- Passculture / 2captcha (restart required) ----
    [ObservableProperty] private string twoCaptchaApiKey = "";

    // ---- Proxy for Passculture (restart required) ----
    [ObservableProperty] private bool proxyEnabled;
    [ObservableProperty] private string proxyAddress = "";
    [ObservableProperty] private string proxyUsername = "";
    [ObservableProperty] private string proxyPassword = "";

    [ObservableProperty] private string statusText = "";

    // ---- Updates ----
    [ObservableProperty] private string currentVersionText = "";
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool updateAvailable;
    private UpdateInfo? _pendingUpdate;

    public SettingsViewModel(ISettingsService settings, IPipelineCoordinator coordinator,
        IBackfillService backfill, ITelegramService telegram, IUpdateService update, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _coordinator = coordinator;
        _backfill = backfill;
        _telegram = telegram;
        _update = update;
        _log = log;
        CurrentVersionText = $"Version {_update.CurrentVersion}";
        LoadFromSettings();
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
        ExtractKeywords = string.Join(Environment.NewLine, s.ExtractKeywords);
        DownloadExtensions = string.Join(Environment.NewLine, s.DownloadExtensions);

        BackfillEnabled = s.BackfillEnabled;
        BackfillIdleSeconds = s.BackfillIdleSeconds.ToString();
        BackfillPageSize = s.BackfillPageSize.ToString();

        IsLightTheme = s.Theme == AppTheme.Light;
        TwoCaptchaApiKey = s.TwoCaptchaApiKey ?? "";

        ProxyEnabled = s.ProxyEnabled;
        ProxyAddress = s.ProxyAddress ?? "";
        ProxyUsername = s.ProxyUsername ?? "";
        ProxyPassword = s.ProxyPassword ?? "";

        StatusText = "";
    }

    [RelayCommand]
    private void Reload()
    {
        LoadFromSettings();
        StatusText = "Réglages rechargés depuis le disque.";
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatus = "Vérification…";
        UpdateAvailable = false;
        var info = await _update.CheckForUpdateAsync();
        _pendingUpdate = info;
        if (info is null)
        {
            UpdateStatus = $"À jour (version {_update.CurrentVersion}).";
        }
        else
        {
            UpdateAvailable = true;
            UpdateStatus = $"Nouvelle version {info.Version} disponible.";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        UpdateStatus = "Téléchargement et installation…";
        var ok = await _update.PrepareAndLaunchUpdaterAsync(_pendingUpdate);
        if (ok)
        {
            UpdateStatus = "Mise à jour prête — redémarrage…";
            System.Windows.Application.Current.Shutdown();
        }
        else
        {
            UpdateStatus = "Échec : l'auto-update requiert un build publié (.exe). Voir les logs.";
        }
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;

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

        // Proxy (restart required — the HttpClient is built once at startup).
        s.ProxyEnabled = ProxyEnabled;
        s.ProxyAddress = (ProxyAddress ?? "").Trim();
        s.ProxyUsername = (ProxyUsername ?? "").Trim();
        s.ProxyPassword = (ProxyPassword ?? "").Trim();

        _settings.Save();

        // Apply everything that can take effect without a restart.
        _coordinator.UpdateBandwidthLimit(s.BandwidthLimitBytesPerSecond);
        _telegram.ApplyTransferTuning();
        if (BackfillEnabled != _backfill.IsEnabled)
            _backfill.SetEnabled(BackfillEnabled); // SetEnabled persists + republishes state on its own
        ThemeManager.Apply(newTheme);

        // Reflect clamped/normalised values back into the fields, then report.
        LoadFromSettings();
        StatusText = "Enregistré. Workers/files/retries, clé 2captcha et proxy prennent effet au prochain démarrage.";
        _log.LogInformation("Settings saved from the settings panel");
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
