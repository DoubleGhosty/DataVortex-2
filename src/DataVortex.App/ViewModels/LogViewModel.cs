using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Logging;
using DataVortex.App.Services;

namespace DataVortex.App.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private const int MaxEntries = 500;
    private readonly IUiDispatcher _ui;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogViewModel(ObservableLogSink sink, IUiDispatcher ui)
    {
        _ui = ui;
        sink.Emitted += OnEmitted;
    }

    private void OnEmitted(LogEntry entry) => _ui.Post(() =>
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    });

    [RelayCommand]
    private void Clear() => Entries.Clear();
}
