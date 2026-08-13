using NksUsbShare.Core.Security;

namespace NksUsbShare.Cli.Commands;

/// <summary>Which computers may connect, and which of them may use which device.</summary>
internal sealed class ComputerCommands(Cli cli)
{
    public int List()
    {
        var settings = cli.LoadSettings();
        if (settings.KnownClients.Count == 0)
        {
            Console.WriteLine("No computer has connected to this node yet.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"COMPUTER",-22} {"KEY FINGERPRINT",-42} {"LAST SEEN",-17} STATE");
        Console.WriteLine($"  {new string('-', 22)} {new string('-', 42)} {new string('-', 17)} -----");

        foreach (var client in settings.KnownClients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {Cli.Truncate(client.Name.Length > 0 ? client.Name : "unnamed", 22),-22} " +
                              $"{client.Fingerprint,-42} " +
                              $"{client.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),-17} " +
                              $"{(client.Blocked ? "blocked" : "allowed")}");
        }

        Console.WriteLine();
        return 0;
    }

    public int SetBlocked(bool blocked)
    {
        if (cli.Options.Arguments.Count == 0)
        {
            return Cli.Missing(blocked ? "block" : "unblock",
                "a computer name or key fingerprint. Run `nksusb clients` first");
        }

        var settings = cli.LoadSettings();
        var client = Cli.FindClient(settings, cli.Options.Arguments[0]);
        if (client is null) return 1;

        if (client.Blocked == blocked)
        {
            Console.WriteLine($"{client.Name} is already {(blocked ? "blocked" : "allowed")}.");
            return 0;
        }

        client.Blocked = blocked;
        settings.Save();

        Console.WriteLine(blocked
            ? $"{client.Name} is blocked. A running node refuses it on its next connection."
            : $"{client.Name} may connect again.");
        return 0;
    }

    public int Forget()
    {
        if (cli.Options.Arguments.Count == 0)
            return Cli.Missing("forget-client", "a computer name or key fingerprint. Run `nksusb clients` first");

        var settings = cli.LoadSettings();
        var client = Cli.FindClient(settings, cli.Options.Arguments[0]);
        if (client is null) return 1;

        settings.KnownClients.Remove(client);

        // Leaving the fingerprint behind on a share would silently keep the door open if the same
        // computer ever paired again.
        foreach (var share in settings.Shares)
            share.AllowedClients.RemoveAll(a => NodeIdentity.FingerprintsMatch(a, client.Fingerprint));

        settings.Save();
        Console.WriteLine($"Forgot {client.Name}. It has to pair again to connect.");
        return 0;
    }

    /// <summary>Shows or sets which computers a shared device is offered to.</summary>
    public async Task<int> AccessAsync()
    {
        if (cli.Options.Arguments.Count == 0)
            return Cli.Missing("access", "a device index or id. Run `nksusb list` first");

        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
        var deviceId = Cli.ResolveDeviceId(cli.Options.Arguments[0], devices.Select(d => d.Device).ToList());
        if (deviceId is null) return 1;

        var (device, share) = devices.First(d => d.Device.DeviceId == deviceId);
        if (share is null)
        {
            Console.Error.WriteLine(
                $"error: {device.DisplayName} is not shared, so there is nothing to allow. Run `nksusb share` first.");
            return 1;
        }

        var settings = node.Settings;
        var wanted = cli.Options.Arguments.Skip(1).ToList();

        if (wanted.Count > 0)
        {
            if (wanted.Count == 1 && wanted[0].Equals("anyone", StringComparison.OrdinalIgnoreCase))
            {
                share.AllowedClients = [];
            }
            else
            {
                var chosen = new List<string>();
                foreach (var name in wanted)
                {
                    var client = Cli.FindClient(settings, name);
                    if (client is null) return 1;
                    if (!chosen.Contains(client.Fingerprint)) chosen.Add(client.Fingerprint);
                }

                share.AllowedClients = chosen;
            }

            settings.Save();
        }

        Console.WriteLine();
        Console.WriteLine($"  {device.DisplayName}");

        if (share.AllowedClients.Count == 0)
        {
            Console.WriteLine("  offered to any computer that knows the pairing token");
        }
        else
        {
            Console.WriteLine("  offered only to:");
            foreach (var fingerprint in share.AllowedClients)
            {
                var known = settings.KnownClients
                    .FirstOrDefault(c => NodeIdentity.FingerprintsMatch(c.Fingerprint, fingerprint));
                Console.WriteLine($"    {fingerprint}  {known?.Name ?? "(never connected)"}");
            }
        }

        Console.WriteLine();
        return 0;
    }
}
