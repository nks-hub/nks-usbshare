using System.Text.Json.Nodes;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Mcp;

public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<string>> Invoke);

/// <summary>
/// The management surface exposed to a language model: what is attached, what is shared, which
/// computers are paired, and what is currently in use from elsewhere.
/// </summary>
public sealed class ToolRegistry
{
    private readonly NodeService _node;

    public ToolRegistry(NodeService node) => _node = node;

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired) required.Add(name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>Result of a tool that needs no awaiting.</summary>
    private static Task<string> Done(string value) => Task.FromResult(value);

    private static string Require(JsonObject arguments, string name)
        => arguments[name]?.GetValue<string>()
           ?? throw new ArgumentException($"The '{name}' argument is required.");

    private static int Port(JsonObject arguments)
        => arguments["port"] is { } port ? port.GetValue<int>() : DefaultPorts.Link;

    public IReadOnlyList<ToolDefinition> All => BuildTools();

    private IReadOnlyList<ToolDefinition> BuildTools() =>
    [
        new("node_info",
            "Identity, platform, pairing token and listening address of this node.",
            Schema(),
            (_, _) =>
            {
                var addresses = string.Join(", ", UsbDisplay.LocalAddresses());
                return Done($"""
                    Node:           {_node.Settings.HostName}
                    Backend:        {_node.Backend.Name}
                    Fingerprint:    {_node.Identity.Fingerprint}
                    Pairing token:  {_node.Settings.PairingToken}
                    Link:           {_node.Settings.LinkBindAddress}:{_node.Settings.LinkPort}
                    Sharing:        {(_node.Host.IsRunning ? "listening" : "stopped")}
                    Reachable at:   {(addresses.Length > 0 ? addresses : "no network interface")}
                    Settings file:  {_node.Settings.SourcePath}
                    """);
            }),

        new("list_local_devices",
            "USB devices attached to this computer, with whether each one is shared.",
            Schema(),
            async (_, cancellationToken) =>
            {
                var devices = await _node.ListLocalDevicesWithSharesAsync(cancellationToken);
                if (devices.Count == 0) return "No USB devices are attached.";

                var lines = devices.Select(entry =>
                {
                    var (device, share) = entry;
                    var state = device.ShareBlockedReason is not null ? $"unavailable — {device.ShareBlockedReason}"
                        : share is { Enabled: true } ? "shared"
                        : "not shared";

                    return $"{device.HardwareKey}  {device.DisplayName}\n" +
                           $"    id      {device.DeviceId}\n" +
                           $"    type    {UsbDisplay.Kind(device)}, {UsbDisplay.Speed(device.Speed)}\n" +
                           $"    serial  {(device.SerialNumber.Length > 0 ? device.SerialNumber : "none")}\n" +
                           $"    state   {state}";
                });

                return string.Join("\n\n", lines);
            }),

        new("share_device",
            "Offer a local USB device to paired computers. A device the operating system is " +
            "holding is claimed first, which needs administrator approval and stops it working " +
            "locally until it is given back.",
            Schema(
                ("deviceId", "string", "Device id from list_local_devices.", true),
                ("claim", "boolean", "Claim the device from its current driver when it is held. False by default.", false)),
            async (arguments, cancellationToken) =>
            {
                var deviceId = Require(arguments, "deviceId");
                var devices = await _node.ListLocalDevicesWithSharesAsync(cancellationToken);
                var device = devices.FirstOrDefault(d => d.Device.DeviceId == deviceId).Device;

                if (device?.ShareBlockedReason is { } blocked)
                {
                    if (arguments["claim"]?.GetValue<bool>() != true)
                        return $"{device.DisplayName} is held by the operating system: {blocked}\n\n" +
                               "Call this tool again with claim set to true to take it over. " +
                               "The device stops working on that computer until release_claimed_devices is called.";

                    var claimed = await _node.ClaimForSharingAsync(deviceId, cancellationToken);

                    // The device is re-enumerated after the driver change, so find it again.
                    devices = await _node.ListLocalDevicesWithSharesAsync(cancellationToken);
                    var again = devices.FirstOrDefault(d =>
                        d.Device.HardwareKey == device.HardwareKey && d.Device.SerialNumber == device.SerialNumber);

                    if (again.Device is null || again.Device.ShareBlockedReason is not null)
                        return $"{claimed}\nThe device is not ready yet; unplug and replug it, then try again.";

                    deviceId = again.Device.DeviceId;
                }

                var share = await _node.ShareDeviceAsync(deviceId, cancellationToken);
                return $"'{share.Label}' is now offered to paired computers.";
            }),

        new("release_claimed_devices",
            "Give every device claimed for sharing back to the driver it had before.",
            Schema(),
            async (_, cancellationToken) => await _node.ReleaseClaimedDevicesAsync(cancellationToken)),

        new("unshare_device",
            "Stop offering a local USB device.",
            Schema(("deviceId", "string", "Device id from list_local_devices.", true)),
            (arguments, _) =>
            {
                var deviceId = Require(arguments, "deviceId");
                _node.UnshareDevice(deviceId);
                return Done($"{deviceId} is no longer shared.");
            }),

        new("list_clients",
            "Computers that have connected to this node, with whether each one is allowed in.",
            Schema(),
            (_, _) =>
            {
                if (_node.Settings.KnownClients.Count == 0)
                    return Done("No computer has connected to this node yet.");

                var live = _node.Host.Sessions.Select(s => s.ClientFingerprint).Where(f => f is not null).ToHashSet();
                return Done(string.Join("\n", _node.Settings.KnownClients.Select(client =>
                    $"{(client.Name.Length > 0 ? client.Name : "unnamed")}  {client.Fingerprint}  " +
                    $"last seen {client.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                    $"{(client.Blocked ? "blocked" : live.Any(f => NodeIdentity.FingerprintsMatch(f!, client.Fingerprint)) ? "connected" : "allowed")}")));
            }),

        new("set_client_blocked",
            "Refuse or re-admit a computer. A blocked computer is turned away even when it knows " +
            "the pairing token, and any devices it holds are released.",
            Schema(
                ("fingerprint", "string", "Key fingerprint from list_clients.", true),
                ("blocked", "boolean", "True to refuse the computer, false to let it back in.", true)),
            async (arguments, _) =>
            {
                var fingerprint = Require(arguments, "fingerprint");
                var blocked = arguments["blocked"]?.GetValue<bool>()
                    ?? throw new ArgumentException("The 'blocked' argument is required.");

                var client = _node.Settings.KnownClients
                    .FirstOrDefault(c => NodeIdentity.FingerprintsMatch(c.Fingerprint, fingerprint))
                    ?? throw new ArgumentException($"No computer has the fingerprint '{fingerprint}'.");

                client.Blocked = blocked;
                _node.Settings.Save();

                if (!blocked) return $"{client.Name} may connect again.";

                var dropped = await _node.Host.DisconnectClientAsync(client.Fingerprint);
                return dropped > 0
                    ? $"{client.Name} is blocked and was disconnected."
                    : $"{client.Name} is blocked and will be refused.";
            }),

        new("set_device_access",
            "Choose which computers may use a shared device. An empty list offers it to every " +
            "computer that knows the pairing token.",
            Schema(
                ("deviceId", "string", "Device id from list_local_devices.", true),
                ("fingerprints", "array", "Key fingerprints from list_clients. Empty or absent means anyone.", false)),
            (arguments, _) =>
            {
                var deviceId = Require(arguments, "deviceId");
                var share = _node.Settings.Shares.FirstOrDefault(s => s.DeviceId == deviceId)
                    ?? throw new ArgumentException($"{deviceId} is not shared, so there is nothing to allow.");

                var chosen = (arguments["fingerprints"] as JsonArray ?? [])
                    .Select(node => node?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToList();

                foreach (var fingerprint in chosen)
                {
                    if (!_node.Settings.KnownClients.Any(c => NodeIdentity.FingerprintsMatch(c.Fingerprint, fingerprint)))
                        throw new ArgumentException($"No computer has the fingerprint '{fingerprint}'.");
                }

                share.AllowedClients = chosen;
                _node.Settings.Save();

                return Done(chosen.Count == 0
                    ? $"'{share.Label}' is offered to any computer that knows the pairing token."
                    : $"'{share.Label}' is offered only to {chosen.Count} chosen computer(s).");
            }),

        new("list_hosts",
            "Computers this node is paired with.",
            Schema(),
            (_, _) =>
            {
                if (_node.Settings.RemoteHosts.Count == 0)
                    return Done("No computers are paired. Use pair_host to add one.");

                var connected = _node.ConnectedHosts.ToHashSet();
                return Done(string.Join("\n", _node.Settings.RemoteHosts.Select(host =>
                    $"{(host.Name.Length > 0 ? host.Name : host.Address)}  {host.Address}:{host.Port}  " +
                    $"{(connected.Contains($"{host.Address}:{host.Port}") ? "connected" : "not connected")}  " +
                    $"{host.PinnedFingerprint ?? "not paired yet"}")));
            }),

        new("pair_host",
            "Remember another computer and its pairing token so its devices can be used.",
            Schema(
                ("address", "string", "Host name or IP address.", true),
                ("token", "string", "Pairing token shown by that computer.", true),
                ("port", "integer", $"TCP port, {DefaultPorts.Link} by default.", false)),
            (arguments, _) =>
            {
                var address = Require(arguments, "address");
                var remote = _node.AddRemote(address, Port(arguments), Require(arguments, "token"));
                // A freshly supplied token means a fresh pairing, so any earlier pin no longer holds.
                remote.PinnedFingerprint = null;
                remote.AutoConnect = true;
                _node.Settings.Save();
                return Done($"Paired with {address}:{remote.Port}.");
            }),

        new("forget_host",
            "Remove a paired computer and release anything imported from it.",
            Schema(
                ("address", "string", "Host name or IP address.", true),
                ("port", "integer", $"TCP port, {DefaultPorts.Link} by default.", false)),
            async (arguments, _) =>
            {
                var address = Require(arguments, "address");
                var port = Port(arguments);
                await _node.DisconnectAsync(address, port);
                _node.RemoveRemote(address, port);
                return $"Removed {address}:{port}.";
            }),

        new("list_remote_devices",
            "Devices another computer is offering to this one.",
            Schema(
                ("address", "string", "Host name or IP address of a paired computer.", true),
                ("port", "integer", $"TCP port, {DefaultPorts.Link} by default.", false)),
            async (arguments, cancellationToken) =>
            {
                var remote = FindRemote(Require(arguments, "address"), Port(arguments));
                var devices = await _node.ListRemoteDevicesAsync(remote, cancellationToken);
                if (devices.Count == 0) return $"{remote.Address}:{remote.Port} offers nothing to this computer.";

                var imported = _node.Imports.Select(i => i.Key).ToHashSet();
                return string.Join("\n\n", devices.Select(device =>
                    $"{device.HardwareKey}  {device.DisplayName}\n" +
                    $"    id      {device.DeviceId}\n" +
                    $"    type    {UsbDisplay.Kind(device)}, {UsbDisplay.Speed(device.Speed)}\n" +
                    $"    state   {(imported.Contains($"{remote.Address}:{remote.Port}|{device.DeviceId}") ? "in use here" : "available")}"));
            }),

        new("import_device",
            "Start using a device that is attached to another computer.",
            Schema(
                ("address", "string", "Host name or IP address of a paired computer.", true),
                ("deviceId", "string", "Device id from list_remote_devices.", true),
                ("port", "integer", $"TCP port, {DefaultPorts.Link} by default.", false)),
            async (arguments, cancellationToken) =>
            {
                var remote = FindRemote(Require(arguments, "address"), Port(arguments));
                var import = await _node.ImportDeviceAsync(remote, Require(arguments, "deviceId"), cancellationToken);
                return $"'{import.Device.Info.DisplayName}' from {import.HostName} is now in use. Key: {import.Key}";
            }),

        new("release_device",
            "Stop using an imported device.",
            Schema(("key", "string", "Import key from list_imports.", true)),
            (arguments, _) =>
            {
                var key = Require(arguments, "key");
                _node.ReleaseImport(key);
                return Done($"Released {key}.");
            }),

        new("list_imports",
            "Devices this computer is currently using from elsewhere.",
            Schema(),
            (_, _) =>
            {
                var imports = _node.Imports;
                if (imports.Count == 0) return Done("No devices are in use from other computers.");

                return Done(string.Join("\n", imports.Select(import =>
                    $"{import.Device.Info.DisplayName}  from {import.HostName} ({import.HostAddress})  key {import.Key}")));
            }),

        new("recent_activity",
            "The most recent entries from this node's activity log.",
            Schema(("count", "integer", "How many entries, 40 by default.", false)),
            (arguments, _) =>
            {
                var count = arguments["count"]?.GetValue<int>() ?? 40;
                var entries = _node.Log.Recent(Math.Clamp(count, 1, 200));
                if (entries.Count == 0) return Done("Nothing has happened yet.");

                return Done(string.Join("\n", entries.Select(entry =>
                    $"{entry.Timestamp:HH:mm:ss}  {entry.Level.ToString().ToLowerInvariant(),-7} {entry.Category,-8} {entry.Message}")));
            }),
    ];

    private RemoteHost FindRemote(string address, int port)
        => _node.Settings.RemoteHosts.FirstOrDefault(h =>
               h.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && h.Port == port)
           ?? throw new ArgumentException($"{address}:{port} is not paired. Use pair_host first.");
}
