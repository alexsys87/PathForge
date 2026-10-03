using CommunityToolkit.Mvvm.ComponentModel;

namespace PathForge.App.ViewModels;

/// <summary>One drawing layer in the layer list.</summary>
public sealed class LayerViewModel : ObservableObject
{
    private readonly Action<LayerViewModel> _visibilityChanged;
    private bool _isVisible;

    public LayerViewModel(string name, int count, bool isVisible, Action<LayerViewModel> visibilityChanged)
    {
        Name = name;
        Count = count;
        _isVisible = isVisible;
        _visibilityChanged = visibilityChanged;
    }

    public string Name { get; }

    public int Count { get; }

    public string Display => $"{(string.IsNullOrEmpty(Name) ? "(без имени)" : Name)} — {Count}";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                _visibilityChanged(this);
            }
        }
    }
}
