using System.Diagnostics;
using System.IO;
using System.Windows;
using DataVortex.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DataVortex.App.Services;

public interface IDialogService
{
    bool ShowLogin();
    void OpenFolder(string path);
    void OpenFile(string path);

    /// <summary>Opens a file picker; returns the chosen path or <c>null</c> if cancelled.</summary>
    string? PickFile(string filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*");

    bool Confirm(string message, string title = "DataVortex");
}

public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _provider;
    public DialogService(IServiceProvider provider) => _provider = provider;

    public bool ShowLogin()
    {
        var dialog = _provider.GetRequiredService<LoginDialog>();
        dialog.Owner = Application.Current.MainWindow;
        return dialog.ShowDialog() == true;
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    public void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    public string? PickFile(string filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*")
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public bool Confirm(string message, string title = "DataVortex")
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
