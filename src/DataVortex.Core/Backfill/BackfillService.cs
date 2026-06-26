using System.Text.Json;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Backfill;

public enum BackfillState { Disabled, WaitingForIdle, Scanning, Completed }

public sealed class BackfillStatus
{
    public BackfillState State { get; init; }
    public string CurrentChannel { get; init; } = "";
    public long TotalScanned { get; init; }
    public long TotalEnqueued { get; init; }
    public int ChannelsCompleted { get; init; }
    public int ChannelsTotal { get; init; }
}

public interface IBackfillService
{
    event Action<BackfillStatus>? StatusChanged;
    BackfillStatus Status { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
    void Start();
    Task StopAsync();
}

/// <summary>
/// When the pipeline has had nothing to do for a configurable idle period, this service walks each watched
/// channel's history (newest → oldest, paged) and feeds every archive message back through the normal
/// detection path — so the existing de-duplicator skips anything already done and only genuinely new old
/// archives get queued. Per-channel offsets are persisted, so it resumes where it left off across restarts.
/// Live messages always take priority: any pipeline activity pauses the backfill until idle again.
/// </summary>
public sealed class BackfillService : IBackfillService, IDisposable
{
    public sealed class ChannelProgress
    {
        public int OffsetId { get; set; }
        public bool Completed { get; set; }
    }

    private readonly ITelegramService _telegram;
    private readonly IPipelineCoordinator _coordinator;
    private readonly ISettingsService _settings;
    private readonly ILogger<BackfillService> _log;
    private readonly string _statePath;

    private readonly object _gate = new();
    private Dictionary<long, ChannelProgress> _progress = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _totalScanned;
    private long _totalEnqueued;
    private volatile bool _enabled;
    private int _rrIndex = -1; // round-robin cursor over watched channels

    public BackfillStatus Status { get; private set; } = new() { State = BackfillState.Disabled };
    public event Action<BackfillStatus>? StatusChanged;
    public bool IsEnabled => _enabled;

    public BackfillService(ITelegramService telegram, IPipelineCoordinator coordinator,
        ISettingsService settings, AppPaths paths, ILoggerFactory loggerFactory)
    {
        _telegram = telegram;
        _coordinator = coordinator;
        _settings = settings;
        _log = loggerFactory.CreateLogger<BackfillService>();
        _statePath = Path.Combine(paths.Root, "backfill.json");
        _enabled = settings.Current.BackfillEnabled;
        Load();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _settings.Current.BackfillEnabled = enabled;
        _settings.Save();
        _log.LogInformation("Backfill {State}", enabled ? "enabled" : "disabled");
        Publish(enabled ? BackfillState.WaitingForIdle : BackfillState.Disabled, "");
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.LogInformation("Backfill service started");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* cancellation */ }
        }
        Save();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await _telegram.EnsureDialogsLoadedAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                if (!_enabled)
                {
                    Publish(BackfillState.Disabled, "");
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    continue;
                }

                // Wait once for the pipeline to be idle for the configured period before a scanning burst.
                if (!await WaitForIdleAsync(ct).ConfigureAwait(false))
                    continue;

                // Scanning burst: keep digging page after page while the pipeline stays idle. A page that
                // enqueues nothing must NOT re-incur the full idle delay — only enqueuing new work (or live
                // activity) sends us back to WaitForIdle so the queue can drain first.
                while (!ct.IsCancellationRequested && _enabled && IsPipelineIdle())
                {
                    var next = NextChannelToScan();
                    if (next is null)
                    {
                        Publish(BackfillState.Completed, "");
                        await Task.Delay(30000, ct).ConfigureAwait(false); // re-check (new channels / cleared state)
                        break;
                    }

                    var (channelId, title) = next.Value;
                    Publish(BackfillState.Scanning, title);

                    int offset;
                    lock (_gate) offset = _progress.TryGetValue(channelId, out var p) ? p.OffsetId : 0;

                    HistoryPage page;
                    try
                    {
                        page = await _telegram.ScanHistoryPageAsync(channelId, offset, _settings.Current.BackfillPageSize, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Backfill scan failed for {Channel}", title);
                        await Task.Delay(10000, ct).ConfigureAwait(false);
                        continue;
                    }

                    Interlocked.Add(ref _totalScanned, page.Scanned);
                    Interlocked.Add(ref _totalEnqueued, page.Enqueued);

                    // A page with zero messages means there is nothing (more) to dig here: either the end of the
                    // channel's history (Exhausted), or a channel we can't read — e.g. one we are not subscribed to,
                    // which never resolves (NextOffsetId stays put) and would otherwise be re-scanned ~3×/s forever.
                    // Either way mark it done so the round-robin drops it instead of hammering it.
                    bool unreachable = page.Scanned == 0 && !page.Exhausted;
                    bool done = page.Exhausted || page.Scanned == 0;
                    lock (_gate)
                    {
                        if (!_progress.TryGetValue(channelId, out var p)) _progress[channelId] = p = new ChannelProgress();
                        p.OffsetId = page.NextOffsetId;
                        if (done) p.Completed = true;
                    }
                    Save();

                    _log.LogInformation("Backfill {Channel}: scanned {Scanned}, enqueued {Enqueued} new archive(s){Done}",
                        title, page.Scanned, page.Enqueued,
                        unreachable ? " — inaccessible (non abonné ?), ignoré" : page.Exhausted ? " — channel complete" : "");
                    Publish(BackfillState.Scanning, title);

                    // Enqueued new work → step back to the idle wait so it can drain. Nothing new → keep digging.
                    if (page.Enqueued > 0)
                        break;

                    await Task.Delay(300, ct).ConfigureAwait(false); // stay gentle on the API
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Backfill loop crashed"); }
    }

    private async Task<bool> WaitForIdleAsync(CancellationToken ct)
    {
        var needed = TimeSpan.FromSeconds(Math.Max(10, _settings.Current.BackfillIdleSeconds));
        Publish(BackfillState.WaitingForIdle, Status.CurrentChannel);
        var idleSince = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (!_enabled) return false;

            if (!IsPipelineIdle())
            {
                idleSince = DateTime.UtcNow;
                await Task.Delay(3000, ct).ConfigureAwait(false);
                continue;
            }

            if (DateTime.UtcNow - idleSince >= needed) return true;
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>True when the pipeline has no live or queued work — the condition for backfill to scan.</summary>
    private bool IsPipelineIdle() =>
        _coordinator.DownloadQueueDepth == 0
        && _coordinator.ProcessingQueueDepth == 0
        && _coordinator.ActiveDownloads == 0
        && !_coordinator.IsPaused;

    /// <summary>Round-robin over watched channels (skipping completed ones), so the backfill scans the most
    /// recent page of EVERY channel in rotation instead of exhausting the first channel before moving on.</summary>
    private (long Id, string Title)? NextChannelToScan()
    {
        var channels = _settings.Current.WatchedChannels;
        if (channels.Count == 0) return null;

        for (int i = 0; i < channels.Count; i++)
        {
            _rrIndex = (_rrIndex + 1) % channels.Count;
            var w = channels[_rrIndex];
            bool done;
            lock (_gate) done = _progress.TryGetValue(w.Id, out var p) && p.Completed;
            if (!done) return (w.Id, w.Title);
        }
        return null; // every channel completed
    }

    private void Publish(BackfillState state, string channel)
    {
        int completed;
        lock (_gate) completed = _progress.Values.Count(p => p.Completed);
        Status = new BackfillStatus
        {
            State = _enabled ? state : BackfillState.Disabled,
            CurrentChannel = channel,
            TotalScanned = Interlocked.Read(ref _totalScanned),
            TotalEnqueued = Interlocked.Read(ref _totalEnqueued),
            ChannelsCompleted = completed,
            ChannelsTotal = _settings.Current.WatchedChannels.Count
        };
        StatusChanged?.Invoke(Status);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_statePath))
                _progress = JsonSerializer.Deserialize<Dictionary<long, ChannelProgress>>(File.ReadAllText(_statePath)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to load backfill state"); }
    }

    private void Save()
    {
        try
        {
            Dictionary<long, ChannelProgress> snapshot;
            lock (_gate) snapshot = new(_progress);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save backfill state"); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
