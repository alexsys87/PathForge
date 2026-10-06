using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PathForge.App.Services;
using PathForge.App.ViewModels;

namespace PathForge.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new WpfDialogService(), App.Appearance, App.Preferences);
        _viewModel.ZoomToFitRequested += (_, _) => Viewport.ZoomToFit();
        DataContext = _viewModel;
        Drop += OnFileDrop;

        // Keyboard and joystick jogging works only while this window is active and the control panel is shown.
        Activated += (_, _) => UpdateJogInputContext();
        Deactivated += (_, _) => UpdateJogInputContext();
        DependencyPropertyDescriptor.FromProperty(TabItem.IsSelectedProperty, typeof(TabItem))
            .AddValueChanged(ControlTab, (_, _) => UpdateJogInputContext());
        PreviewKeyDown += OnJogKey;
        PreviewKeyUp += OnJogKey;
    }

    private void UpdateJogInputContext() => _viewModel.Control.SetJogInputContext(IsActive && ControlTab.IsSelected);

    private void OnJogKey(object sender, KeyEventArgs e)
    {
        // Typing in a field (console, feeds) keeps the keys; shortcuts with Ctrl or Alt are left alone.
        if (e.IsDown && (e.OriginalSource is TextBoxBase or PasswordBox ||
                         (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0))
        {
            return;
        }

        if (_viewModel.Control.HandleJogKey(e.Key, e.IsDown, e.IsRepeat))
        {
            e.Handled = true;
        }
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
