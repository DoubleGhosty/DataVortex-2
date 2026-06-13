using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.App.Themes;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Backfill;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using DataVortex.Core.Pipeline;
using DataVortex.Core.Security;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ITelegramService _telegram;
    private readonly IPipelineCoordinator _coordinator;
    private readonly IBackfillService _backfill;
    private readonly ISettingsService _settings;
    private readonly CredentialStore _credentials;
    private readonly IDialogService _dialogs;
    private readonly IStorageService _storage;
    private readonly IDownloadDeduplicator _dedup;
    private readonly ChannelsViewModel _channels;
    private readonly AccountsViewModel _accounts;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ShellViewModel> _log;

    public ObservableCollection<NavSection> Sections { get; }

    [ObservableProperty] private NavSection? selectedSection;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenLogin))]
    private ConnectionState connection = ConnectionState.Disconnected;
    [ObservableProperty] private string connectionText = "Disconnected";
    [ObservableProperty] private string userName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool isPaused;

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    private bool _pipelineStarted;
    private bool _loginOpen;

    public ShellViewModel(
        ITelegramService telegram, IPipelineCoordinator coordinator, IBackfillService backfill, ISettingsService settings,
        CredentialStore credentials, IDialogService dialogs, IStorageService storage, IDownloadDeduplicator dedup,
        DashboardViewModel dashboard, ChannelsViewModel channels, QueuesViewModel queues,
        FilesViewModel files, StatsViewModel stats, AccountsViewModel accounts, LogViewModel logs, SettingsViewModel settingsPanel,
        IUiDispatcher ui, ILogger<ShellViewModel> log)
    {
        _telegram = telegram;
        _coordinator = coordinator;
        _backfill = backfill;
        _accounts = accounts;
        _settings = settings;
        _credentials = credentials;
        _dialogs = dialogs;
        _storage = storage;
        _dedup = dedup;
        _channels = channels;
        _ui = ui;
        _log = log;

        Sections = new ObservableCollection<NavSection>
        {
            new() { Name = "Dashboard", Glyph = "", ViewModel = dashboard },
            new() { Name = "Channels",  Glyph = "", ViewModel = channels },
            new() { Name = "Queues",    Glyph = "", ViewModel = queues },
            new() { Name = "Files",     Glyph = "", ViewModel = files },
            new() { Name = "Stats", Glyph = "", ViewModel = stats },
            new() { Name = "Accounts",  Glyph = "", ViewModel = accounts },
            new() { Name = "Settings",  Glyph = "", ViewModel = settingsPanel },
            new() { Name = "Logs",      Glyph = "", ViewModel = logs }
        };
        SelectedSection = Sections[0];

        Connection = telegram.State;
        ConnectionText = telegram.State.ToString();
        _telegram.StateChanged += OnStateChanged;

        // Refresh accounts count whenever a file is archived
        _coordinator.FileArchived += _ => _ui.Post(() => _accounts.Refresh());
    }

    private void OnStateChanged(ConnectionState state) => _ui.Post(() =>
    {
        Connection = state;
        ConnectionText = Humanize(state);
        UserName = _telegram.LoggedInUser ?? "";

        switch (state)
        {
            case ConnectionState.Connected:
                StartPipelineOnce();
                break;
            case ConnectionState.WaitingForCode:
            case ConnectionState.WaitingForPassword:
            case ConnectionState.Failed:
                ShowLoginIfNeeded();
                break;
        }
    });

    private void StartPipelineOnce()
    {
        if (_pipelineStarted) return;
        _pipelineStarted = true;
        _channels.ApplyWatchedFromSettings();
        _coordinator.Start();
        _backfill.Start();
        IsPaused = _coordinator.IsPaused;
        _ = _channels.RefreshCommand.ExecuteAsync(null);
        _log.LogInformation("Pipeline started after successful connection");
    }

    public async Task InitializeAsync()
    {
        var hash = _credentials.LoadApiHash();
        bool canAutoConnect = _settings.Current.ApiId > 0
                              && !string.IsNullOrWhiteSpace(hash)
                              && !string.IsNullOrWhiteSpace(_settings.Current.PhoneNumber);

        if (canAutoConnect)
        {
            var creds = new TelegramCredentials(_settings.Current.ApiId, hash!, _settings.Current.PhoneNumber);
            try
            {
                // A valid saved session connects silently; if it needs a code, the state change opens login.
                await Task.Run(() => _telegram.ConnectAsync(creds)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Auto-connect failed; prompting for login");
                _ui.Post(ShowLoginIfNeeded);
            }
        }
        else
        {
            _ui.Post(ShowLoginIfNeeded);
        }
    }

    private void ShowLoginIfNeeded()
    {
        if (_loginOpen || _telegram.State == ConnectionState.Connected) return;
        _loginOpen = true;
        try { _dialogs.ShowLogin(); }
        finally { _loginOpen = false; }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_coordinator.IsPaused) _coordinator.Resume();
        else _coordinator.Pause();
        IsPaused = _coordinator.IsPaused;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _settings.Current.Theme = next;
        _settings.Save();
        ThemeManager.Apply(next);
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(_storage.Paths.Root);

    [RelayCommand]
    private void ClearDedup()
    {
        if (!_dialogs.Confirm(
                $"Clear the de-duplication memory ({_dedup.Count} archive(s))?\nArchives may then be downloaded again.",
                "Clear dedup store"))
            return;
        _dedup.Clear();
        _log.LogInformation("Dedup store cleared from the UI");
    }

    [RelayCommand]
    private void Login() => ShowLoginIfNeeded();

    public bool CanOpenLogin => Connection != ConnectionState.Connected;

    private static string Humanize(ConnectionState state) => state switch
    {
        ConnectionState.WaitingForCode => "Waiting for code",
        ConnectionState.WaitingForPassword => "Waiting for password",
        _ => state.ToString()
    };
}
