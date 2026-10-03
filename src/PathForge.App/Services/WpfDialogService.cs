using System.Windows;
using Microsoft.Win32;

namespace PathForge.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private const string Caption = "PathForge";

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool? AskYesNoCancel(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowInfo(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
}
