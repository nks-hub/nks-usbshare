using System.Windows;
using System.Windows.Threading;

namespace NksUsbShare.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A background failure should say what happened rather than vanish with the process.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "NKS UsbShare",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
