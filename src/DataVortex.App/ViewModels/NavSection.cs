namespace DataVortex.App.ViewModels;

/// <summary>One entry in the left navigation rail. <see cref="ViewModel"/> is resolved to a view via the
/// DataTemplates declared in App.xaml.</summary>
public sealed class NavSection
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public object ViewModel { get; init; } = default!;
}
