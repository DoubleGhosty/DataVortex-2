using System.Windows;
using DataVortex.App.ViewModels;

namespace DataVortex.App.Views;

public partial class LoginDialog : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginDialog(LoginViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // PasswordBox cannot be data-bound (by design); push changes into the VM manually.
        PasswordInput.PasswordChanged += (_, _) => _viewModel.Password = PasswordInput.Password;

        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.Cleanup();
    }

    private void OnCloseRequested(bool success)
    {
        // The dialog is always shown via ShowDialog(); guard anyway in case it isn't.
        try { DialogResult = success; }
        catch (InvalidOperationException) { /* not shown modally */ }
        Close();
    }
}
