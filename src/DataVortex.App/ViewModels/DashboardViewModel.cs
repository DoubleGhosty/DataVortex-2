using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Backfill;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;

namespace DataVortex.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int WindowSize = 60;
    private readonly IUiDispatcher _ui;
    private readonly IBackfillService _backfill;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _processingHistory = new();

    /// <summary>Shared with the Logs section — shown as a live feed on the dashboard too.</summary>
    public LogViewModel Log { get; }

    [ObservableProperty] private string downloadSpeedText = "0 B/s";
    [ObservableProperty] private double extractedFilesPerSecond;
    [ObservableProperty] private int activeDownloads;
    [ObservableProperty] private int downloadQueueDepth;
    [ObservableProperty] private int processingQueueDepth;
    [ObservableProperty] private long totalFilesDownloaded;
    [ObservableProperty] private long totalFilesProcessed;
    [ObservableProperty] private long totalBytesDownloaded;
    [ObservableProperty] private long totalExtractedFiles;
    [ObservableProperty] private int watchedChannelCount;
    [ObservableProperty] private ConnectionState connection = ConnectionState.Disconnected;
    [ObservableProperty] private string connectionText = "Disconnected";
    [ObservableProperty] private IReadOnlyList<double> downloadSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> processingSeries = Array.Empty<double>();
    [ObservableProperty] private string extractFilterText = "";
    [ObservableProperty] private string backfillStateText = "Disabled";
    [ObservableProperty] private string backfillDetail = "off";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackfillToggleLabel))]
    private bool backfillEnabled;

    public string BackfillToggleLabel => BackfillEnabled ? "Pause backfill" : "Enable backfill";

    public DashboardViewModel(
        IMetricsService metrics, ITelegramService telegram, ISettingsService settings,
        IBackfillService backfill, LogViewModel log, IUiDispatcher ui)
    {
        _ui = ui;
        _backfill = backfill;
        Log = log;
        BackfillEnabled = backfill.IsEnabled;
        UpdateBackfill(backfill.Status);
        backfill.StatusChanged += s => _ui.Post(() => UpdateBackfill(s));
        WatchedChannelCount = settings.Current.WatchedChannels.Count;
        ExtractFilterText = DescribeFilter(settings.Current);
        Connection = telegram.State;
        ConnectionText = telegram.State.ToString();

        metrics.SnapshotProduced += OnSnapshot;
        telegram.StateChanged += s => _ui.Post(() =>
        {
            Connection = s;
            ConnectionText = s.ToString();
        });
        settings.Changed += s => _ui.Post(() =>
        {
            WatchedChannelCount = s.WatchedChannels.Count;
            ExtractFilterText = DescribeFilter(s);
        });
    }

    private static string DescribeFilter(AppSettings s)
        => s.ExtractOnlyMatchingTxt && s.ExtractKeywords.Count > 0
            ? $"Extraction filter: only .txt whose filename contains — {string.Join(", ", s.ExtractKeywords)} (case-insensitive)"
            : "Extraction filter: off — every .txt is extracted";

    private void OnSnapshot(MetricsSnapshot snap) => _ui.Post(() =>
    {
        DownloadSpeedText = FormatRate(snap.DownloadBytesPerSecond);
        ExtractedFilesPerSecond = Math.Round(snap.ExtractedFilesPerSecond, 1);
        ActiveDownloads = snap.ActiveDownloads;
        DownloadQueueDepth = snap.DownloadQueueDepth;
        ProcessingQueueDepth = snap.ProcessingQueueDepth;
        TotalFilesDownloaded = snap.TotalFilesDownloaded;
        TotalFilesProcessed = snap.TotalFilesProcessed;
        TotalBytesDownloaded = snap.TotalBytesDownloaded;
        TotalExtractedFiles = snap.TotalExtractedFiles;

        Push(_downloadHistory, snap.DownloadBytesPerSecond);
        Push(_processingHistory, snap.ExtractedFilesPerSecond);
        DownloadSeries = _downloadHistory.ToArray();
        ProcessingSeries = _processingHistory.ToArray();
    });

    private static string FormatRate(double bytesPerSec)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double v = bytesPerSec;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.0} {units[u]}";
    }

    private static void Push(Queue<double> q, double value)
    {
        q.Enqueue(value);
        while (q.Count > WindowSize) q.Dequeue();
    }

    [RelayCommand]
    private void ToggleBackfill()
    {
        _backfill.SetEnabled(!_backfill.IsEnabled);
        BackfillEnabled = _backfill.IsEnabled;
    }

    private void UpdateBackfill(BackfillStatus status)
    {
        BackfillStateText = status.State.ToString();
        BackfillEnabled = _backfill.IsEnabled;
        BackfillDetail = status.State switch
        {
            BackfillState.Scanning => $"{status.CurrentChannel} · {status.TotalScanned:N0} scanned · {status.TotalEnqueued:N0} found",
            BackfillState.WaitingForIdle => "waiting for the pipeline to be idle…",
            BackfillState.Completed => $"all caught up · {status.ChannelsCompleted}/{status.ChannelsTotal} channels · {status.TotalEnqueued:N0} archived",
            _ => "off"
        };
    }
}
