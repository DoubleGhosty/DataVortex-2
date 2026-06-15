using Serilog.Core;
using Serilog.Events;

namespace DataVortex.App.Logging;

public sealed record LogEntry(DateTime Timestamp, string Level, string Message);

/// <summary>
/// A Serilog sink that re-publishes every log event as a .NET event so the UI can show a live log.
/// Exposed as a singleton so it can be wired into the Serilog pipeline before the DI container exists.
/// </summary>
public sealed class ObservableLogSink : ILogEventSink
{
    public static ObservableLogSink Instance { get; } = new();

    public event Action<LogEntry>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        // Surface the exception reason in the live log too (the file sink already has the full stack).
        if (logEvent.Exception is not null)
            message += " — " + logEvent.Exception.Message;
        var entry = new LogEntry(
            logEvent.Timestamp.LocalDateTime,
            logEvent.Level.ToString(),
            message);
        Emitted?.Invoke(entry);
    }
}
