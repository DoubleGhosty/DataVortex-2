namespace DataVortex.Core.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    WaitingForCode,
    WaitingForPassword,
    Connected,
    Reconnecting,
    Failed
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Retrying,
    Canceled,        // interrupted by app shutdown — kept in the resume store, retried next launch
    CanceledByUser   // explicitly cancelled in the UI — dropped from the resume store, not retried
}

public enum ProcessingStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Ignored
}

public enum ArchiveKind
{
    None,
    Zip,
    Rar,
    SevenZip,
    PlainText,
    Other
}
