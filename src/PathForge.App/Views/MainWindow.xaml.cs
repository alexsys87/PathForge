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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentGcodeLine))
            {
                ShowCurrentGcodeLine();
            }
        };
        DataContext = _viewModel;
        Drop += OnFileDrop;

        // Keyboard and joystick jogging works only while this window is active and the control panel is shown.
        Activated += (_, _) => UpdateJogInputContext();
        Deactivated += (_, _) => UpdateJogInputContext();
        DependencyPropertyDescriptor.FromProperty(TabItem.IsSelectedProperty, typeof(TabItem))
            .AddValueChanged(ControlTab, (_, _) => UpdateJogInputContext());
        PreviewKeyDown += OnJogKey;
        PreviewKeyUp += OnJogKey;

        RestoreLayout(App.Preferences.Window);
    }

    /// <summary>Keeps the highlighted G-code line in view (nothing moves while it is visible).</summary>
    private void ShowCurrentGcodeLine()
    {
        if (_viewModel.CurrentGcodeLine is { } line && GcodeList.IsVisible)
        {
            GcodeList.ScrollIntoView(line);
        }
    }

    private void OnShowCurrentGcodeLine(object sender, RoutedEventArgs e) => ShowCurrentGcodeLine();

    /// <summary>Ctrl+C in the G-code panel: the selected lines in program order, or the whole program.</summary>
    private void OnCopyGcode(object sender, ExecutedRoutedEventArgs e)
    {
        var selected = GcodeList.SelectedItems.OfType<GcodeLineViewModel>().OrderBy(l => l.Number).Select(l => l.Text).ToList();
        CopyText(selected.Count > 0 ? string.Join(Environment.NewLine, selected) : AllGcodeText());
        e.Handled = true;
    }

    private void OnCopyAllGcode(object sender, RoutedEventArgs e) => CopyText(AllGcodeText());

    private string AllGcodeText() => string.Join(Environment.NewLine, _viewModel.GcodeLines.Select(l => l.Text));

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is held by another program: nothing to do but leave it.
        }
    }

    /// <summary>
    /// Puts the window and its panels back as they were at the last close. A window that would now be off every
    /// screen (a monitor was unplugged, the resolution changed) opens centred instead.
    /// </summary>
    private void RestoreLayout(WindowLayout? layout)
    {
        if (layout is null)
        {
            return;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(layout.Left, layout.Top, Math.Max(MinWidth, layout.Width), Math.Max(MinHeight, layout.Height));
        var visible = Rect.Intersect(screen, bounds);
        if (layout.Width > 0 && layout.Height > 0 && !visible.IsEmpty && visible.Width >= 200 && visible.Height >= 100 &&
            bounds.Top >= screen.Top - 1)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Min(bounds.Width, screen.Width);
            Height = Math.Min(bounds.Height, screen.Height);
        }

        if (layout.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (layout.LeftPanelWidth > 0)
        {
            LeftPanelColumn.Width = new GridLength(Math.Clamp(layout.LeftPanelWidth, LeftPanelColumn.MinWidth, 1200));
        }

        if (layout.BottomPanelHeight > 0)
        {
            BottomPanelRow.Height = new GridLength(Math.Clamp(layout.BottomPanelHeight, BottomPanelRow.MinHeight, 900));
        }

        SelectTab(LeftTabs, layout.LeftTab);
        SelectTab(ViewTabs, layout.ViewTab);
        SelectTab(BottomTabs, layout.BottomTab);
    }

    private static void SelectTab(TabControl tabs, int index)
    {
        if (index >= 0 && index < tabs.Items.Count)
        {
            tabs.SelectedIndex = index;
        }
    }

    /// <summary>The window as it is now; when maximized or minimized, the bounds it returns to.</summary>
    private WindowLayout CurrentLayout()
    {
        var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return new WindowLayout
        {
            Left = normal.Left,
            Top = normal.Top,
            Width = normal.Width,
            Height = normal.Height,
            Maximized = WindowState == WindowState.Maximized,
            LeftPanelWidth = LeftPanelColumn.ActualWidth,
            BottomPanelHeight = BottomPanelRow.ActualHeight,
            LeftTab = LeftTabs.SelectedIndex,
            ViewTab = ViewTabs.SelectedIndex,
            BottomTab = BottomTabs.SelectedIndex,
        };
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
        else
        {
            App.Preferences.Window = CurrentLayout();
            App.Preferences.Save();
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
