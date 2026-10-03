using System.ComponentModel;
using System.IO;
using System.Windows;
using PathForge.App.Services;
using PathForge.App.ViewModels;

namespace PathForge.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new WpfDialogService(), App.Appearance);
        _viewModel.ZoomToFitRequested += (_, _) => Viewport.ZoomToFit();
        DataContext = _viewModel;
        Drop += OnFileDrop;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.Control.ConfirmClose() || !_viewModel.ConfirmDiscardChanges())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Control.Dispose();
        base.OnClosed(e);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>A DXF or SVG file dropped onto the window is imported.</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
            Path.GetExtension(files[0]).ToLowerInvariant() is ".dxf" or ".svg")
        {
            _viewModel.ImportDrawingFile(files[0]);
        }
    }
}
