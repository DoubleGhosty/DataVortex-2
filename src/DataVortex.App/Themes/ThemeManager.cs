using System.Windows;
using DataVortex.Core.Configuration;

namespace DataVortex.App.Themes;

/// <summary>Swaps the theme colour dictionary (merged index 0) at runtime. Controls reference colours
/// via <c>DynamicResource</c>, so the whole UI re-themes instantly.</summary>
public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        var name = theme == AppTheme.Light ? "Light" : "Dark";
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute)
        };
        Application.Current.Resources.MergedDictionaries[0] = dict;
    }
}
