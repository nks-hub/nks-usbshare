using System.Windows;
using NksUsbShare.Core.Usb.Windows;
using System.Windows.Threading;

namespace NksUsbShare.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A driver change needs administrator rights, so the application runs itself again with
        // the consent prompt and these arguments. That run does the work and exits without ever
        // showing a window; it is the same executable, not a script or a separate tool.
        if (WinUsbHelper.IsHelperCommand(e.Args))
        {
            var log = Array.IndexOf(e.Args, "--log") is var i and >= 0 && i + 1 < e.Args.Length
                ? e.Args[i + 1]
                : null;

            var arguments = e.Args.Skip(1).TakeWhile(a => a != "--log").ToArray();
            Shutdown(WinUsbHelper.Run(e.Args[0], arguments, log));
            return;
        }

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
