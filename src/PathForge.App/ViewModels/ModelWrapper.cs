using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PathForge.App.ViewModels;

/// <summary>Base for view models that edit a core model object in place and report every change.</summary>
public abstract class ModelWrapper : ObservableObject
{
    private readonly Action _changed;

    protected ModelWrapper(Action changed)
    {
        _changed = changed;
    }

    /// <summary>Assigns a model value, raises PropertyChanged and notifies the owner.</summary>
    protected void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        assign(value);
        OnPropertyChanged(name);
        OnModelChanged();
        _changed();
    }

    /// <summary>Re-reads all displayed values (changed outside the editor, or the interface language changed).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Tells the owner that the model changed outside of <see cref="Set{T}"/> (e.g. bulk updates).</summary>
    protected void NotifyOwner() => _changed();

    /// <summary>Hook for derived classes to refresh dependent display properties.</summary>
    protected virtual void OnModelChanged()
    {
    }
}
