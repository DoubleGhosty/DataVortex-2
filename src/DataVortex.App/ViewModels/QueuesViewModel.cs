using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;

namespace DataVortex.App.ViewModels;

/// <summary>
/// Live view of both queues plus completed history. The job objects are the very instances the pipeline
/// mutates (they raise PropertyChanged), so status/progress cells update in place; this VM only manages
/// membership (active vs. history) on the UI thread and exposes per-item cancel/retry.
/// </summary>
public sealed partial class QueuesViewModel : ObservableObject
{
    private const int MaxHistory = 500;
    private readonly IPipelineCoordinator _coordinator;
    private readonly IUiDispatcher _ui;
    private readonly HashSet<Guid> _activeDownloads = new();
    private readonly HashSet<Guid> _activeProcessing = new();

    public ObservableCollection<DownloadJob> DownloadQueue { get; } = new();
    public ObservableCollection<ProcessingJob> ProcessingQueue { get; } = new();
    public ObservableCollection<DownloadJob> History { get; } = new();

    public QueuesViewModel(IPipelineCoordinator coordinator, IUiDispatcher ui)
    {
        _coordinator = coordinator;
        _ui = ui;
        coordinator.DownloadJobChanged += OnDownloadChanged;
        coordinator.ProcessingJobChanged += OnProcessingChanged;
    }

    private void OnDownloadChanged(DownloadJob job) => _ui.Post(() =>
    {
        bool active = job.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Retrying;
        if (active)
        {
            History.Remove(job); // a retried job leaves the history and re-enters the live queue
            if (_activeDownloads.Add(job.Id)) DownloadQueue.Insert(0, job);
        }
        else
        {
            if (_activeDownloads.Remove(job.Id)) DownloadQueue.Remove(job);
            if (job.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.CanceledByUser)
            {
                if (!History.Contains(job)) History.Insert(0, job);
                while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
            }
        }
    });

    private void OnProcessingChanged(ProcessingJob job) => _ui.Post(() =>
    {
        bool active = job.Status is ProcessingStatus.Queued or ProcessingStatus.Processing;
        if (active)
        {
            if (_activeProcessing.Add(job.Id)) ProcessingQueue.Insert(0, job);
        }
        else
        {
            if (_activeProcessing.Remove(job.Id)) ProcessingQueue.Remove(job);
        }
    });

    [RelayCommand]
    private void CancelDownload(DownloadJob? job)
    {
        if (job is not null) _coordinator.CancelDownload(job);
    }

    [RelayCommand]
    private void RetryDownload(DownloadJob? job)
    {
        if (job is not null) _coordinator.RetryDownload(job);
    }
}
