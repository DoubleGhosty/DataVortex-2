namespace DataVortex.Core.Models;

/// <summary>
/// Resolves and creates the on-disk folder layout:
/// <code>
/// {Root}/
///   downloads/   raw files pulled from Telegram (one subfolder per channel)
///   extracted/   *.txt files extracted from archives
///   metadata/    one JSON record per processed file
///   logs/        Serilog rolling files
///   session/     encrypted WTelegram session + update state + protected credentials
/// </code>
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Downloads { get; }
    public string Extracted { get; }
    public string Metadata { get; }
    public string Logs { get; }
    public string Session { get; }

    public AppPaths(string root)
    {
        Root = root;
        Downloads = Path.Combine(root, "downloads");
        Extracted = Path.Combine(root, "extracted");
        Metadata = Path.Combine(root, "metadata");
        Logs = Path.Combine(root, "logs");
        Session = Path.Combine(root, "session");
    }

    /// <summary>Default data root: a "data" folder next to the executable.</summary>
    public static AppPaths CreateDefault()
        => new(Path.Combine(AppContext.BaseDirectory, "data"));

    public AppPaths EnsureCreated()
    {
        foreach (var dir in new[] { Root, Downloads, Extracted, Metadata, Logs, Session })
            Directory.CreateDirectory(dir);
        return this;
    }

    public string SessionFile => Path.Combine(Session, "DataVortex.session");
    public string UpdateStateFile => Path.Combine(Session, "updates.state");
    public string CredentialsFile => Path.Combine(Session, "credentials.dat");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>DPAPI-encrypted local licence state (signed lease + bound fingerprint + last-seen clock).</summary>
    public string LicenseFile => Path.Combine(Session, "license.dat");
}
