using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataVortex.Core.Models;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base so Core DTOs can be data-bound directly by the
/// WPF layer and mutated from background pipeline threads. WPF marshals scalar property-change
/// notifications to the dispatcher automatically, so this keeps the Core library free of any UI
/// framework dependency while still giving the dashboard live updates.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
