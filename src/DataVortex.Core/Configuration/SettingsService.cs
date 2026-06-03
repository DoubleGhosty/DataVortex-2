using System.Text.Json;

namespace DataVortex.Core.Configuration;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
    event Action<AppSettings>? Changed;
}

/// <summary>Loads/saves <see cref="AppSettings"/> as indented JSON. Tolerant of a missing/corrupt file.</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly string _path;
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public SettingsService(string path)
    {
        _path = path;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt settings — fall back to defaults rather than crashing on startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
        Changed?.Invoke(Current);
    }
}
