namespace PathForge.App.Services;

/// <summary>File dialogs and message boxes, abstracted so that view models stay UI-independent.</summary>
public interface IDialogService
{
    string? OpenFile(string title, string filter);

    string? SaveFile(string title, string filter, string defaultFileName);

    void ShowError(string message);

    /// <summary>Returns true for "Yes", false for "No" and null for "Cancel".</summary>
    bool? AskYesNoCancel(string message);

    /// <summary>Yes/No question; true for "Yes".</summary>
    bool Confirm(string message);

    void ShowInfo(string message);
}
