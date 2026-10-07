using CommunityToolkit.Mvvm.ComponentModel;

namespace PathForge.App.ViewModels;

/// <summary>One line of the G-code panel; <see cref="IsCurrent"/> marks the line being simulated or machined.</summary>
public sealed partial class GcodeLineViewModel : ObservableObject
{
    public GcodeLineViewModel(int number, string text)
    {
        Number = number;
        Text = text;
    }

    /// <summary>1-based line number in the program.</summary>
    public int Number { get; }

    public string Text { get; }

    [ObservableProperty]
    private bool isCurrent;
}
