using System.Windows;
using DataVortex.App.ViewModels;

namespace DataVortex.App.Views;

public partial class ShellWindow : Window
{
    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
