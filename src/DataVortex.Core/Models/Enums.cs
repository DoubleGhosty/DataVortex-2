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
    Canceled
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
