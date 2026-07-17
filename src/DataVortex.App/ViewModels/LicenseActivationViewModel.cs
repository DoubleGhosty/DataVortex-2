using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.Core.Licensing;

namespace DataVortex.App.ViewModels;

/// <summary>Drives the licence activation screen: takes a key, calls the manager, and reports success (which
/// closes the dialog) or a user-facing error. All the real logic lives in <see cref="ILicenseManager"/>.</summary>
public sealed partial class LicenseActivationViewModel : ObservableObject
{
    private readonly ILicenseManager _manager;

    [ObservableProperty] private string licenseKey = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool isBusy;

    /// <summary>Raised when the dialog should close — <c>true</c> if the licence is now active.</summary>
    public event Action<bool>? CloseRequested;

    public LicenseActivationViewModel(ILicenseManager manager) => _manager = manager;

    [RelayCommand]
    private async Task ActivateAsync()
    {
        Error = "";
        var key = (LicenseKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key)) { Error = "Saisissez votre clé de licence."; return; }

        IsBusy = true;
        StatusText = "Activation en cours…";
        try
        {
            var status = await _manager.ActivateAsync(key).ConfigureAwait(true);
            if (status.IsUsable)
            {
                StatusText = "Licence activée.";
                CloseRequested?.Invoke(true);
            }
            else
            {
                Error = status.Message ?? "Activation impossible.";
                StatusText = "";
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Quit() => CloseRequested?.Invoke(false);
}
