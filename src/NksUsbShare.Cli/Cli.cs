using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Cli;

/// <summary>
/// What every command needs: the parsed arguments and the handful of shared answers — how to
/// build a node, how to turn what someone typed into a device or a computer, and how to ask a
/// question when there is a terminal to ask on.
/// </summary>
internal sealed class Cli(CommandLine options)
{
    public CommandLine Options { get; } = options;

    public (NodeService Node, ActivityLog Log) CreateNode(bool echo)
    {
        var settings = NodeSettings.LoadOrCreate(Options.Home);
        var log = new ActivityLog { EchoToConsole = echo };
        return (new NodeService(settings, log), log);
    }

    public NodeSettings LoadSettings() => NodeSettings.LoadOrCreate(Options.Home);

    public static ShutdownSignal Stopping() => new();

    /// <summary>Reports the problem and returns null when the host is not paired.</summary>
    public static RemoteHost? RequireRemote(NodeService node, string reference)
    {
        var (address, port) = SplitHost(reference);
        var remote = node.Settings.RemoteHosts.FirstOrDefault(h =>
            h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        if (remote is not null) return remote;

        Console.Error.WriteLine($"error: {address}:{port} is not paired. Run `nksusb pair {address}:{port} <token>` first.");
        return null;
    }

    /// <summary>Turns an index from `list`, a device id or a vid:pid into one device id.</summary>
    public static string? ResolveDeviceId(string reference, IReadOnlyList<UsbDeviceInfo> devices)
    {
        if (int.TryParse(reference, out var index))
        {
            if (index >= 1 && index <= devices.Count) return devices[index - 1].DeviceId;
            Console.Error.WriteLine($"error: there is no device {index}; `nksusb list` shows {devices.Count}.");
            return null;
        }

        var exact = devices.FirstOrDefault(d => d.DeviceId.Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.DeviceId;

        var byHardware = devices.Where(d => d.HardwareKey.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byHardware.Count == 1) return byHardware[0].DeviceId;
        if (byHardware.Count > 1)
        {
            Console.Error.WriteLine($"error: {reference} matches {byHardware.Count} devices; use the index from `nksusb list`.");
            return null;
        }

        Console.Error.WriteLine($"error: no device matches '{reference}'.");
        return null;
    }

    /// <summary>Resolves a computer by name or by any unambiguous part of its key fingerprint.</summary>
    public static KnownClient? FindClient(NodeSettings settings, string wanted)
    {
        var matches = settings.KnownClients
            .Where(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                        || NodeIdentity.FingerprintsMatch(c.Fingerprint, wanted)
                        || c.Fingerprint.Replace("-", "").Contains(wanted.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0];

        Console.Error.WriteLine(matches.Count == 0
            ? $"error: no computer matches '{wanted}'. Run `nksusb clients` to see the list."
            : $"error: '{wanted}' matches {matches.Count} computers. Use the full key fingerprint.");
        return null;
    }

    public static (string Address, int Port) SplitHost(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port)) return (value[..separator], port);
        return (value, DefaultPorts.Link);
    }

    public static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..(width - 1)] + "…";

    /// <summary>Asks a yes or no question, defaulting to no when there is no console to ask on.</summary>
    public static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) return false;

        Console.Write($"{question} [y/N] ");
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reports a missing argument in the same shape everywhere.</summary>
    public static int Missing(string command, string what)
    {
        Console.Error.WriteLine($"error: {command} needs {what}.");
        return 1;
    }
}
