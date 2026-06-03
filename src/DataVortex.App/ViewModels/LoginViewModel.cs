using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using DataVortex.Core.Security;
using Microsoft.Extensions.Logging;

namespace DataVortex.App.ViewModels;

public enum LoginStage { Credentials, Connecting, Code, Password, Done }

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly ITelegramService _telegram;
    private readonly ISettingsService _settings;
    private readonly CredentialStore _credentials;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<LoginViewModel> _log;

    [ObservableProperty] private int apiId;
    [ObservableProperty] private string apiHash = "";
    [ObservableProperty] private string phoneNumber = "";
    [ObservableProperty] private string code = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private LoginStage stage = LoginStage.Credentials;

    public event Action<bool>? CloseRequested;

    public LoginViewModel(ITelegramService telegram, ISettingsService settings, CredentialStore credentials,
        IUiDispatcher ui, ILogger<LoginViewModel> log)
    {
        _telegram = telegram;
        _settings = settings;
        _credentials = credentials;
        _ui = ui;
        _log = log;

        ApiId = settings.Current.ApiId;
        PhoneNumber = settings.Current.PhoneNumber;
        ApiHash = credentials.LoadApiHash() ?? "";

        // If a connection attempt is already in flight (auto-connect), show the right stage immediately.
        Stage = telegram.State switch
        {
            ConnectionState.WaitingForCode => LoginStage.Code,
            ConnectionState.WaitingForPassword => LoginStage.Password,
            ConnectionState.Connecting => LoginStage.Connecting,
            _ => LoginStage.Credentials
        };

        _telegram.VerificationRequested += OnVerificationRequested;
        _telegram.StateChanged += OnStateChanged;
    }

    private void OnVerificationRequested(string what) => _ui.Post(() =>
    {
        Stage = what == "password" ? LoginStage.Password : LoginStage.Code;
        IsBusy = false;
    });

    private void OnStateChanged(ConnectionState state) => _ui.Post(() =>
    {
        switch (state)
        {
            case ConnectionState.Connected:
                Stage = LoginStage.Done;
                CloseRequested?.Invoke(true);
                break;
            case ConnectionState.Failed:
                Error = "Connection failed. Check your credentials and try again.";
                IsBusy = false;
                Stage = LoginStage.Credentials;
                break;
        }
    });

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Error = "";
        if (ApiId <= 0 || string.IsNullOrWhiteSpace(ApiHash) || string.IsNullOrWhiteSpace(PhoneNumber))
        {
            Error = "API ID, API hash and phone number are all required.";
            return;
        }

        _settings.Current.ApiId = ApiId;
        _settings.Current.PhoneNumber = PhoneNumber.Trim();
        _settings.Save();
        _credentials.SaveApiHash(ApiHash.Trim());

        IsBusy = true;
        Stage = LoginStage.Connecting;
        try
        {
            var creds = new TelegramCredentials(ApiId, ApiHash.Trim(), PhoneNumber.Trim());
            await Task.Run(() => _telegram.ConnectAsync(creds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connect failed");
            _ui.Post(() => { Error = ex.Message; IsBusy = false; Stage = LoginStage.Credentials; });
        }
    }

    [RelayCommand]
    private void SubmitCode()
    {
        if (string.IsNullOrWhiteSpace(Code)) { Error = "Enter the code you received."; return; }
        Error = "";
        IsBusy = true;
        _telegram.ProvideVerificationCode(Code.Trim());
    }

    [RelayCommand]
    private void SubmitPassword()
    {
        Error = "";
        IsBusy = true;
        _telegram.ProvidePassword(Password);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    public void Cleanup()
    {
        _telegram.VerificationRequested -= OnVerificationRequested;
        _telegram.StateChanged -= OnStateChanged;
    }
}
