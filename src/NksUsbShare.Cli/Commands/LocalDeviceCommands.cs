using NksUsbShare.Core;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Cli.Commands;

/// <summary>The devices attached to this computer: listing them, sharing them, taking them back.</summary>
internal sealed class LocalDeviceCommands(Cli cli)
{
    public async Task<int> ListAsync()
    {
        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
        if (devices.Count == 0)
        {
            Console.WriteLine("No USB devices found.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"#",-3} {"VID:PID",-10} {"DEVICE",-34} {"SPEED",-11} STATE");
        Console.WriteLine($"  {new string('-', 3)} {new string('-', 10)} {new string('-', 34)} {new string('-', 11)} -----");

        for (var index = 0; index < devices.Count; index++)
        {
            var (device, share) = devices[index];
            var state = share is { Enabled: true } ? "shared"
                : share is not null ? "shared (off)"
                : device.ShareBlockedReason is not null ? "held by system"
                : "private";

            Console.WriteLine($"  {index + 1,-3} {device.HardwareKey,-10} {Cli.Truncate(device.DisplayName, 34),-34} " +
                              $"{UsbDisplay.Speed(device.Speed),-11} {state}");
        }

        var blocked = devices.Where(d => d.Device.ShareBlockedReason is not null).ToList();
        if (blocked.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Held by the operating system — `nksusb share` can claim these:");
            foreach (var (device, _) in blocked)
                Console.WriteLine($"    {device.DisplayName}: {device.ShareBlockedReason}");
        }

        Console.WriteLine();
        return 0;
    }

    public async Task<int> SetShareAsync(bool share)
    {
        if (cli.Options.Arguments.Count == 0)
            return Cli.Missing(share ? "share" : "unshare", "a device index or id. Run `nksusb list` first");

        // Claiming a device from Windows takes a driver package and a hub port cycle. The helper
        // reports each step, so the log is echoed rather than leaving the terminal silent.
        var (node, _) = cli.CreateNode(echo: share);
        await using var _ = node;

        var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
        var deviceId = Cli.ResolveDeviceId(cli.Options.Arguments[0], devices.Select(d => d.Device).ToList());
        if (deviceId is null) return 1;

        var device = devices.First(d => d.Device.DeviceId == deviceId).Device;

        if (!share)
        {
            node.UnshareDevice(deviceId);
            Console.WriteLine($"Stopped sharing {device.DisplayName}.");
            return 0;
        }

        // A device the operating system is holding has to be claimed before it can be forwarded.
        if (device.ShareBlockedReason is not null)
        {
            var (claimed, exitCode) = await ClaimAsync(node, device, deviceId);
            if (claimed is null) return exitCode;
            deviceId = claimed;
        }

        await node.ShareDeviceAsync(deviceId, CancellationToken.None);
        Console.WriteLine($"Sharing {device.DisplayName}.");
        return 0;
    }

    /// <summary>
    /// Takes a device from the driver holding it. The device id afterwards is not the one it had
    /// before, because Windows re-enumerates it, so the new one is what comes back. A null id with
    /// exit code 0 means the operator said no, which is not a failure.
    /// </summary>
    private async Task<(string? DeviceId, int ExitCode)> ClaimAsync(NodeService node, UsbDeviceInfo device, string deviceId)
    {
        if (!NodeService.CanClaimHeldDevices)
        {
            Console.Error.WriteLine($"error: {device.ShareBlockedReason}");
            return (null, 1);
        }

        Console.WriteLine($"{device.DisplayName} is held by the operating system.");
        Console.WriteLine("Claiming it stops it working on this computer until it is given back.");
        Console.WriteLine("You will be asked to approve the change as an administrator.");
        Console.WriteLine();

        if (!cli.Options.Flags.Contains("yes") && !Cli.Confirm("Hand this device over?"))
        {
            Console.WriteLine("Left alone.");
            return (null, 0);
        }

        Console.WriteLine(await node.ClaimForSharingAsync(deviceId, CancellationToken.None));

        var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
        var again = devices.FirstOrDefault(d =>
            d.Device.HardwareKey == device.HardwareKey && d.Device.SerialNumber == device.SerialNumber);

        if (again.Device is null || again.Device.ShareBlockedReason is not null)
        {
            Console.Error.WriteLine("error: the device is not ready yet. Unplug and replug it, then try again.");
            return (null, 1);
        }

        return (again.Device.DeviceId, 0);
    }

    public async Task<int> GiveBackAsync()
    {
        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        Console.WriteLine(await node.ReleaseClaimedDevicesAsync(CancellationToken.None));
        return 0;
    }

    public int Token()
    {
        var settings = cli.LoadSettings();

        if (cli.Options.Flags.Contains("new"))
        {
            settings.PairingToken = AccessToken.Generate();
            settings.Save();
            Console.WriteLine("A new pairing token was generated. Existing clients must pair again.");
        }

        Console.WriteLine(settings.PairingToken);
        return 0;
    }

    public async Task<int> InfoAsync()
    {
        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        Console.WriteLine();
        Console.WriteLine($"  Node            {node.Settings.HostName}");
        Console.WriteLine($"  Platform        {UsbBackend.DescribeCurrentPlatform()}");
        Console.WriteLine($"  Backend         {node.Backend.Name}");
        Console.WriteLine($"  Fingerprint     {node.Identity.Fingerprint}");
        Console.WriteLine($"  Pairing token   {node.Settings.PairingToken}");
        Console.WriteLine($"  Link            {node.Settings.LinkBindAddress}:{node.Settings.LinkPort}");
        Console.WriteLine($"  Settings        {node.Settings.SourcePath}");

        var addresses = UsbDisplay.LocalAddresses();
        if (addresses.Count > 0) Console.WriteLine($"  Reachable at    {string.Join(", ", addresses)}");

        Console.WriteLine();
        return 0;
    }
}
