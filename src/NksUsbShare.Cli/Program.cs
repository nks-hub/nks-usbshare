using NksUsbShare.Cli;
using NksUsbShare.Cli.Commands;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

// The whole program is this: parse the arguments, hand the work to one command, and turn the
// failures worth showing into one line instead of a stack trace.

var cli = new Cli(CommandLine.Parse(args));

var local = new LocalDeviceCommands(cli);
var computers = new ComputerCommands(cli);
var hosts = new HostCommands(cli);
var remote = new RemoteDeviceCommands(cli);

try
{
    return cli.Options.Command switch
    {
        "help" or "--help" or "-h" => HelpCommand.Show(),
        "version" or "--version" => HelpCommand.Version(),

        "serve" or "run" => await new ServeCommand(cli).RunAsync(),

        "list" => await local.ListAsync(),
        "share" => await local.SetShareAsync(share: true),
        "unshare" => await local.SetShareAsync(share: false),
        "giveback" => await local.GiveBackAsync(),
        "token" => local.Token(),
        "info" => await local.InfoAsync(),

        "clients" => computers.List(),
        "block" => computers.SetBlocked(blocked: true),
        "unblock" => computers.SetBlocked(blocked: false),
        "forget-client" => computers.Forget(),
        "access" => await computers.AccessAsync(),

        "hosts" => hosts.List(),
        "pair" => hosts.Pair(),
        "forget" => hosts.Forget(),

        "remote" => await remote.ListAsync(),
        "import" => await remote.ImportAsync(),
        "probe" => await remote.ProbeAsync(),
        "attach" => await remote.AttachAsync(),
        "storage" => await remote.StorageAsync(),

        // Not in the help: this is how the application re-runs itself with administrator rights
        // to change a driver. Nobody types it.
        "__winusb-bind" or "__winusb-restore" when OperatingSystem.IsWindows() =>
            NksUsbShare.Core.Usb.Windows.WinUsbHelper.Run(cli.Options.Command, cli.Options.Arguments,
                cli.Options.Values.GetValueOrDefault("log")),

        _ => HelpCommand.Unknown(cli.Options.Command),
    };
}
catch (Exception ex) when (ex is UsbBackendException or HostRejectedException
                               or PinnedCertificateMismatchException or ProtocolViolationExceptionEx
                               or IOException or InvalidOperationException or ArgumentException
                               or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
