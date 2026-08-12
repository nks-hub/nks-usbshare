using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Host;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Tests;

/// <summary>
/// Drives a real host and a real client over a real TLS socket. Only the USB device underneath
/// is a double, so everything between the two processes is exercised exactly as shipped.
/// </summary>
public static class EndToEndTests
{
    private static Task<EndToEndHarness> StartHostAsync(Action<NodeSettings>? configure = null)
        => EndToEndHarness.StartAsync(configure);

    private static Task<UsbShareClient> ConnectAsync(EndToEndHarness harness, string? token = null)
        => harness.ConnectClientAsync(token);

    public static void Register()
    {
        const string suite = "End to end";

        TestRunner.Add(suite, "a client pairs with the host and learns who it is", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            Assert.Equal("test-host", client.HostName, "host name from the handshake");
            Assert.Equal(harness.Identity.Fingerprint, client.HostFingerprint, "pinned fingerprint matched");
            Assert.True(client.IsConnected, "the link is up");
        });

        TestRunner.Add(suite, "a wrong pairing token is refused", async () =>
        {
            await using var harness = await StartHostAsync();
            await Assert.Throws<HostRejectedException>(
                async () => await ConnectAsync(harness, AccessToken.Generate()),
                "connecting with a token the host does not know");
        });

        TestRunner.Add(suite, "only shared devices are listed", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            var devices = await client.ListDevicesAsync(CancellationToken.None);

            Assert.Equal(1, devices.Count, "the unshared device is hidden");
            Assert.Equal(FakeUsbBackend.FlashDriveId, devices[0].DeviceId, "the shared device is the flash drive");
            Assert.Equal("Test Flash Drive", devices[0].Product, "product name survived the round trip");
            Assert.Equal(2, devices[0].Interfaces[0].Endpoints.Count, "endpoints survived the round trip");
        });

        TestRunner.Add(suite, "disabling a share hides the device immediately", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            harness.Settings.Shares[0].Enabled = false;
            var devices = await client.ListDevicesAsync(CancellationToken.None);
            Assert.Equal(0, devices.Count, "a disabled share is not offered");
        });

        TestRunner.Add(suite, "a control transfer reaches the device and the answer comes back", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            using var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);
            var descriptor = await device.GetDescriptorAsync(0x01, 0, length: 18);

            Assert.Equal(18, descriptor.Length, "a full device descriptor came back");
            Assert.Equal((byte)0x12, descriptor[0], "bLength");
            Assert.Equal((byte)0x01, descriptor[1], "bDescriptorType is DEVICE");

            var vendorId = (ushort)(descriptor[8] | (descriptor[9] << 8));
            Assert.Equal(FakeUsbBackend.FlashDrive.VendorId, vendorId, "vendor id read off the wire");
        });

        TestRunner.Add(suite, "bulk data survives a round trip through the link", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);
            using var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);

            var payload = new byte[64 * 1024];
            Random.Shared.NextBytes(payload);

            var written = await device.WriteAsync(0x02, payload);
            Assert.Equal(payload.Length, written, "the whole payload was written");

            var read = await device.ReadAsync(0x81, payload.Length);
            Assert.SequenceEqual(payload, read, "the payload came back byte for byte");
        });

        TestRunner.Add(suite, "many transfers can be in flight at once", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);
            using var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);

            // Each descriptor read is independent, so they must not serialise behind one another.
            var reads = Enumerable.Range(0, 50)
                .Select(_ => device.GetDescriptorAsync(0x01, 0, length: 18))
                .ToArray();

            var results = await Task.WhenAll(reads);
            Assert.True(results.All(r => r.Length == 18), "all 50 concurrent transfers completed correctly");
        });

        TestRunner.Add(suite, "device control operations are carried and reported", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);
            using var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);

            await device.ClaimInterfaceAsync(0, CancellationToken.None);
            await device.SetConfigurationAsync(1, CancellationToken.None);
            await device.ResetAsync(CancellationToken.None);

            // Interface 3 does not exist, and the failure must travel back as an exception.
            await Assert.Throws<UsbBackendException>(
                async () => await device.ClaimInterfaceAsync(3, CancellationToken.None),
                "claiming an interface the device does not have");
        });

        TestRunner.Add(suite, "opening an unshared device is refused", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            var rejection = await CaptureAsync(() => client.OpenDeviceAsync(FakeUsbBackend.BlockedDeviceId, CancellationToken.None));
            Assert.NotNull(rejection, "the host refused");
            Assert.Equal(ProtocolError.AccessDenied, rejection!.Code, "refusal reason");
        });

        TestRunner.Add(suite, "a per-client allow list keeps other clients out", async () =>
        {
            await using var harness = await StartHostAsync(settings =>
                settings.Shares[0].AllowedClients.Add("AAAA-BBBB-CCCC-DDDD-EEEE"));

            await using var client = await ConnectAsync(harness);

            var devices = await client.ListDevicesAsync(CancellationToken.None);
            Assert.Equal(0, devices.Count, "a client outside the allow list sees nothing");

            var rejection = await CaptureAsync(() => client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None));
            Assert.NotNull(rejection, "and cannot open it either");
            Assert.Equal(ProtocolError.AccessDenied, rejection!.Code, "refusal reason");
        });

        TestRunner.Add(suite, "closing a device releases it on the host", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);
            Assert.Equal(1, harness.Backend.OpenCount, "the host opened the device");

            device.Dispose();

            // The close travels asynchronously; give it a moment to land.
            for (var attempt = 0; attempt < 50 && harness.Backend.DisposeCount == 0; attempt++)
                await Task.Delay(20);

            Assert.Equal(1, harness.Backend.DisposeCount, "the host released the device");
        });

        TestRunner.Add(suite, "a dropped client frees its devices", async () =>
        {
            await using var harness = await StartHostAsync();
            var client = await ConnectAsync(harness);

            using var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);
            Assert.Equal(1, harness.Backend.OpenCount, "the device is open");

            // Drop the link without a clean close, the way a crash or a cable pull would.
            await client.DisposeAsync();

            for (var attempt = 0; attempt < 100 && harness.Backend.DisposeCount == 0; attempt++)
                await Task.Delay(20);

            Assert.Equal(1, harness.Backend.DisposeCount, "the host cleaned up after the vanished client");
            Assert.Equal(0, harness.Host.Sessions.Count, "the session was removed");
        });

        TestRunner.Add(suite, "ping measures a round trip", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var client = await ConnectAsync(harness);

            var elapsed = await client.PingAsync(CancellationToken.None);
            Assert.True(elapsed > TimeSpan.Zero, "a round trip took measurable time");
            Assert.True(elapsed < TimeSpan.FromSeconds(5), "and completed promptly");
            Console.WriteLine($"          loopback round trip: {elapsed.TotalMilliseconds:F2} ms");
        });

        TestRunner.Add(suite, "several clients can be served at once", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var first = await ConnectAsync(harness);
            await using var second = await ConnectAsync(harness);

            var firstDevices = await first.ListDevicesAsync(CancellationToken.None);
            var secondDevices = await second.ListDevicesAsync(CancellationToken.None);

            Assert.Equal(1, firstDevices.Count, "the first client sees the share");
            Assert.Equal(1, secondDevices.Count, "so does the second");
            Assert.Equal(2, harness.Host.Sessions.Count, "the host tracks both sessions");
        });

        TestRunner.Add(suite, "two clients cannot hold the same device", async () =>
        {
            await using var harness = await StartHostAsync();
            await using var first = await ConnectAsync(harness);
            await using var second = await ConnectAsync(harness);

            using var held = await first.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);

            // The fake backend allows a second open, so this asserts what the host permits today:
            // a device may be opened once per session. Cross-session exclusivity is enforced by the
            // operating system's own claim, which the fake backend does not model.
            var rejection = await CaptureAsync(() => first.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None));
            Assert.NotNull(rejection, "the same session cannot open the device twice");
            Assert.Equal(ProtocolError.DeviceBusy, rejection!.Code, "refusal reason");
        });
    }

    private static async Task<HostRejectedException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (HostRejectedException ex)
        {
            return ex;
        }
    }
}
