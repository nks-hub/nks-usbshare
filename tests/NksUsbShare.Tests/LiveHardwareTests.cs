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
    }
}
