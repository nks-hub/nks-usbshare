using System.Runtime.InteropServices;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Tests;

/// <summary>
/// Tests that talk to the real USB bus of whatever machine they run on. They skip rather than
/// fail when there is no hardware, so the suite stays green in a container while still proving
/// the backend works on a developer machine.
/// </summary>
public static class LiveHardwareTests
{
    public static void Register()
    {
        const string suite = "Live hardware";

        TestRunner.Add(suite, "a backend exists for this platform", () =>
        {
            var backend = UsbBackend.Create();
            Assert.NotNull(backend.Name, "backend name");
            Console.WriteLine($"          backend: {backend.Name}");
        });

        TestRunner.Add(suite, "enumeration returns the devices actually on the bus", async () =>
        {
            var backend = UsbBackend.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var devices = await backend.EnumerateAsync(timeout.Token);

            if (devices.Count == 0)
                Assert.Skip("no USB devices are attached to this machine");

            foreach (var device in devices)
            {
                Console.WriteLine($"          {device.VendorId:x4}:{device.ProductId:x4}  " +
                                  $"{device.DisplayName,-38} {device.Speed,-10} {device.BusPath}");

                Assert.True(device.DeviceId.Length > 0, $"device {device.DisplayName} has an id");
            }

            // Every device should present a plausible vendor id; an all-zero list means the
            // descriptor read path silently failed.
            Assert.True(devices.Any(d => d.VendorId != 0), "at least one device reported a vendor id");
        });

        TestRunner.Add(suite, "descriptors parse into interfaces for at least one device", async () =>
        {
            var backend = UsbBackend.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var devices = await backend.EnumerateAsync(timeout.Token);

            if (devices.Count == 0) Assert.Skip("no USB devices are attached to this machine");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Assert.Skip("the macOS backend does not read configuration descriptors yet");

            var withInterfaces = devices.Where(d => d.Interfaces.Count > 0).ToList();
            Assert.True(withInterfaces.Count > 0,
                "at least one device exposed a parsed configuration descriptor");

            foreach (var device in withInterfaces.Take(3))
            {
                Console.WriteLine($"          {device.DisplayName}: {device.Interfaces.Count} interface(s), " +
                                  $"{device.Interfaces.Sum(i => i.Endpoints.Count)} endpoint(s)");
            }
        });

        TestRunner.Add(suite, "unshareable devices explain why", async () =>
        {
            var backend = UsbBackend.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var devices = await backend.EnumerateAsync(timeout.Token);

            if (devices.Count == 0) Assert.Skip("no USB devices are attached to this machine");

            foreach (var device in devices.Where(d => d.ShareBlockedReason is not null))
                Assert.True(device.ShareBlockedReason!.Length > 10,
                    $"{device.DisplayName} gives a usable reason, not an empty string");

            var shareable = devices.Count(d => d.ShareBlockedReason is null);
            Console.WriteLine($"          {shareable} of {devices.Count} device(s) can be shared right now");
        });

        TestRunner.Add(suite, "opening a device that does not exist fails cleanly", async () =>
        {
            var backend = UsbBackend.Create();
            await Assert.Throws<UsbBackendException>(
                async () => await backend.OpenAsync("no-such-device-id", CancellationToken.None),
                "opening a bogus device id");
        });

        // A claim installs a driver package, and Windows refuses a package whose catalog does not
        // cover its INF. Building the catalog needs no administrator rights, so it is checked here.
        TestRunner.Add(suite, "a driver catalog is built for an INF", () =>
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip("catalogs are a Windows idea");

            var directory = Path.Combine(Path.GetTempPath(), "nksusb-cat-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);

            try
            {
                var infPath = Path.Combine(directory, "nksusb.inf");
                File.WriteAllText(infPath, "[Version]\r\nSignature = \"$Windows NT$\"\r\n",
                    System.Text.Encoding.Unicode);

                var catalogPath = Path.Combine(directory, "nksusb.cat");
                NksUsbShare.Core.Usb.Windows.DriverPackage.BuildCatalog(directory, catalogPath, @"USB\VID_0000&PID_0000");

                Assert.True(File.Exists(catalogPath), "the catalog is written");

                var length = new FileInfo(catalogPath).Length;
                Assert.True(length > 200, $"and holds a real store ({length} bytes)");

                // Windows itself has to be able to read it back, member and all.
                var handle = NksUsbShare.Core.Usb.Windows.DriverNative.CryptCATOpen(catalogPath, 0, IntPtr.Zero, 0, 0);
                Assert.True(handle != IntPtr.Zero && handle != new IntPtr(-1), "Windows opens it as a catalog");

                var members = 0;
                var member = IntPtr.Zero;
                while ((member = NksUsbShare.Core.Usb.Windows.DriverNative.CryptCATEnumerateMember(handle, member)) != IntPtr.Zero)
                    members++;

                NksUsbShare.Core.Usb.Windows.DriverNative.CryptCATClose(handle);
                Assert.True(members > 0, $"and finds the INF inside it ({members} members)");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        // Finding the virtual host controller means walking the whole tree, not just USB devices.
        TestRunner.Add(suite, "the whole device tree can be walked", () =>
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip("this walk is Windows only");

            var all = NksUsbShare.Core.Usb.Windows.DeviceNode.All().ToList();
            Assert.True(all.Count > 20, $"the walk reaches the whole tree ({all.Count} nodes)");

            var roots = all.Where(n => n.InstanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(roots.Count > 0, $"including software devices under ROOT\\ ({roots.Count})");

            foreach (var node in all.Where(n => n.InstanceId.StartsWith(@"ROOT\USB", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"          {node.InstanceId}  service={node.Service}  name={node.FriendlyName}");
        });

        // The claim on Windows reads the device tree directly rather than shelling out. This
        // checks that reading against real hardware answers the questions the claim depends on.
        TestRunner.Add(suite, "the device tree answers what a claim needs to know", async () =>
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip("the device tree is read this way only on Windows");

            var backend = UsbBackend.Create();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var devices = await backend.EnumerateAsync(timeout.Token);

            var usb = devices.FirstOrDefault(d => d.DeviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase));
            if (usb is null) Assert.Skip("no USB device is attached to this machine");

            var node = NksUsbShare.Core.Usb.Windows.DeviceNode.Open(usb!.DeviceId);
            Assert.NotNull(node, $"the device tree knows {usb.DeviceId}");

            Assert.True(node!.Service is { Length: > 0 }, $"it reports a driver ({node.Service})");
            Assert.True(node.HardwareIds.Count > 0, "and its hardware ids");
            Assert.NotNull(node.Parent(), "and the hub it hangs off");
            Assert.True(node.HubPort is not null, $"and the port it sits on ({node.HubPort})");

            Console.WriteLine($"          {usb.DisplayName}: driver {node.Service}, hub port {node.HubPort}");

            // Looking a device up by hardware id is how the claim finds it again after Windows
            // re-enumerates it, so it has to find the same node.
            var hardwareId = string.Join('\\', usb.DeviceId.Split('\\').Take(2));
            var found = NksUsbShare.Core.Usb.Windows.DeviceNode.FindByHardwareId(hardwareId);
            Assert.NotNull(found, $"searching by hardware id {hardwareId} finds it");
            Assert.Equal(usb.DeviceId.ToLowerInvariant(), found!.InstanceId.ToLowerInvariant(), "the same device");
        });
    }
}
