using System.Diagnostics;
using System.Net.Http.Json;
using NksUsbShare.App.Web;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

var options = CommandLine.Parse(args);

try
{
    return options.Command switch
    {
        "help" or "--help" or "-h" => Help(),
        "version" or "--version" => Version(),
        "run" => await RunAsync(withInterface: true),
        "serve" => await RunAsync(withInterface: false),
        "list" => await ListAsync(),
        "share" => await SetShareAsync(share: true),
        "unshare" => await SetShareAsync(share: false),
        "token" => await TokenAsync(),
        "info" => await InfoAsync(),
        "hosts" => Hosts(),
        "pair" => Pair(),
        "forget" => Forget(),
        "remote" => await RemoteAsync(),
        "import" => await ImportAsync(),
        _ => Unknown(options.Command),
    };
}
catch (UsbBackendException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ---- commands ----

int Help()
{
    Console.WriteLine("""
        nksusb — share USB devices over an encrypted network link

        USAGE
          nksusb [command] [arguments] [--home <dir>]

        RUNNING
          run                        start the node and open the control panel (default)
          serve                      start the node without the web interface

        THIS MACHINE
          list                       show local USB devices and what is shared
          share <index|id>           offer a device to paired clients
          unshare <index|id>         stop offering a device
          token [--new]              show the pairing token, or generate a fresh one
          info                       identity, ports and fingerprint

        OTHER MACHINES
          hosts                      list hosts this node knows
          pair <host[:port]> <token> remember a host and its pairing token
          forget <host[:port]>       drop a host
          remote <host[:port]>       list the devices a host offers
          import <host[:port]> <id>  import a device and hold it until interrupted

        Devices can be named by the index shown in `list`, so `nksusb share 3` works.
        """);
    return 0;
}

int Version()
{
    var version = typeof(NodeService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    Console.WriteLine($"nksusb {version} — {UsbBackend.DescribeCurrentPlatform()}");
    return 0;
}

int Unknown(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'. Try `nksusb help`.");
    return 1;
}

async Task<int> RunAsync(bool withInterface)
{
    var (node, log) = CreateNode(echo: true);
    await using var _ = node;

    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stopping.Cancel();
    };

    await node.StartHostAsync(stopping.Token);
    await node.AutoConnectAsync(stopping.Token);

    if (!withInterface)
    {
        log.Info("app", "Running headless. Press Ctrl+C to stop.");
        await WaitForShutdownAsync(stopping.Token);
        return 0;
    }

    var app = WebInterface.Build(node, []);
    await app.StartAsync(stopping.Token);

    var url = $"http://{(node.Settings.WebUiBindAddress == "0.0.0.0" ? "127.0.0.1" : node.Settings.WebUiBindAddress)}:{node.Settings.WebUiPort}";
    log.Info("app", $"Control panel at {url}");
    OpenBrowser(url);

    await WaitForShutdownAsync(stopping.Token);
    await app.StopAsync(CancellationToken.None);
    return 0;
}

async Task<int> ListAsync()
{
    var (node, _) = CreateNode(echo: false);
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
            : device.ShareBlockedReason is not null ? "unavailable"
            : "private";

        Console.WriteLine($"  {index + 1,-3} {device.HardwareKey,-10} {Truncate(device.DisplayName, 34),-34} " +
                          $"{ViewMapper.Describe(device.Speed),-11} {state}");
    }

    var blocked = devices.Where(d => d.Device.ShareBlockedReason is not null).ToList();
    if (blocked.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Unavailable devices:");
        foreach (var (device, _) in blocked)
            Console.WriteLine($"    {device.DisplayName}: {device.ShareBlockedReason}");
    }

    Console.WriteLine();
    return 0;
}

async Task<int> SetShareAsync(bool share)
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine($"error: {(share ? "share" : "unshare")} needs a device index or id. Run `nksusb list` first.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
    var deviceId = ResolveDeviceId(options.Arguments[0], devices.Select(d => d.Device).ToList());
    if (deviceId is null) return 1;

    var device = devices.First(d => d.Device.DeviceId == deviceId).Device;

    if (share)
    {
        await node.ShareDeviceAsync(deviceId, CancellationToken.None);
        Console.WriteLine($"Sharing {device.DisplayName}.");
    }
    else
    {
        node.UnshareDevice(deviceId);
        Console.WriteLine($"Stopped sharing {device.DisplayName}.");
    }

    await NotifyRunningDaemonAsync(node.Settings, share ? "/api/share" : "/api/unshare", new { deviceId });
    return 0;
}

async Task<int> TokenAsync()
{
    var settings = NodeSettings.LoadOrCreate(options.Home);

    if (options.Flags.Contains("new"))
    {
        settings.PairingToken = AccessToken.Generate();
        settings.Save();
        Console.WriteLine("A new pairing token was generated. Existing clients must pair again.");
        await NotifyRunningDaemonAsync(settings, "/api/token/regenerate", new { });
    }

    Console.WriteLine(settings.PairingToken);
    return 0;
}

async Task<int> InfoAsync()
{
    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    Console.WriteLine();
    Console.WriteLine($"  Node            {node.Settings.HostName}");
    Console.WriteLine($"  Platform        {UsbBackend.DescribeCurrentPlatform()}");
    Console.WriteLine($"  Backend         {node.Backend.Name}");
    Console.WriteLine($"  Fingerprint     {node.Identity.Fingerprint}");
    Console.WriteLine($"  Pairing token   {node.Settings.PairingToken}");
    Console.WriteLine($"  Link            {node.Settings.LinkBindAddress}:{node.Settings.LinkPort}");
    Console.WriteLine($"  Control panel   {node.Settings.WebUiBindAddress}:{node.Settings.WebUiPort}");
    Console.WriteLine($"  Settings        {node.Settings.SourcePath}");

    var addresses = WebInterface.LocalAddresses();
    if (addresses.Count > 0)
        Console.WriteLine($"  Reachable at    {string.Join(", ", addresses)}");

    Console.WriteLine();
    return 0;
}

int Hosts()
{
    var settings = NodeSettings.LoadOrCreate(options.Home);
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

int Pair()
{
    if (options.Arguments.Count < 2)
    {
        Console.Error.WriteLine("error: pair needs a host and a token, for example `nksusb pair 192.0.2.10 ABCDE-FGHIJ-KLMNO-PQRST-UVWXY`.");
        return 1;
    }

    var (address, port) = SplitHost(options.Arguments[0]);
    var settings = NodeSettings.LoadOrCreate(options.Home);

    var existing = settings.RemoteHosts.FirstOrDefault(h =>
        h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

    if (existing is null)
    {
        settings.RemoteHosts.Add(new RemoteHost
        {
            Address = address,
            Port = port,
            Token = options.Arguments[1],
            AutoConnect = true,
        });
    }
    else
    {
        existing.Token = options.Arguments[1];
        // A new token means the operator expects a fresh pairing, so drop the old pin.
        existing.PinnedFingerprint = null;
    }

    settings.Save();
    Console.WriteLine($"Paired with {address}:{port}.");
    return 0;
}

int Forget()
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine("error: forget needs a host address.");
        return 1;
    }

    var (address, port) = SplitHost(options.Arguments[0]);
    var settings = NodeSettings.LoadOrCreate(options.Home);

    var removed = settings.RemoteHosts.RemoveAll(h =>
        h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

    settings.Save();
    Console.WriteLine(removed > 0 ? $"Forgot {address}:{port}." : $"{address}:{port} was not paired.");
    return 0;
}

async Task<int> RemoteAsync()
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine("error: remote needs a host address.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var (address, port) = SplitHost(options.Arguments[0]);
    var remote = node.Settings.RemoteHosts.FirstOrDefault(h =>
        h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

    if (remote is null)
    {
        Console.Error.WriteLine($"error: {address}:{port} is not paired. Run `nksusb pair {address}:{port} <token>` first.");
        return 1;
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var devices = await node.ListRemoteDevicesAsync(remote, timeout.Token);

    if (devices.Count == 0)
    {
        Console.WriteLine($"{remote.Address}:{remote.Port} is not offering any device to this client.");
        return 0;
    }

    Console.WriteLine();
    foreach (var device in devices)
    {
        Console.WriteLine($"  {device.DisplayName}");
        Console.WriteLine($"    id     {device.DeviceId}");
        Console.WriteLine($"    usb    {device.HardwareKey}  {ViewMapper.Describe(device.Speed)}  {ViewMapper.DescribeKind(device)}");
        if (device.SerialNumber.Length > 0) Console.WriteLine($"    serial {device.SerialNumber}");
    }
    Console.WriteLine();
    return 0;
}

async Task<int> ImportAsync()
{
    if (options.Arguments.Count < 2)
    {
        Console.Error.WriteLine("error: import needs a host and a device id. Run `nksusb remote <host>` to see them.");
        return 1;
    }

    var (node, log) = CreateNode(echo: true);
    await using var _ = node;

    var (address, port) = SplitHost(options.Arguments[0]);
    var remote = node.Settings.RemoteHosts.FirstOrDefault(h =>
        h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

    if (remote is null)
    {
        Console.Error.WriteLine($"error: {address}:{port} is not paired.");
        return 1;
    }

    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stopping.Cancel();
    };

    var import = await node.ImportDeviceAsync(remote, options.Arguments[1], stopping.Token);
    log.Info("app", $"Holding {import.Device.Info.DisplayName}. Press Ctrl+C to release it.");

    await WaitForShutdownAsync(stopping.Token);
    node.ReleaseImport(import.Key);
    return 0;
}

// ---- helpers ----

(NodeService Node, ActivityLog Log) CreateNode(bool echo)
{
    var settings = NodeSettings.LoadOrCreate(options.Home);
    var log = new ActivityLog { EchoToConsole = echo };
    return (new NodeService(settings, log), log);
}

static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
    }
}

static string? ResolveDeviceId(string reference, IReadOnlyList<UsbDeviceInfo> devices)
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

static (string Address, int Port) SplitHost(string value)
{
    var separator = value.LastIndexOf(':');
    if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port))
        return (value[..separator], port);
    return (value, DefaultPorts.Link);
}

static string Truncate(string value, int width)
    => value.Length <= width ? value : value[..(width - 1)] + "…";

/// <summary>
/// A running daemon holds settings in memory, so a change made from the command line has to be
/// told to it. Failure is expected and silent: it simply means nothing is running.
/// </summary>
static async Task NotifyRunningDaemonAsync(NodeSettings settings, string path, object body)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await http.PostAsJsonAsync($"http://127.0.0.1:{settings.WebUiPort}{path}", body);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // No daemon listening; the settings file is the source of truth for the next start.
    }
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
    {
        // Headless machines have no browser to open; the URL was already printed.
    }
}

/// <summary>Minimal argument parsing: a verb, positional arguments, and --flags.</summary>
internal sealed record CommandLine(string Command, IReadOnlyList<string> Arguments, IReadOnlySet<string> Flags, string? Home)
{
    public static CommandLine Parse(string[] args)
    {
        var positional = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? home = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith('-'))
            {
                positional.Add(argument);
                continue;
            }

            var name = argument.TrimStart('-');
            if (name.Equals("home", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                home = args[++i];
                continue;
            }

            flags.Add(name);
        }

        var command = positional.Count > 0 ? positional[0] : "run";
        if (flags.Contains("help") || flags.Contains("h")) command = "help";
        if (flags.Contains("version")) command = "version";

        return new CommandLine(command.ToLowerInvariant(), positional.Skip(1).ToArray(), flags, home);
    }
}
