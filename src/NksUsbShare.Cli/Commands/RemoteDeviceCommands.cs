using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Storage;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Cli.Commands;

/// <summary>Using a device that is plugged into another computer.</summary>
internal sealed class RemoteDeviceCommands(Cli cli)
{
    public async Task<int> ListAsync()
    {
        if (cli.Options.Arguments.Count == 0) return Cli.Missing("remote", "a host address");

        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        var remote = Cli.RequireRemote(node, cli.Options.Arguments[0]);
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

    /// <summary>Holds a remote device open in this process, without involving the local OS.</summary>
    public async Task<int> ImportAsync()
    {
        if (cli.Options.Arguments.Count < 2)
            return Cli.Missing("import", "a host and a device id. Run `nksusb remote <host>` to see them");

        var (node, log) = cli.CreateNode(echo: true);
        await using var _ = node;

        var remote = Cli.RequireRemote(node, cli.Options.Arguments[0]);
        if (remote is null) return 1;

        using var stopping = Cli.Stopping();

        var import = await node.ImportDeviceAsync(remote, cli.Options.Arguments[1], stopping.Token);
        log.Info("app", $"Holding {import.Device.Info.DisplayName}. Press Ctrl+C to release it.");

        await stopping.WaitAsync();
        node.ReleaseImport(import.Key);
        return 0;
    }

    /// <summary>
    /// Opens a remote device and reads its descriptors straight off the hardware. This is the
    /// end-to-end proof that real USB traffic is crossing the link, not just metadata.
    /// </summary>
    public async Task<int> ProbeAsync()
    {
        if (cli.Options.Arguments.Count < 2) return Cli.Missing("probe", "a host and a device id");

        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        var remote = Cli.RequireRemote(node, cli.Options.Arguments[0]);
        if (remote is null) return 1;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var import = await node.ImportDeviceAsync(remote, cli.Options.Arguments[1], timeout.Token);
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
    /// Imports a device and hands it to the machine's own virtual USB host controller, so the
    /// whole operating system sees it rather than just this process.
    /// </summary>
    public async Task<int> AttachAsync()
    {
        if (cli.Options.Arguments.Count < 2) return Cli.Missing("attach", "a host and a device id");

        var (node, log) = cli.CreateNode(echo: true);
        await using var _ = node;

        var remote = Cli.RequireRemote(node, cli.Options.Arguments[0]);
        if (remote is null) return 1;

        using var stopping = Cli.Stopping();

        var import = await node.ImportDeviceAsync(remote, cli.Options.Arguments[1], stopping.Token);

        // 3240 is the port a USB/IP controller looks at by default, but another tool may already
        // hold it — usbipd-win runs a service on exactly that port. The endpoint falls back to a
        // free port on its own; --port forces a particular one.
        var port = cli.Options.Number("port", DefaultPorts.LocalController);
        var busId = await node.PublishToLocalControllerAsync(import, port, stopping.Token);

        // The controller is fetched and installed the first time it is needed, so this is not
        // something to go and set up beforehand.
        var controller = await LocalUsbController.EnsureAsync(log, stopping.Token);
        if (controller is null)
        {
            Console.WriteLine();
            Console.WriteLine("  No virtual USB host controller is installed and one could not be set up, so the");
            Console.WriteLine("  device cannot be handed to the operating system. It is still reachable from here.");
            Console.WriteLine($"  A controller speaking USB/IP can attach to 127.0.0.1:{node.LocalController.Port} bus id {busId}.");
            Console.WriteLine();
        }
        else
        {
            log.Info("attach", $"Asking the local controller to attach bus id {busId}.");
            if (!await controller.AttachAsync(node.LocalController.Port, busId, stopping.Token))
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

    /// <summary>
    /// Reads a remote storage device over SCSI without involving the local operating system. On a
    /// platform that cannot be given a virtual USB device at all, this is what "using" it means.
    /// </summary>
    public async Task<int> StorageAsync()
    {
        if (cli.Options.Arguments.Count < 2) return Cli.Missing("storage", "a host and a device id");

        var (node, _) = cli.CreateNode(echo: false);
        await using var _ = node;

        var remote = Cli.RequireRemote(node, cli.Options.Arguments[0]);
        if (remote is null) return 1;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var import = await node.ImportDeviceAsync(remote, cli.Options.Arguments[1], timeout.Token);

        try
        {
            var storage = MassStorageClient.Attach(import.Device);
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

            var partitions = MassStorageClient.ReadPartitions(sector);
            foreach (var (type, firstBlock, blockCount) in partitions)
                Console.WriteLine($"  partition        type 0x{type:x2}, {blockCount:N0} blocks from {firstBlock:N0}");

            // On a partitioned medium the volume starts at the partition, not at block zero.
            var volumeStart = partitions.Count > 0 ? partitions[0].FirstBlock : 0;
            var bootSector = volumeStart == 0
                ? sector
                : await storage.ReadBlocksAsync(volumeStart, 1, capacity.BlockSize, timeout.Token);

            var layout = FatDirectory.ReadLayout(bootSector, volumeStart);
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

            var label = FatDirectory.FindVolumeLabel(root);
            if (label is not null) Console.WriteLine($"  volume label     {label}");

            var entries = FatDirectory.Parse(root);
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
}
