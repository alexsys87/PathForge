using System.ComponentModel;
using System.Windows;

namespace Dlc32Flasher.Wpf;

/// <summary>
/// A ready window with <see cref="Dlc32FlasherControl"/>. It does not let itself be closed during a write (that would leave
/// the board without a firmware); closing it during a read cancels the read.
/// </summary>
public sealed class Dlc32FlasherWindow : Window
{
    public Dlc32FlasherWindow()
    {
        Title = FlasherText.T("Прошивка MKS DLC32", "MKS DLC32 firmware");
        Width = 600;
        Height = 520;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Flasher;
    }

    /// <summary>The control inside: its port, speed, events.</summary>
    public Dlc32FlasherControl Flasher { get; } = new();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Flasher.IsWriting)
        {
            MessageBox.Show(this, FlasherText.T(
                    "Идёт запись прошивки. Если прервать её, плата не запустится до повторной записи. Дождитесь окончания.",
                    "The firmware is being written. Stopping now leaves the board unable to start until it is written again. Wait for the end."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }

        Flasher.Cancel();
        base.OnClosing(e);
    }
}
