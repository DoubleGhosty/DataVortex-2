using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Licensing;
using DataVortex.Core.Models;
using DataVortex.Licensing;

namespace DataVortex.App.ViewModels;

public sealed record ExtractedFile(string Name, string FullPath, string Folder, long SizeBytes, DateTime Modified);

public sealed partial class FilesViewModel : ObservableObject
{
    private readonly IStorageService _storage;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ILicenseGate _gate;

    public ObservableCollection<ExtractedFile> Files { get; } = new();

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private ExtractedFile? selectedFile;
    [ObservableProperty] private int fileCount;

    public FilesViewModel(IStorageService storage, IPipelineCoordinator coordinator, IDialogService dialogs,
        IUiDispatcher ui, ILicenseGate gate)
    {
        _storage = storage;
        _dialogs = dialogs;
        _ui = ui;
        _gate = gate;
        coordinator.FileArchived += OnArchived;
        Reload();
    }

    partial void OnSearchTextChanged(string value) => Reload();

    private void OnArchived(FileRecord record) => _ui.Post(() =>
    {
        foreach (var path in record.ExtractedTextFiles)
        {
            if (!Matches(path)) continue;
            Files.Insert(0, ToItem(path));
        }
        FileCount = Files.Count;
    });

    private bool Matches(string path) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        Path.GetFileName(path).Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    private static ExtractedFile ToItem(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return new ExtractedFile(fi.Name, fi.FullName, fi.DirectoryName ?? "", fi.Exists ? fi.Length : 0,
                fi.Exists ? fi.LastWriteTime : DateTime.Now);
        }
        catch
        {
            return new ExtractedFile(Path.GetFileName(path), path, Path.GetDirectoryName(path) ?? "", 0, DateTime.Now);
        }
    }

    private void Reload()
    {
        Files.Clear();
        foreach (var path in _storage.EnumerateExtractedFiles(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText))
            Files.Add(ToItem(path));
        FileCount = Files.Count;
    }

    [RelayCommand] private void Refresh() => Reload();

    // Export capability gate (dispersed, silent): accessing the extracted results is a licensed capability too.
    [RelayCommand] private void OpenExtractedFolder() { if (_gate.Allows(Capability.Export)) _dialogs.OpenFolder(_storage.Paths.Extracted); }
    [RelayCommand] private void OpenFile(ExtractedFile? file) { if (file is not null && _gate.Allows(Capability.Export)) _dialogs.OpenFile(file.FullPath); }
    [RelayCommand] private void OpenContainingFolder(ExtractedFile? file) { if (file is not null && _gate.Allows(Capability.Export)) _dialogs.OpenFolder(file.FullPath); }
}
