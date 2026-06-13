using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DataVortex.App.Converters;

/// <summary>Formats a byte count as a human-readable size (e.g. 1.2 MB).</summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "";
        double bytes = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return $"{bytes:0.#} {units[u]}";
    }

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Maps a status enum (DownloadStatus / ProcessingStatus / ConnectionState) to a themed brush
/// resolved from application resources, so colours follow the active theme.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            // Pipeline + connection states
            "Completed" or "Connected" => "Success",
            "Downloading" or "Processing" or "Connecting" or "Reconnecting"
                or "WaitingForCode" or "WaitingForPassword" => "Accent",
            "Retrying" or "Warning" => "Warning",
            "Failed" or "Error" or "Fatal" => "Error",
            "Ignored" or "Canceled" or "CanceledByUser" or "Disconnected" or "Debug" or "Verbose" => "Muted",
            _ => "TextSecondary"
        };
        return Application.Current.TryFindResource(key) as Brush
               ?? Application.Current.TryFindResource("TextSecondary") as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>true =&gt; Collapsed, false =&gt; Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Returns Visible when the bound string is non-empty, otherwise Collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
