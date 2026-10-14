using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Dlc32Flasher.Wpf;

/// <summary>
/// Everything needed to write or read the firmware of an MKS DLC32 through its USB port: the port and its speed, the
/// buttons, a progress bar and a log. No esptool is needed (see <see cref="Dlc32FirmwareFlasher"/>). Put it into any WPF
/// window, or use <see cref="Dlc32FlasherWindow"/>. Built in code, without XAML, so that it is one file to take along.
/// </summary>
public sealed class Dlc32FlasherControl : UserControl
{
    private readonly ComboBox _ports = new() { MinWidth = 110, Margin = new Thickness(0, 0, 4, 0) };
    private readonly Button _refresh = new() { Padding = new Thickness(8, 2, 8, 2) };
    private readonly ComboBox _bauds = new() { MinWidth = 110 };
    private readonly CheckBox _manual = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _write = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _read = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _cancel = new() { Padding = new Thickness(10, 3, 10, 3) };
    private readonly ProgressBar _progress = new() { Height = 16, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
        MinHeight = 120,
    };

    private CancellationTokenSource? _cts;
    private bool _writing;

    public Dlc32FlasherControl()
    {
        _refresh.Click += (_, _) => RefreshPorts();
        _write.Click += async (_, _) => await WriteWithDialogsAsync();
        _read.Click += async (_, _) => await ReadWithDialogsAsync();
        _cancel.Click += (_, _) => Cancel();
        foreach (var baud in Dlc32FirmwareFlasher.BaudRates)
        {
            _bauds.Items.Add(baud);
        }

        _bauds.SelectedItem = Dlc32FirmwareFlasher.DefaultBaud;
        Content = BuildLayout();
        ApplyTexts();
        RefreshPorts();
        UpdateButtons();
    }

    /// <summary>The board's port; the first port of the list when not set.</summary>
    public string? PortName
    {
        get => _ports.SelectedItem as string ?? (_ports.Text.Length > 0 ? _ports.Text : null);
        set
        {
            if (value is not null && !_ports.Items.Contains(value))
            {
                _ports.Items.Add(value);
            }

            _ports.SelectedItem = value;
        }
    }

    /// <summary>Speed of the port while flashing.</summary>
    public int Baud
    {
        get => _bauds.SelectedItem is int baud ? baud : Dlc32FirmwareFlasher.DefaultBaud;
        set
        {
            if (!_bauds.Items.Contains(value))
            {
                _bauds.Items.Add(value);
            }

            _bauds.SelectedItem = value;
        }
    }

    /// <summary>The board was put into the bootloader by hand: no reset through DTR/RTS.</summary>
    public bool BoardInBootloader
    {
        get => _manual.IsChecked == true;
        set => _manual.IsChecked = value;
    }

    /// <summary>An operation is running.</summary>
    public bool IsBusy => _cts is not null;

    /// <summary>A write is running: stopping it now leaves the board without a working firmware.</summary>
    public bool IsWriting => _cts is not null && _writing;

    /// <summary>
    /// Called on the UI thread before the port is opened, with its name. The host program frees the port here (closes its own
    /// connection to the board) and returns false to call the operation off.
    /// </summary>
    public Func<string, bool>? BeforePortUse { get; set; }

    /// <summary>Opens the port (a COM port of the computer by default). For other transports and for tests.</summary>
    public Func<string, IFlasherPort>? OpenPort { get; set; }

    /// <summary>Lines of the log, also for the host program (raised on the UI thread).</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>An operation ended (raised on the UI thread).</summary>
    public event EventHandler<FlasherReport>? Completed;

    /// <summary><see cref="IsBusy"/> changed.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>The texts are taken again (after <see cref="FlasherText.Russian"/> was changed).</summary>
    public void ApplyTexts()
    {
        _refresh.Content = FlasherText.T("Обновить", "Refresh");
        _refresh.ToolTip = FlasherText.T("Заново найти COM-порты (после подключения платы).", "Look for COM ports again (after the board was plugged in).");
        _manual.Content = FlasherText.T("Плата уже в загрузчике (без автосброса)", "The board is in the bootloader already (no auto-reset)");
        _manual.ToolTip = FlasherText.T(
            "Для платы, у которой не работает автосброс: замкните GPIO0 (BOOT) на землю, нажмите Reset, отпустите GPIO0 и включите этот флажок.",
            "For a board whose auto-reset does not work: connect GPIO0 (BOOT) to ground, press Reset, release GPIO0 and check this box.");
        _write.Content = FlasherText.T("Записать прошивку…", "Write firmware…");
        _write.ToolTip = FlasherText.T(
            "Записать полный образ флеш-памяти (файл прошивки MKS DLC32 или считанную копию) с адреса 0x0 и проверить его по MD5.",
            "Write a whole flash image (a firmware file of the MKS DLC32 or a copy read before) from address 0x0 and check it by MD5.");
        _read.Content = FlasherText.T("Считать прошивку…", "Read firmware…");
        _read.ToolTip = FlasherText.T(
            "Сохранить всю флеш-память платы в файл: прошивку и настройки. Загрузчик ESP32 читает медленно: 8 МБ — около 10 минут.",
            "Save the whole flash of the board into a file: the firmware and the settings. The ESP32 bootloader reads slowly: 8 MB take about 10 minutes.");
        _cancel.Content = FlasherText.T("Отмена", "Cancel");
        _portLabel.Text = FlasherText.T("Порт:", "Port:");
        _baudLabel.Text = FlasherText.T("Скорость:", "Speed:");
        _bauds.ToolTip = FlasherText.T(
            "Скорость порта во время прошивки. При ошибках связи выберите меньше.",
            "Speed of the port while flashing. Choose a lower one on communication errors.");
        if (!IsBusy && string.IsNullOrEmpty(_status.Text))
        {
            _status.Text = FlasherText.T(
                "Подключите плату MKS DLC32 кабелем USB и подайте питание 12–24 В.",
                "Connect the MKS DLC32 with a USB cable and power it with 12-24 V.");
        }
    }

    public void RefreshPorts()
    {
        var selected = PortName;
        _ports.Items.Clear();
        foreach (var name in SerialFlasherPort.PortNames())
        {
            _ports.Items.Add(name);
        }

        if (selected is not null && _ports.Items.Contains(selected))
        {
            _ports.SelectedItem = selected;
        }
        else if (_ports.Items.Count > 0)
        {
            _ports.SelectedIndex = 0;
        }
    }

    /// <summary>Stops the running operation.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Writes a whole flash image without asking anything (the image is checked all the same).</summary>
    public Task<FlasherReport> WriteFileAsync(string imagePath) =>
        RunAsync(write: true, (flasher, port, options, progress, token) => flasher.WriteFileAsync(port, imagePath, options, progress, token));

    /// <summary>Reads the whole flash into a file without asking anything.</summary>
    public Task<FlasherReport> ReadFileAsync(string outputPath) =>
        RunAsync(write: false, (flasher, port, options, progress, token) => flasher.ReadFileAsync(port, outputPath, options, progress, token));

    private readonly TextBlock _portLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _baudLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    private UIElement BuildLayout()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        portRow.Children.Add(_ports);
        portRow.Children.Add(_refresh);
        Place(grid, _portLabel, 0, 0);
        Place(grid, portRow, 0, 1);

        var baudRow = new StackPanel { Orientation = Orientation.Horizontal };
        baudRow.Children.Add(_bauds);
        Place(grid, _baudLabel, 1, 0);
        Place(grid, baudRow, 1, 1);

        Place(grid, _manual, 2, 0, 2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_write);
        buttons.Children.Add(_read);
        buttons.Children.Add(_cancel);
        Place(grid, buttons, 3, 0, 2);
        Place(grid, _progress, 4, 0, 2);
        Place(grid, _status, 5, 0, 2);
        Place(grid, _log, 6, 0, 2);
        return grid;
    }

    private static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }

    private async Task WriteWithDialogsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = FlasherText.T("Прошивка для записи в плату", "Firmware to write to the board"),
            Filter = FileFilter(),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        FirmwareImageInfo image;
        try
        {
            image = Dlc32FirmwareFlasher.InspectFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(FlasherText.T($"Не удалось прочитать файл: {ex.Message}", $"Cannot read the file: {ex.Message}"));
            return;
        }

        if (image.RefusalReason() is { } refusal)
        {
            ShowError(refusal);
            return;
        }

        if (image.Length > Dlc32FirmwareFlasher.Dlc32FlashSize)
        {
            ShowError(FlasherText.T(
                $"Файл ({FlasherText.Size(image.Length)}) больше флеш-памяти MKS DLC32 ({FlasherText.Size(Dlc32FirmwareFlasher.Dlc32FlashSize)}).",
                $"The file ({FlasherText.Size(image.Length)}) is larger than the flash of the MKS DLC32 ({FlasherText.Size(Dlc32FirmwareFlasher.Dlc32FlashSize)})."));
            return;
        }

        var name = Path.GetFileName(dialog.FileName);
        if (!Confirm(FlasherText.T(
                $"Записать «{name}» ({FlasherText.Size(image.Length)}) во флеш-память платы с адреса 0x0?\n\nПрошивка и настройки, которые хранятся во флеш-памяти, будут заменены. Сначала стоит считать текущую прошивку (кнопка «Считать прошивку»): по копии плату можно вернуть к прежнему состоянию. Во время записи не отключайте плату и кабель USB.",
                $"Write \"{name}\" ({FlasherText.Size(image.Length)}) to the flash of the board from address 0x0?\n\nThe firmware and the settings kept in the flash will be replaced. Read the current firmware first (the Read firmware button): the copy brings the board back to its present state. Do not disconnect the board or the USB cable during the writing.")))
        {
            return;
        }

        var report = await WriteFileAsync(dialog.FileName);
        ShowReport(report);
    }

    private async Task ReadWithDialogsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = FlasherText.T("Сохранить прошивку платы", "Save the firmware of the board"),
            Filter = FileFilter(),
            FileName = $"dlc32-flash-{DateTime.Now:yyyyMMdd-HHmm}.bin",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var report = await ReadFileAsync(dialog.FileName);
        ShowReport(report);
    }

    private async Task<FlasherReport> RunAsync(
        bool write,
        Func<Dlc32FirmwareFlasher, string, Dlc32FlasherOptions, IProgress<FlashProgress>, CancellationToken, Task<FlasherReport>> operation)
    {
        if (IsBusy)
        {
            return new FlasherReport(FlasherResult.Failed, FlasherText.T("Уже идёт другая операция.", "Another operation is running."));
        }

        var port = PortName;
        if (string.IsNullOrWhiteSpace(port))
        {
            var none = new FlasherReport(FlasherResult.Failed, FlasherText.T(
                "Не выбран COM-порт. Подключите плату и нажмите «Обновить».",
                "No COM port is chosen. Plug the board in and press Refresh."), Error: FlasherError.PortUnavailable);
            Finish(none);
            return none;
        }

        if (BeforePortUse is { } beforePortUse && !beforePortUse(port))
        {
            return new FlasherReport(FlasherResult.Cancelled, FlasherText.T("Отменено.", "Cancelled."));
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        _writing = write;
        _log.Clear();
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        UpdateButtons();
        BusyChanged?.Invoke(this, EventArgs.Empty);

        var dispatcher = Dispatcher;
        var flasher = new Dlc32FirmwareFlasher(line => dispatcher.BeginInvoke(() => AppendLog(line)), OpenPort);
        var progress = new Progress<FlashProgress>(p =>
        {
            _progress.IsIndeterminate = p.Total == 0 || p.Stage is FlashStage.Erasing or FlashStage.Verifying;
            if (!_progress.IsIndeterminate)
            {
                _progress.Value = p.Percent;
            }

            _status.Text = p.Text;
        });
        var options = new Dlc32FlasherOptions { Baud = Baud, ResetIntoBootloader = !BoardInBootloader };
        AppendLog($"{port}, {Baud}");

        FlasherReport report;
        try
        {
            report = await operation(flasher, port, options, progress, cts.Token);
        }
        finally
        {
            _cts = null;
            _writing = false;
        }

        // Progress reports may still be queued: let them run before the final state is shown.
        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Finish(report);
        BusyChanged?.Invoke(this, EventArgs.Empty);
        return report;
    }

    private void Finish(FlasherReport report)
    {
        _progress.IsIndeterminate = false;
        _progress.Value = report.Success ? 100 : _progress.Value;
        _status.Text = report.Message;
        AppendLog(report.Message);
        UpdateButtons();
        Completed?.Invoke(this, report);
    }

    private void ShowReport(FlasherReport report)
    {
        if (report.Success)
        {
            Show(report.Message, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (report.Result == FlasherResult.Failed || report.FlashChanged)
        {
            ShowError(report.Message);
        }
    }

    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
        LogLine?.Invoke(this, line);
    }

    private void UpdateButtons()
    {
        var idle = !IsBusy;
        _write.IsEnabled = idle;
        _read.IsEnabled = idle;
        _ports.IsEnabled = idle;
        _refresh.IsEnabled = idle;
        _bauds.IsEnabled = idle;
        _manual.IsEnabled = idle;
        _cancel.IsEnabled = !idle;
    }

    private bool Confirm(string text) => Show(text, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void ShowError(string text) => Show(text, MessageBoxButton.OK, MessageBoxImage.Error);

    private MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage image) => Window.GetWindow(this) is { } owner
        ? MessageBox.Show(owner, text, Caption(), buttons, image)
        : MessageBox.Show(text, Caption(), buttons, image);

    private static string Caption() => FlasherText.T("Прошивка MKS DLC32", "MKS DLC32 firmware");

    private static string FileFilter() => FlasherText.T(
        "Образ прошивки (*.bin)|*.bin|Все файлы (*.*)|*.*",
        "Firmware image (*.bin)|*.bin|All files (*.*)|*.*");
}
