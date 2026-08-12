using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Host;
using NksUsbShare.Core.Security;

namespace NksUsbShare.Tests;

/// <summary>
/// A running host with one shared device, backed by a fake USB device so no hardware is needed.
/// Everything above the device — the protocol, the TLS, the sockets — is the real implementation.
/// </summary>
public sealed class EndToEndHarness : IAsyncDisposable
{
    public required NodeSettings Settings { get; init; }
    public required HostServer Host { get; init; }
    public required FakeUsbBackend Backend { get; init; }
    public required NodeIdentity Identity { get; init; }
    public required string StateDirectory { get; init; }

    public static async Task<EndToEndHarness> StartAsync(Action<NodeSettings>? configure = null)
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "nks-usbshare-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var settings = NodeSettings.LoadOrCreate(stateDirectory);
        settings.HostName = "test-host";
        settings.LinkBindAddress = "127.0.0.1";
        settings.LinkPort = 0; // let the operating system pick a free port
        settings.Shares.Add(new DeviceShare
        {
            DeviceId = FakeUsbBackend.FlashDriveId,
            HardwareKey = FakeUsbBackend.FlashDrive.HardwareKey,
            SerialNumber = FakeUsbBackend.FlashDrive.SerialNumber,
            Label = "Test Flash Drive",
            Enabled = true,
        });
        configure?.Invoke(settings);

        var identity = NodeIdentity.LoadOrCreate(stateDirectory, "test-host");
        var backend = new FakeUsbBackend();
        var host = new HostServer(settings, identity, backend, new ActivityLog());
        await host.StartAsync(CancellationToken.None);

        return new EndToEndHarness
        {
            Settings = settings,
            Host = host,
            Backend = backend,
            Identity = identity,
            StateDirectory = stateDirectory,
        };
    }

    public async Task<UsbShareClient> ConnectClientAsync(string? token = null, NodeIdentity? clientIdentity = null)
    {
        var client = new UsbShareClient(new ActivityLog());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync("127.0.0.1", Host.Port, token ?? Settings.PairingToken,
            Identity.Fingerprint, "test-client", clientIdentity?.Certificate, timeout.Token);
        return client;
    }

    /// <summary>A separate key, so a test can act as a second computer.</summary>
    public NodeIdentity CreateClientIdentity(string name)
    {
        var directory = Path.Combine(StateDirectory, "clients", name);
        Directory.CreateDirectory(directory);
        return NodeIdentity.LoadOrCreate(directory, name);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Identity.Certificate.Dispose();
        if (Directory.Exists(StateDirectory)) Directory.Delete(StateDirectory, true);
    }
}
