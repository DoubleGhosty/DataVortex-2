using System.Windows;
using DataVortex.App.ViewModels;

namespace DataVortex.App.Views;

public partial class LicenseActivationDialog : Window
{
    public LicenseActivationDialog(LicenseActivationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool activated)
    {
        try { DialogResult = activated; }
        catch (InvalidOperationException) { /* not shown modally */ }
        Close();
    }
}
