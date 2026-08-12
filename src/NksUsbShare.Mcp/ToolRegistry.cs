using System.Text.Json.Nodes;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
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
            "Offer a local USB device to paired computers.",
            Schema(("deviceId", "string", "Device id from list_local_devices.", true)),
            async (arguments, cancellationToken) =>
            {
                var share = await _node.ShareDeviceAsync(Require(arguments, "deviceId"), cancellationToken);
                return $"'{share.Label}' is now offered to paired computers.";
            }),

        new("unshare_device",
            "Stop offering a local USB device.",
            Schema(("deviceId", "string", "Device id from list_local_devices.", true)),
            (arguments, _) =>
            {
                var deviceId = Require(arguments, "deviceId");
                _node.UnshareDevice(deviceId);
                return Done($"{deviceId} is no longer shared.");
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
