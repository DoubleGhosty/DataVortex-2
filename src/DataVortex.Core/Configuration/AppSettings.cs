using DataVortex.Core.Models;

namespace DataVortex.Core.Configuration;

public enum AppTheme { Dark, Light }

/// <summary>User-facing, non-secret configuration. Persisted as <c>settings.json</c>.
/// The Telegram api_hash is NOT stored here — it lives DPAPI-encrypted in the credential store.</summary>
public sealed class AppSettings
{
    // ---- Pipeline tuning ----
    public int MaxParallelDownloads { get; set; } = 4;
    public int MaxParallelProcessing { get; set; } = 3;
    public int DownloadQueueCapacity { get; set; } = 2000;
    public int ProcessingQueueCapacity { get; set; } = 2000;
    public int MaxDownloadRetries { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 2000;

    /// <summary>Global download bandwidth cap in bytes/second. 0 = unlimited.</summary>
    public long BandwidthLimitBytesPerSecond { get; set; } = 0;

    /// <summary>How many file chunks WTelegram downloads in parallel per file (its <c>ParallelTransfers</c>).
    /// WTelegram's own default is a cautious 2; raising it is the main lever for download speed on
    /// high-latency links (e.g. a cloud server far from the file's DC). Too high can trigger FLOOD_WAIT,
    /// so it is clamped to 1..32 when applied.</summary>
    public int ParallelTransfersPerFile { get; set; } = 8;

    // ---- Extraction filtering ----
    /// <summary>When true, only *.txt whose <b>filename</b> contains one of <see cref="ExtractKeywords"/>
    /// are extracted; the rest are skipped. Content is not inspected. Set false to extract every *.txt.</summary>
    public bool ExtractOnlyMatchingTxt { get; set; } = true;

    /// <summary>Case-insensitive substring keywords matched against the filename. "password" also matches
    /// Password / PASSWORD / passwords.</summary>
    public List<string> ExtractKeywords { get; set; } = new() { "password" };

    /// <summary>When false (default), matching *.txt entries are scanned for credentials <b>in memory</b> and
    /// never written to disk — far less disk I/O and no per-message folders (the killer at high volume).
    /// Set true to also persist the extracted *.txt under <c>extracted/</c> (e.g. to browse them in Files).</summary>
    public bool KeepExtractedFiles { get; set; } = false;

    // ---- Download filtering ----
    /// <summary>Only files whose extension is in this list are downloaded; an empty list downloads
    /// everything. Default: archives only (so plain .txt and other attachments are skipped).</summary>
    public List<string> DownloadExtensions { get; set; } = new() { ".zip", ".rar", ".7z" };

    // ---- UI ----
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // ---- Telegram (non-secret) ----
    public int ApiId { get; set; }
    public string PhoneNumber { get; set; } = "";

    // ---- Backfill (idle catch-up) ----
    /// <summary>When idle, scan watched channels' history for old archives never processed.</summary>
    public bool BackfillEnabled { get; set; } = true;
    /// <summary>How long the pipeline must be idle before backfill kicks in (seconds).</summary>
    public int BackfillIdleSeconds { get; set; } = 120;
    /// <summary>Messages fetched per history page (1..100).</summary>
    public int BackfillPageSize { get; set; } = 100;

    // ---- Channels the user wants to archive ----
    public List<WatchedChannel> WatchedChannels { get; set; } = new();

    // ---- External services ----
    // 2captcha key (optional). Keep secrets in credential store in production; settings.json is user-facing.
    public string TwoCaptchaApiKey { get; set; } = "e0650244d66d3b814d47e0646445fbac";

    // ---- Proxy (used for Passculture backend requests) ----
    /// <summary>When true, Passculture backend requests are routed through the proxies in <see cref="Proxies"/>,
    /// rotated round-robin per request. False (or an empty list) sends requests directly.</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>Proxy list — one full URL per line: "http://user:pass@host:port". Imported from a .txt file.</summary>
    public List<string> Proxies { get; set; } = new();

    // ---- Account checker ----
    /// <summary>Global cap on concurrent Passculture sign-in checks — shared by the combolist import, the
    /// archive flow and the manual button (1..10). Each check costs a captcha; HTTP 429 is retried with backoff.</summary>
    public int MaxParallelAccountChecks { get; set; } = 10;

    // ---- Maintenance ----
    /// <summary>Safety-net cleanup: files left in downloads/ and extracted/ older than this many minutes are
    /// deleted periodically (active/locked files are skipped). 0 disables it.</summary>
    public int CleanupResidueMinutes { get; set; } = 60;
}
