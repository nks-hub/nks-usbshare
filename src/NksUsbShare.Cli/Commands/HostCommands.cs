using NksUsbShare.Core.Configuration;

namespace NksUsbShare.Cli.Commands;

/// <summary>The computers this one borrows devices from.</summary>
internal sealed class HostCommands(Cli cli)
{
    public int List()
    {
        var settings = cli.LoadSettings();
        if (settings.RemoteHosts.Count == 0)
        {
            Console.WriteLine("No hosts paired. Use `nksusb pair <host> <token>`.");
            return 0;
        }

        Console.WriteLine();
        foreach (var host in settings.RemoteHosts)
        {
            Console.WriteLine($"  {(host.Name.Length > 0 ? host.Name : host.Address)}");
            Console.WriteLine($"    address      {host.Address}:{host.Port}");
            Console.WriteLine($"    fingerprint  {host.PinnedFingerprint ?? "not pinned yet"}");
            Console.WriteLine($"    autoconnect  {(host.AutoConnect ? "yes" : "no")}");
        }
        Console.WriteLine();
        return 0;
    }

    public int Pair()
    {
        if (cli.Options.Arguments.Count < 2)
        {
            return Cli.Missing("pair",
                "a host and a token, for example `nksusb pair 192.0.2.10 ABCDE-FGHIJ-KLMNO-PQRST-UVWXY`");
        }

        var (address, port) = Cli.SplitHost(cli.Options.Arguments[0]);
        var settings = cli.LoadSettings();

        var existing = settings.RemoteHosts.FirstOrDefault(h =>
            h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        if (existing is null)
        {
            settings.RemoteHosts.Add(new RemoteHost
            {
                Address = address,
                Port = port,
                Token = cli.Options.Arguments[1],
                AutoConnect = true,
            });
        }
        else
        {
            existing.Token = cli.Options.Arguments[1];
            // A fresh token means the operator expects a fresh pairing, so drop the old pin.
            existing.PinnedFingerprint = null;
        }

        settings.Save();
        Console.WriteLine($"Paired with {address}:{port}.");
        return 0;
    }

    public int Forget()
    {
        if (cli.Options.Arguments.Count == 0) return Cli.Missing("forget", "a host address");

        var (address, port) = Cli.SplitHost(cli.Options.Arguments[0]);
        var settings = cli.LoadSettings();

        var removed = settings.RemoteHosts.RemoveAll(h =>
            h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        settings.Save();
        Console.WriteLine(removed > 0 ? $"Forgot {address}:{port}." : $"{address}:{port} was not paired.");
        return 0;
    }
}
