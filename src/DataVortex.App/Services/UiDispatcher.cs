using System.Windows;

namespace DataVortex.App.Services;

/// <summary>Marshals work onto the WPF UI thread. Pipeline events fire on background threads, so any
/// mutation of an <c>ObservableCollection</c> bound to the UI must go through here.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher = Application.Current.Dispatcher;

    public void Post(Action action)
    {
        // Background pipeline/metrics events can fire while the app is closing; never marshal onto a
        // dispatcher that is shutting down (BeginInvoke would throw on a worker thread and crash the process).
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess()) { action(); return; }
        try { _dispatcher.BeginInvoke(action); }
        catch (Exception) { /* dispatcher shut down between the check and the call */ }
    }

    public Task InvokeAsync(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return Task.CompletedTask;
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        try { return _dispatcher.InvokeAsync(action).Task; }
        catch (Exception) { return Task.CompletedTask; }
    }
}
