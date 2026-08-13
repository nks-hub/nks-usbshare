using NksUsbShare.Core;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Cli.Commands;

/// <summary>What the tool can do, said once, in the order someone would need it.</summary>
internal static class HelpCommand
{
    public static int Show()
    {
        Console.WriteLine("""
            nksusb — share USB devices over an encrypted network link

            USAGE
              nksusb [command] [arguments] [--home <dir>]

            RUNNING
              serve                      run the node in this terminal until interrupted

            THIS MACHINE
              list                       show local USB devices and what is shared
              share <index|id> [--yes]   offer a device to paired clients, claiming it from the
                                         operating system first when it has to be
              unshare <index|id>         stop offering a device
              giveback                   return every claimed device to its original driver
              token [--new]              show the pairing token, or generate a fresh one
              info                       identity, ports and fingerprint

            COMPUTERS ALLOWED IN
              clients                    list every computer that has connected here
              block <name|fingerprint>   refuse a computer even if it knows the token
              unblock <name|fingerprint> let a blocked computer back in
              forget-client <name|fp>    drop a computer from the list, and from every device's
                                         access list with it
              access <index|id> [who...] show or set which computers may use a shared device;
                                         `access 3 anyone` offers it to every paired computer

            OTHER MACHINES
              hosts                      list hosts this node knows
              pair <host[:port]> <token> remember a host and its pairing token
              forget <host[:port]>       drop a host
              remote <host[:port]>       list the devices a host offers
              import <host[:port]> <id>  import a device and hold it until interrupted
              probe <host[:port]> <id>   read descriptors straight off a remote device
              attach <host[:port]> <id>  hand a remote device to this machine's USB stack, so every
                                         application sees it as though it were plugged in here
                                         [--port <n>] if another tool already owns 3240
              storage <host[:port]> <id> read a remote storage device directly, without handing it to
                                         the operating system — the only way on macOS

            Devices can be named by the index shown in `list`, so `nksusb share 3` works.
            The desktop application hosts the same node with a window; run one or the other,
            not both, because they would compete for the same port.
            """);
        return 0;
    }

    public static int Version()
    {
        var version = typeof(NodeService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"nksusb {version} — {UsbBackend.DescribeCurrentPlatform()}");
        return 0;
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'. Try `nksusb help`.");
        return 1;
    }
}
