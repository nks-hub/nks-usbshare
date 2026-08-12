using System.Collections.Concurrent;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Host;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core;

/// <summary>A device this node has imported from somewhere else.</summary>
public sealed record ActiveImport(
    string HostName,
    string HostAddress,
    RemoteUsbDevice Device,
    DateTimeOffset ImportedAt)
{
    public string Key => $"{HostAddress}|{Device.Info.DeviceId}";
}

/// <summary>
/// The whole node in one object: what it shares, what it has imported, and who it is talking to.
/// The command line, the web interface and the MCP server are all thin skins over this.
/// </summary>
public sealed class NodeService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UsbShareClient> _connections = new();
    private readonly ConcurrentDictionary<string, ActiveImport> _imports = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public NodeSettings Settings { get; }
    public NodeIdentity Identity { get; }
    public IUsbBackend Backend { get; }
    public ActivityLog Log { get; }
    public HostServer Host { get; }

    /// <summary>
    /// Presents imported devices to the machine's own virtual USB host controller, which is what
    /// makes a remote device visible to every application rather than only to this process.
    /// </summary>
    public UsbIpEndpoint LocalController { get; }

    /// <summary>Raised whenever something changed that a user interface would want to redraw for.</summary>
    public event Action? StateChanged;

    public NodeService(NodeSettings settings, ActivityLog log, IUsbBackend? backend = null)
    {
        Settings = settings;
        Log = log;
        Backend = backend ?? UsbBackend.Create();
        Identity = NodeIdentity.LoadOrCreate(
            Path.GetDirectoryName(settings.SourcePath) ?? NodeSettings.DefaultStateDirectory, settings.HostName);
        Host = new HostServer(settings, Identity, Backend, log);
        LocalController = new UsbIpEndpoint(log);
    }

    public IReadOnlyCollection<ActiveImport> Imports => _imports.Values.ToArray();
    public IReadOnlyCollection<string> ConnectedHosts => _connections.Keys.ToArray();

    private void Changed() => StateChanged?.Invoke();

    // ---- sharing ----

    public Task<IReadOnlyList<UsbDeviceInfo>> ListLocalDevicesAsync(CancellationToken cancellationToken)
        => Backend.EnumerateAsync(cancellationToken);

    /// <summary>Pairs each local device with its share record, which is what a user interface needs.</summary>
    public async Task<IReadOnlyList<(UsbDeviceInfo Device, DeviceShare? Share)>> ListLocalDevicesWithSharesAsync(
        CancellationToken cancellationToken)
    {
        var devices = await Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(d => (Device: d, Share: Settings.FindShare(d.DeviceId, d.HardwareKey, d.SerialNumber)))
            .ToArray();
    }

    public async Task<DeviceShare> ShareDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var devices = await Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.DeviceId == deviceId)
                     ?? throw new UsbBackendException($"No device with id '{deviceId}' is attached.");

        var share = Settings.FindShare(device.DeviceId, device.HardwareKey, device.SerialNumber);
        if (share is null)
        {
            share = new DeviceShare
            {
                DeviceId = device.DeviceId,
                HardwareKey = device.HardwareKey,
                SerialNumber = device.SerialNumber,
                Label = device.DisplayName,
            };
            Settings.Shares.Add(share);
        }

        share.DeviceId = device.DeviceId;
        share.Enabled = true;
        Settings.Save();

        Log.Info("share", $"Sharing {device.DisplayName} ({device.HardwareKey}).");
        Changed();
        return share;
    }

    /// <summary>
    /// True when a device the operating system is holding could be claimed for sharing on this
    /// platform, so a user interface knows whether to offer it.
    /// </summary>
    public static bool CanClaimHeldDevices => OperatingSystem.IsWindows();

    /// <summary>
    /// Takes a device away from the driver that currently owns it so it can be forwarded.
    /// This is what makes sharing work for devices Windows would otherwise keep to itself, and it
    /// asks for administrator approval because changing a device's driver is an administrative act.
    /// </summary>
    public async Task<string> ClaimForSharingAsync(string deviceId, CancellationToken cancellationToken)
    {
        var devices = await Backend.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.DeviceId == deviceId)
                     ?? throw new UsbBackendException($"No device with id '{deviceId}' is attached.");

        if (device.ShareBlockedReason is null) return "That device is already available for sharing.";

        if (!OperatingSystem.IsWindows())
            throw new UsbBackendException(device.ShareBlockedReason);

        var result = await new Usb.Windows.WinUsbBinder(Log).BindAsync(device, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new UsbBackendException(result.Message);

        Changed();
        return result.Message;
    }

    /// <summary>Gives every claimed device back to the driver it had before it was shared.</summary>
    public async Task<string> ReleaseClaimedDevicesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return "Nothing to give back: devices are only claimed this way on Windows.";

        var result = await new Usb.Windows.WinUsbBinder(Log).RestoreAllAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new UsbBackendException(result.Message);

        Changed();
        return result.Message;
    }

    public void UnshareDevice(string deviceId)
    {
        var removed = Settings.Shares.RemoveAll(s => s.DeviceId == deviceId);
        if (removed > 0)
        {
            Settings.Save();
            Log.Info("share", $"Stopped sharing {deviceId}.");
            Changed();
        }
    }

    public void SetShareEnabled(string deviceId, bool enabled)
    {
        var share = Settings.Shares.FirstOrDefault(s => s.DeviceId == deviceId);
        if (share is null || share.Enabled == enabled) return;

        share.Enabled = enabled;
        Settings.Save();
        Log.Info("share", $"{(enabled ? "Enabled" : "Disabled")} share for {share.Label}.");
        Changed();
    }

    public async Task StartHostAsync(CancellationToken cancellationToken)
    {
        if (Host.IsRunning) return;
        await Host.StartAsync(cancellationToken).ConfigureAwait(false);
        Changed();
    }

    public async Task StopHostAsync()
    {
        await Host.StopAsync().ConfigureAwait(false);
        Changed();
    }

    // ---- importing ----

    private static string HostKey(string address, int port) => $"{address}:{port}";

    /// <summary>Connects to a host, reusing an existing link when there already is one.</summary>
    public async Task<UsbShareClient> ConnectAsync(RemoteHost remote, CancellationToken cancellationToken)
    {
        var key = HostKey(remote.Address, remote.Port);

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(key, out var existing) && existing.IsConnected) return existing;

            var client = new UsbShareClient(Log);
            client.Disconnected += reason =>
            {
                _connections.TryRemove(key, out _);
                foreach (var import in _imports.Values.Where(i => i.HostAddress == key).ToArray())
                    _imports.TryRemove(import.Key, out _);

                if (reason is not null) Log.Warning("client", $"Link to {key} dropped: {reason.Message}");
                Changed();
            };

            // The identity is always presented. It costs nothing, and without it a host cannot tell
            // one client from another, so blocking a computer or restricting a device to named
            // computers would have nothing to match on.
            await client.ConnectAsync(remote.Address, remote.Port, remote.Token, remote.PinnedFingerprint,
                Settings.HostName, Identity.Certificate,
                cancellationToken).ConfigureAwait(false);

            // Trust on first use: remember what the host presented so a later change is caught.
            if (remote.PinnedFingerprint is null)
            {
                remote.PinnedFingerprint = client.HostFingerprint;
                Settings.Save();
                Log.Info("client", $"Pinned '{remote.Address}' to fingerprint {client.HostFingerprint}.");
            }

            if (remote.Name.Length == 0)
            {
                remote.Name = client.HostName;
                Settings.Save();
            }

            _connections[key] = client;
            Changed();
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(string address, int port)
    {
        var key = HostKey(address, port);
        if (!_connections.TryRemove(key, out var client)) return;

        foreach (var import in _imports.Values.Where(i => i.HostAddress == key).ToArray())
        {
            import.Device.Dispose();
            _imports.TryRemove(import.Key, out _);
        }

        await client.DisposeAsync().ConfigureAwait(false);
        Log.Info("client", $"Disconnected from {key}.");
        Changed();
    }

    public async Task<IReadOnlyList<UsbDeviceInfo>> ListRemoteDevicesAsync(
        RemoteHost remote, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
        return await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActiveImport> ImportDeviceAsync(
        RemoteHost remote, string deviceId, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
        var key = HostKey(remote.Address, remote.Port);

        if (_imports.TryGetValue($"{key}|{deviceId}", out var already)) return already;

        var device = await client.OpenDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var import = new ActiveImport(client.HostName, key, device, DateTimeOffset.UtcNow);
        _imports[import.Key] = import;

        Log.Info("import", $"Imported {device.Info.DisplayName} from '{client.HostName}'.");
        Changed();
        return import;
    }

    public void ReleaseImport(string importKey) => ReleaseImportAsync(importKey).GetAwaiter().GetResult();

    public async Task ReleaseImportAsync(string importKey)
    {
        // Take it away from the operating system first, so nothing is left holding a device
        // whose transfers can no longer be answered.
        await DetachFromSystemAsync(importKey, CancellationToken.None).ConfigureAwait(false);

        if (!_imports.TryRemove(importKey, out var import)) return;

        LocalController.Withdraw(import.Device);
        import.Device.Dispose();
        Log.Info("import", $"Released {import.Device.Info.DisplayName}.");
        Changed();
    }

    /// <summary>
    /// Publishes an imported device to the local virtual controller and returns the bus id the
    /// controller should attach to. Starts the endpoint on first use.
    /// </summary>
    public async Task<string> PublishToLocalControllerAsync(ActiveImport import, int port, CancellationToken cancellationToken)
    {
        if (!LocalController.IsRunning)
            await LocalController.StartAsync(port, cancellationToken).ConfigureAwait(false);

        return LocalController.Publish(import.Device);
    }

    /// <summary>Bus ids handed to the local controller, by import key.</summary>
    private readonly ConcurrentDictionary<string, string> _attached = new();

    /// <summary>True when this import has been handed to the machine's own USB stack.</summary>
    public bool IsAttachedToSystem(string importKey) => _attached.ContainsKey(importKey);

    /// <summary>Whether this machine has a virtual USB host controller at all.</summary>
    public bool SystemAttachAvailable => LocalUsbController.Locate(Log) is not null;

    /// <summary>
    /// Hands an imported device to this machine's virtual USB host controller, so every
    /// application sees it rather than only this process.
    /// </summary>
    public async Task AttachToSystemAsync(ActiveImport import, CancellationToken cancellationToken)
    {
        var controller = LocalUsbController.Locate(Log)
            ?? throw new InvalidOperationException(
                "No virtual USB host controller is installed, so the device cannot be handed to the operating system.");

        var busId = await PublishToLocalControllerAsync(import, DefaultPorts.LocalController, cancellationToken)
            .ConfigureAwait(false);

        if (!await controller.AttachAsync(LocalController.Port, busId, cancellationToken).ConfigureAwait(false))
        {
            LocalController.Withdraw(import.Device);
            throw new InvalidOperationException($"The {controller.Name} controller refused to attach the device.");
        }

        _attached[import.Key] = busId;
        Log.Info("attach", $"{import.Device.Info.DisplayName} is now attached to this machine.");
        Changed();
    }

    public async Task DetachFromSystemAsync(string importKey, CancellationToken cancellationToken)
    {
        if (!_attached.TryRemove(importKey, out var busId)) return;

        var controller = LocalUsbController.Locate(Log);
        if (controller is not null) await controller.DetachAsync(busId, cancellationToken).ConfigureAwait(false);

        if (_imports.TryGetValue(importKey, out var import)) LocalController.Withdraw(import.Device);

        Log.Info("attach", "Detached from this machine.");
        Changed();
    }

    public RemoteHost? FindRemote(string addressOrName)
        => Settings.RemoteHosts.FirstOrDefault(h =>
               h.Address.Equals(addressOrName, StringComparison.OrdinalIgnoreCase))
           ?? Settings.RemoteHosts.FirstOrDefault(h =>
               h.Name.Equals(addressOrName, StringComparison.OrdinalIgnoreCase));

    public RemoteHost AddRemote(string address, int port, string token, string? name = null)
    {
        var existing = Settings.RemoteHosts.FirstOrDefault(h =>
            h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        if (existing is not null)
        {
            existing.Token = token;
            if (name is not null) existing.Name = name;
            Settings.Save();
            Changed();
            return existing;
        }

        var remote = new RemoteHost { Address = address, Port = port, Token = token, Name = name ?? "" };
        Settings.RemoteHosts.Add(remote);
        Settings.Save();
        Changed();
        return remote;
    }

    public void RemoveRemote(string address, int port)
    {
        if (Settings.RemoteHosts.RemoveAll(h =>
                h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port) == 0) return;

        Settings.Save();
        Changed();
    }

    /// <summary>Connects every remote marked for auto-connect, reporting failures without stopping.</summary>
    public async Task AutoConnectAsync(CancellationToken cancellationToken)
    {
        foreach (var remote in Settings.RemoteHosts.Where(h => h.AutoConnect))
        {
            try
            {
                await ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning("client", $"Auto-connect to {remote.Address}:{remote.Port} failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var import in _imports.Values) import.Device.Dispose();
        _imports.Clear();

        foreach (var client in _connections.Values) await client.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();

        await LocalController.DisposeAsync().ConfigureAwait(false);

        await Host.DisposeAsync().ConfigureAwait(false);
        Identity.Certificate.Dispose();
        _connectLock.Dispose();
    }
}
