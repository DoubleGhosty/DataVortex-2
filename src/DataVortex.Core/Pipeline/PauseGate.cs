namespace DataVortex.Core.Pipeline;

/// <summary>
/// Async manual-reset gate shared by both pipelines. Workers <c>await WaitAsync()</c> before taking the
/// next item; while paused they suspend (without spinning) until <see cref="Resume"/> is called.
/// </summary>
public sealed class PauseGate
{
    private readonly object _gate = new();
    private volatile TaskCompletionSource _tcs = CreateCompleted();

    public bool IsPaused { get; private set; }

    private static TaskCompletionSource CreateCompleted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public Task WaitAsync(CancellationToken ct)
    {
        var tcs = _tcs;
        if (tcs.Task.IsCompleted) return Task.CompletedTask;
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (IsPaused) return;
            IsPaused = true;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsPaused) return;
            IsPaused = false;
            _tcs.TrySetResult();
        }
    }
}
