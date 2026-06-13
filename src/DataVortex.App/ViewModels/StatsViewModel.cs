using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;

namespace DataVortex.App.ViewModels;

/// <summary>Analytics over the persisted SQLite store: global totals, per-channel rollup, and an indexed,
/// filterable, paged record search (incl. a Failures filter). All queries run off the UI thread.</summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly IStorageService _storage;
    private readonly IUiDispatcher _ui;
    private DateTime _lastStatsRefresh;

    public ObservableCollection<ChannelStat> Channels { get; } = new();
    public ObservableCollection<FileRecord> Results { get; } = new();
    public string[] StatusFilters { get; } = { "All", "Completed", "Failed", "Ignored" };

    [ObservableProperty] private long totalRecords;
    [ObservableProperty] private long totalBytes;
    [ObservableProperty] private long completed;
    [ObservableProperty] private long ignored;
    [ObservableProperty] private long failed;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusFilter = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo), nameof(CanPrev), nameof(CanNext))]
    private int page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo), nameof(CanPrev), nameof(CanNext))]
    private int totalResults;

    public string PageInfo => TotalResults == 0
        ? "0 result"
        : $"Page {Page + 1} / {Math.Max(1, (TotalResults + PageSize - 1) / PageSize)}  ·  {TotalResults} total";
    public bool CanPrev => Page > 0;
    public bool CanNext => (Page + 1) * PageSize < TotalResults;

    public StatsViewModel(IStorageService storage, IPipelineCoordinator coordinator, IUiDispatcher ui)
    {
        _storage = storage;
        _ui = ui;
        coordinator.FileArchived += OnArchived;
        _ = RefreshAsync();
    }

    private void OnArchived(FileRecord record)
    {
        if ((DateTime.UtcNow - _lastStatsRefresh).TotalSeconds < 5) return;
        _ = RefreshStatsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshStatsAsync().ConfigureAwait(false);
        await SearchAsync().ConfigureAwait(false);
    }

    private async Task RefreshStatsAsync()
    {
        _lastStatsRefresh = DateTime.UtcNow;
        var (stats, channels) = await Task.Run(() => (_storage.GetStats(), _storage.GetChannelStats())).ConfigureAwait(false);
        _ui.Post(() =>
        {
            TotalRecords = stats.TotalRecords;
            TotalBytes = stats.TotalBytes;
            Completed = stats.Completed;
            Ignored = stats.Ignored;
            Failed = stats.Failed;
            Channels.Clear();
            foreach (var c in channels) Channels.Add(c);
        });
    }

    partial void OnSearchTextChanged(string value) { Page = 0; _ = SearchAsync(); }
    partial void OnStatusFilterChanged(string value) { Page = 0; _ = SearchAsync(); }

    [RelayCommand]
    private void NextPage() { if (CanNext) { Page++; _ = SearchAsync(); } }

    [RelayCommand]
    private void PrevPage() { if (CanPrev) { Page--; _ = SearchAsync(); } }

    private async Task SearchAsync()
    {
        ProcessingStatus? status = StatusFilter switch
        {
            "Completed" => ProcessingStatus.Completed,
            "Failed" => ProcessingStatus.Failed,
            "Ignored" => ProcessingStatus.Ignored,
            _ => null
        };
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var offset = Page * PageSize;
        var (total, results) = await Task.Run(() =>
            (_storage.CountRecords(text, status, null), _storage.SearchRecords(text, status, null, PageSize, offset)))
            .ConfigureAwait(false);
        _ui.Post(() =>
        {
            TotalResults = total;
            Results.Clear();
            foreach (var r in results) Results.Add(r);
        });
    }
}
