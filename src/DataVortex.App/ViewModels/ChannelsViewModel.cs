using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.ViewModels;

public sealed partial class ChannelsViewModel : ObservableObject
{
    private readonly ITelegramService _telegram;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ChannelsViewModel> _log;

    public ObservableCollection<ChannelInfo> Channels { get; } = new();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var c in Channels) c.IsWatched = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var c in Channels) c.IsWatched = false;
    }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Not loaded.";

    public ChannelsViewModel(ITelegramService telegram, ISettingsService settings, IUiDispatcher ui, ILogger<ChannelsViewModel> log)
    {
        _telegram = telegram;
        _settings = settings;
        _ui = ui;
        _log = log;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_telegram.State != ConnectionState.Connected)
        {
            StatusText = "Connect to Telegram first.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Loading dialogs…";
            var dialogs = await _telegram.GetDialogsAsync().ConfigureAwait(false);
            var watched = _settings.Current.WatchedChannels.Select(w => w.Id).ToHashSet();

            _ui.Post(() =>
            {
                Channels.Clear();
                foreach (var info in dialogs)
                {
                    info.IsWatched = watched.Contains(info.Id);
                    Channels.Add(info);
                }
                StatusText = $"{Channels.Count} dialog(s). {Channels.Count(c => c.IsWatched)} watched.";
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load dialogs");
            _ui.Post(() => StatusText = "Failed to load dialogs — see logs.");
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var selected = Channels.Where(c => c.IsWatched).ToList();
        _settings.Current.WatchedChannels = selected
            .Select(c => new WatchedChannel { Id = c.Id, Title = c.Title, IsChannel = c.IsChannel })
            .ToList();
        _settings.Save();
        _telegram.SetWatchedChannels(selected.Select(c => c.Id));
        StatusText = $"Saved — watching {selected.Count} channel(s).";
        _log.LogInformation("Watched channels updated: {Count}", selected.Count);
    }

    /// <summary>Pushes the persisted watched set into the Telegram listener (called right after connect).</summary>
    public void ApplyWatchedFromSettings()
        => _telegram.SetWatchedChannels(_settings.Current.WatchedChannels.Select(w => w.Id));
}
