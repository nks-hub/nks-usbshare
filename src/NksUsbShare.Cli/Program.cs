using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

var options = CommandLine.Parse(args);

try
{
    return options.Command switch
    {
        "help" or "--help" or "-h" => Help(),
        "version" or "--version" => Version(),
        "serve" or "run" => await ServeAsync(),
        "list" => await ListAsync(),
        "share" => await SetShareAsync(share: true),
        "unshare" => await SetShareAsync(share: false),
        "token" => Token(),
        "info" => await InfoAsync(),
        "hosts" => Hosts(),
        "pair" => Pair(),
        "forget" => Forget(),
        "clients" => Clients(),
        "block" => SetBlocked(blocked: true),
        "unblock" => SetBlocked(blocked: false),
        "forget-client" => ForgetClient(),
        "access" => await AccessAsync(),
        "remote" => await RemoteAsync(),
        "import" => await ImportAsync(),
        "probe" => await ProbeAsync(),
        "attach" => await AttachAsync(),
        "storage" => await StorageAsync(),
        "giveback" => await GiveBackAsync(),
        _ => Unknown(options.Command),
    };
}
catch (Exception ex) when (ex is UsbBackendException or HostRejectedException
                               or PinnedCertificateMismatchException or ProtocolViolationExceptionEx)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException)
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
          storage <host[:port]> <id> read a remote storage device directly, without handing it to
                                     the operating system — the only way on macOS

        Devices can be named by the index shown in `list`, so `nksusb share 3` works.
        The desktop application hosts the same node with a window; run one or the other,
        not both, because they would compete for the same port.
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

async Task<int> ServeAsync()
{
    var (node, log) = CreateNode(echo: true);
    await using var _ = node;

    using var stopping = CreateShutdownSignal();

    try
    {
        await node.StartHostAsync(stopping.Token);
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        Console.Error.WriteLine($"error: cannot listen on {node.Settings.LinkBindAddress}:{node.Settings.LinkPort} — {ex.Message}");
        Console.Error.WriteLine("Another instance, or the desktop application, may already be running.");
        return 1;
    }

    await node.AutoConnectAsync(stopping.Token);
    log.Info("app", "Running. Press Ctrl+C to stop.");

    await stopping.WaitAsync();
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
            : device.ShareBlockedReason is not null ? "held by system"
            : "private";

        Console.WriteLine($"  {index + 1,-3} {device.HardwareKey,-10} {Truncate(device.DisplayName, 34),-34} " +
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

async Task<int> SetShareAsync(bool share)
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine($"error: {(share ? "share" : "unshare")} needs a device index or id. Run `nksusb list` first.");
        return 1;
    }

    // Claiming a device from Windows takes about half a minute of certificate, catalog and
    // driver work. The helper reports each step, so the log is echoed rather than leaving the
    // terminal silent for the duration.
    var (node, _) = CreateNode(echo: share);
    await using var _ = node;

    var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
    var deviceId = ResolveDeviceId(options.Arguments[0], devices.Select(d => d.Device).ToList());
    if (deviceId is null) return 1;

    var device = devices.First(d => d.Device.DeviceId == deviceId).Device;

    if (share)
    {
        // A device the operating system is holding has to be claimed before it can be forwarded.
        if (device.ShareBlockedReason is not null)
        {
            if (!NodeService.CanClaimHeldDevices)
            {
                Console.Error.WriteLine($"error: {device.ShareBlockedReason}");
                return 1;
            }

            Console.WriteLine($"{device.DisplayName} is held by the operating system.");
            Console.WriteLine("Claiming it stops it working on this computer until it is given back.");
            Console.WriteLine("You will be asked to approve the change as an administrator.");
            Console.WriteLine();

            if (!options.Flags.Contains("yes") && !Confirm("Hand this device over?"))
            {
                Console.WriteLine("Left alone.");
                return 0;
            }

            Console.WriteLine(await node.ClaimForSharingAsync(deviceId, CancellationToken.None));

            // The device is re-enumerated, so find it again by hardware identity.
            devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
            var again = devices.FirstOrDefault(d =>
                d.Device.HardwareKey == device.HardwareKey && d.Device.SerialNumber == device.SerialNumber);

            if (again.Device is null || again.Device.ShareBlockedReason is not null)
            {
                Console.Error.WriteLine("error: the device is not ready yet. Unplug and replug it, then try again.");
                return 1;
            }

            deviceId = again.Device.DeviceId;
        }

        await node.ShareDeviceAsync(deviceId, CancellationToken.None);
        Console.WriteLine($"Sharing {device.DisplayName}.");
    }
    else
    {
        node.UnshareDevice(deviceId);
        Console.WriteLine($"Stopped sharing {device.DisplayName}.");
    }

    return 0;
}

/// <summary>
/// Reads a remote storage device over SCSI without involving the local operating system. On a
/// platform that cannot be given a virtual USB device at all, this is what "using" the device means.
/// </summary>
async Task<int> StorageAsync()
{
    if (options.Arguments.Count < 2)
    {
        Console.Error.WriteLine("error: storage needs a host and a device id.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var remote = RequireRemote(node, options.Arguments[0]);
    if (remote is null) return 1;

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var import = await node.ImportDeviceAsync(remote, options.Arguments[1], timeout.Token);

    try
    {
        var storage = NksUsbShare.Core.Storage.MassStorageClient.Attach(import.Device);
        await storage.ClaimAsync(timeout.Token);

        Console.WriteLine();
        Console.WriteLine($"  {import.Device.Info.DisplayName}  ({import.Device.Info.HardwareKey})");
        Console.WriteLine($"  host {import.HostName} at {import.HostAddress}");
        Console.WriteLine();

        var identity = await storage.InquiryAsync(timeout.Token);
        Console.WriteLine($"  SCSI inquiry     {identity.Vendor} {identity.Product} rev {identity.Revision}");

        var capacity = await storage.ReadCapacityAsync(timeout.Token);
        Console.WriteLine($"  capacity         {capacity.BlockCount:N0} blocks of {capacity.BlockSize} bytes " +
                          $"({capacity.TotalBytes / 1024.0 / 1024 / 1024:F2} GB)");

        var sector = await storage.ReadBlocksAsync(0, 1, capacity.BlockSize, timeout.Token);
        Console.WriteLine($"  first sector     {sector.Length} bytes, " +
                          $"boot signature {(sector.Length >= 512 && sector[510] == 0x55 && sector[511] == 0xAA ? "present" : "absent")}");
        Console.WriteLine($"    {Convert.ToHexString(sector.AsSpan(0, Math.Min(32, sector.Length)))}…");

        var partitions = NksUsbShare.Core.Storage.MassStorageClient.ReadPartitions(sector);
        foreach (var (type, firstBlock, blockCount) in partitions)
            Console.WriteLine($"  partition        type 0x{type:x2}, {blockCount:N0} blocks from {firstBlock:N0}");

        // On a partitioned medium the volume starts at the partition, not at block zero.
        var volumeStart = partitions.Count > 0 ? partitions[0].FirstBlock : 0;
        var bootSector = volumeStart == 0 ? sector : await storage.ReadBlocksAsync(volumeStart, 1, capacity.BlockSize, timeout.Token);

        var layout = NksUsbShare.Core.Storage.FatDirectory.ReadLayout(bootSector, volumeStart);
        if (layout is null)
        {
            Console.WriteLine();
            Console.WriteLine("  Read straight off the remote medium over the encrypted link.");
            Console.WriteLine("  The volume is not FAT, so its contents are not listed here.");
            Console.WriteLine();
            return 0;
        }

        // One cluster of the root directory is plenty for a listing.
        var clusterSectors = (ushort)Math.Min(layout.SectorsPerCluster, 64);
        var root = await storage.ReadBlocksAsync(layout.RootDirectorySector, clusterSectors, capacity.BlockSize, timeout.Token);

        var label = NksUsbShare.Core.Storage.FatDirectory.FindVolumeLabel(root);
        if (label is not null) Console.WriteLine($"  volume label     {label}");

        var entries = NksUsbShare.Core.Storage.FatDirectory.Parse(root);
        Console.WriteLine();
        Console.WriteLine($"  root directory   {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");

        foreach (var entry in entries)
            Console.WriteLine($"    {(entry.IsDirectory ? "<dir> " : "      ")}{entry.Name,-34} " +
                              $"{(entry.IsDirectory ? "" : $"{entry.Size,12:N0}")}  " +
                              $"{entry.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""}");

        Console.WriteLine();
        Console.WriteLine("  Read straight off the remote medium over the encrypted link.");
        Console.WriteLine();
        return 0;
    }
    finally
    {
        await node.ReleaseImportAsync(import.Key);
    }
}

async Task<int> GiveBackAsync()
{
    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    Console.WriteLine(await node.ReleaseClaimedDevicesAsync(CancellationToken.None));
    return 0;
}

/// <summary>Asks a yes or no question, defaulting to no when there is no console to ask on.</summary>
static bool Confirm(string question)
{
    if (Console.IsInputRedirected) return false;

    Console.Write($"{question} [y/N] ");
    var answer = Console.ReadLine();
    return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
}

int Token()
{
    var settings = NodeSettings.LoadOrCreate(options.Home);

    if (options.Flags.Contains("new"))
    {
        settings.PairingToken = AccessToken.Generate();
        settings.Save();
        Console.WriteLine("A new pairing token was generated. Existing clients must pair again.");
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
    Console.WriteLine($"  Settings        {node.Settings.SourcePath}");

    var addresses = UsbDisplay.LocalAddresses();
    if (addresses.Count > 0) Console.WriteLine($"  Reachable at    {string.Join(", ", addresses)}");

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
        // A fresh token means the operator expects a fresh pairing, so drop the old pin.
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

int Clients()
{
    var settings = NodeSettings.LoadOrCreate(options.Home);
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
        Console.WriteLine($"  {Truncate(client.Name.Length > 0 ? client.Name : "unnamed", 22),-22} " +
                          $"{client.Fingerprint,-42} " +
                          $"{client.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),-17} " +
                          $"{(client.Blocked ? "blocked" : "allowed")}");
    }

    Console.WriteLine();
    return 0;
}

int SetBlocked(bool blocked)
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine($"error: {(blocked ? "block" : "unblock")} needs a computer name or key fingerprint. Run `nksusb clients` first.");
        return 1;
    }

    var settings = NodeSettings.LoadOrCreate(options.Home);
    var client = FindClient(settings, options.Arguments[0]);
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

int ForgetClient()
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine("error: forget-client needs a computer name or key fingerprint. Run `nksusb clients` first.");
        return 1;
    }

    var settings = NodeSettings.LoadOrCreate(options.Home);
    var client = FindClient(settings, options.Arguments[0]);
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

async Task<int> AccessAsync()
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine("error: access needs a device index or id. Run `nksusb list` first.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var devices = await node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
    var deviceId = ResolveDeviceId(options.Arguments[0], devices.Select(d => d.Device).ToList());
    if (deviceId is null) return 1;

    var (device, share) = devices.First(d => d.Device.DeviceId == deviceId);
    if (share is null)
    {
        Console.Error.WriteLine($"error: {device.DisplayName} is not shared, so there is nothing to allow. Run `nksusb share` first.");
        return 1;
    }

    var settings = node.Settings;
    var wanted = options.Arguments.Skip(1).ToList();

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
                var client = FindClient(settings, name);
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

/// <summary>Resolves a computer by name or by any unambiguous part of its key fingerprint.</summary>
KnownClient? FindClient(NodeSettings settings, string wanted)
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

async Task<int> RemoteAsync()
{
    if (options.Arguments.Count == 0)
    {
        Console.Error.WriteLine("error: remote needs a host address.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var remote = RequireRemote(node, options.Arguments[0]);
    if (remote is null) return 1;

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
        Console.WriteLine($"    usb    {device.HardwareKey}  {UsbDisplay.Speed(device.Speed)}  {UsbDisplay.Kind(device)}");
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

    var remote = RequireRemote(node, options.Arguments[0]);
    if (remote is null) return 1;

    using var stopping = CreateShutdownSignal();

    var import = await node.ImportDeviceAsync(remote, options.Arguments[1], stopping.Token);
    log.Info("app", $"Holding {import.Device.Info.DisplayName}. Press Ctrl+C to release it.");

    await stopping.WaitAsync();
    node.ReleaseImport(import.Key);
    return 0;
}

/// <summary>
/// Opens a remote device and reads its descriptors straight off the hardware. This is the
/// end-to-end proof that real USB traffic is crossing the link, not just metadata.
/// </summary>
async Task<int> ProbeAsync()
{
    if (options.Arguments.Count < 2)
    {
        Console.Error.WriteLine("error: probe needs a host and a device id.");
        return 1;
    }

    var (node, _) = CreateNode(echo: false);
    await using var _ = node;

    var remote = RequireRemote(node, options.Arguments[0]);
    if (remote is null) return 1;

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var import = await node.ImportDeviceAsync(remote, options.Arguments[1], timeout.Token);
    var device = import.Device;

    try
    {
        Console.WriteLine();
        Console.WriteLine($"  {device.Info.DisplayName}  ({device.Info.HardwareKey})");
        Console.WriteLine($"  host {import.HostName} at {import.HostAddress}");
        Console.WriteLine();

        // GET_DESCRIPTOR(DEVICE): the most basic control transfer there is.
        var descriptor = await device.GetDescriptorAsync(0x01, 0, length: 18, cancellationToken: timeout.Token);
        Console.WriteLine($"  device descriptor ({descriptor.Length} bytes)");
        Console.WriteLine($"    {Convert.ToHexString(descriptor)}");

        if (descriptor.Length >= 18)
        {
            var usb = (ushort)(descriptor[2] | (descriptor[3] << 8));
            var vendor = (ushort)(descriptor[8] | (descriptor[9] << 8));
            var product = (ushort)(descriptor[10] | (descriptor[11] << 8));
            Console.WriteLine($"    usb {usb >> 8:x}.{(usb >> 4) & 0xF:x}{usb & 0xF:x}   vendor {vendor:x4}   product {product:x4}   " +
                              $"class {descriptor[4]:x2}   max packet {descriptor[7]}");
        }

        // GET_DESCRIPTOR(CONFIGURATION): read the header, then the whole thing.
        var header = await device.GetDescriptorAsync(0x02, 0, length: 9, cancellationToken: timeout.Token);
        var total = UsbDescriptorParser.ReadConfigurationTotalLength(header);
        Console.WriteLine();
        Console.WriteLine($"  configuration descriptor, {total} bytes total");

        if (total is > 9 and <= 4096)
        {
            var configuration = await device.GetDescriptorAsync(0x02, 0, length: (ushort)total, cancellationToken: timeout.Token);
            Console.WriteLine($"    {Convert.ToHexString(configuration)}");

            foreach (var iface in UsbDescriptorParser.ParseConfiguration(configuration))
            {
                Console.WriteLine($"    interface {iface.Number} alt {iface.AlternateSetting}  " +
                                  $"class {iface.InterfaceClass:x2}/{iface.InterfaceSubClass:x2}/{iface.InterfaceProtocol:x2}");
                foreach (var endpoint in iface.Endpoints)
                    Console.WriteLine($"      endpoint 0x{endpoint.Address:x2}  {endpoint.TransferType,-11} " +
                                      $"{endpoint.MaxPacketSize} bytes  interval {endpoint.Interval}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Descriptors above came off the remote device over the encrypted link.");
        Console.WriteLine();
        return 0;
    }
    finally
    {
        node.ReleaseImport(import.Key);
    }
}

/// <summary>
/// Imports a device and hands it to the machine's own virtual USB host controller, so the whole
/// operating system sees it rather than just this process.
/// </summary>
async Task<int> AttachAsync()
{
    if (options.Arguments.Count < 2)
    {
        Console.Error.WriteLine("error: attach needs a host and a device id.");
        return 1;
    }

    var (node, log) = CreateNode(echo: true);
    await using var _ = node;

    var remote = RequireRemote(node, options.Arguments[0]);
    if (remote is null) return 1;

    using var stopping = CreateShutdownSignal();

    var import = await node.ImportDeviceAsync(remote, options.Arguments[1], stopping.Token);
    var port = options.Flags.Contains("port") ? 3240 : 3240;
    var busId = await node.PublishToLocalControllerAsync(import, port, stopping.Token);

    var controller = LocalUsbController.Locate(log);
    if (controller is null)
    {
        Console.WriteLine();
        Console.WriteLine("  No virtual USB host controller is installed, so the device cannot be handed to the");
        Console.WriteLine("  operating system. It is still reachable from this process.");
        Console.WriteLine($"  A controller speaking USB/IP can attach to 127.0.0.1:{node.LocalController.Port} bus id {busId}.");
        Console.WriteLine();
    }
    else
    {
        log.Info("attach", $"Asking the local controller to attach bus id {busId}.");
        var attached = await controller.AttachAsync(node.LocalController.Port, busId, stopping.Token);
        if (!attached)
        {
            Console.Error.WriteLine("error: the local controller refused to attach the device.");
            node.ReleaseImport(import.Key);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  {import.Device.Info.DisplayName} is now attached to this machine.");
        Console.WriteLine("  It should appear in the operating system as a locally connected device.");
        Console.WriteLine("  Press Ctrl+C to give it back.");
        Console.WriteLine();
    }

    await stopping.WaitAsync();

    if (controller is not null)
    {
        log.Info("attach", "Detaching.");
        await controller.DetachAsync(busId, CancellationToken.None);
    }

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

RemoteHost? RequireRemote(NodeService node, string reference)
{
    var (address, port) = SplitHost(reference);
    var remote = node.Settings.RemoteHosts.FirstOrDefault(h =>
        h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

    if (remote is not null) return remote;

    Console.Error.WriteLine($"error: {address}:{port} is not paired. Run `nksusb pair {address}:{port} <token>` first.");
    return null;
}

/// <summary>
/// A shutdown signal that works whether or not there is a terminal attached.
/// Console.CancelKeyPress only fires for an interactive Ctrl+C, so a process started by a service
/// manager or with nohup would otherwise never hear a stop request and would have to be killed,
/// leaving devices attached behind it.
/// </summary>
static ShutdownSignal CreateShutdownSignal() => new();


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
    if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port)) return (value[..separator], port);
    return (value, DefaultPorts.Link);
}

static string Truncate(string value, int width)
    => value.Length <= width ? value : value[..(width - 1)] + "…";

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

        var command = positional.Count > 0 ? positional[0] : "help";
        if (flags.Contains("help") || flags.Contains("h")) command = "help";
        if (flags.Contains("version")) command = "version";

        return new CommandLine(command.ToLowerInvariant(), positional.Skip(1).ToArray(), flags, home);
    }
}

internal sealed class ShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly List<PosixSignalRegistration> _registrations = [];

    public ShutdownSignal()
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        foreach (var signal in (PosixSignal[])[PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT])
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    _source.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists on every platform; the others still cover shutdown.
            }
        }
    }

    public CancellationToken Token => _source.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        _source.Cancel();
    }

    public async Task WaitAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _source.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        foreach (var registration in _registrations) registration.Dispose();
        _source.Dispose();
    }
}
