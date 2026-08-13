using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// Hands a Windows device over to the in-box winusb.sys so its transfers can be forwarded, and
/// gives it back afterwards. Everything is done through Windows itself — the device tree, the
/// driver store, the driver-selection call and the hub port cycle — so nothing is installed
/// alongside the application and Secure Boot can stay on.
///
/// The work needs administrator rights, so it runs in an elevated child process rather than here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbDriverInstaller(Action<string> report)
{
    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "nks-usbshare", "bound-devices.json");

    /// <summary>One device this tool has taken, and what it has to be given back to.</summary>
    public sealed record BoundDevice
    {
        public string HardwareId { get; init; } = "";
        public string InstanceId { get; init; } = "";
        public string FriendlyName { get; init; } = "";
        public string OriginalDriver { get; init; } = "";
        public string OriginalInf { get; init; } = "";
        public string PublishedName { get; init; } = "";
        public string InterfaceGuid { get; init; } = "";
        public DateTimeOffset BoundAt { get; init; } = DateTimeOffset.Now;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static IReadOnlyList<BoundDevice> Bound()
    {
        if (!File.Exists(StatePath)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<BoundDevice>>(File.ReadAllText(StatePath), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(IReadOnlyList<BoundDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(devices, Json));
    }

    /// <summary>Takes a device away from its current driver and puts it on WinUSB.</summary>
    public void Bind(string hardwareId, string description)
    {
        var device = DeviceNode.FindByHardwareId(hardwareId)
                     ?? throw new UsbBackendException($"No present device matches hardware id '{hardwareId}'.");

        var name = device.FriendlyName ?? hardwareId;
        report($"Found {name}. Preparing a driver package...");

        var current = device.Service ?? "none";
        if (string.Equals(current, "WinUSB", StringComparison.OrdinalIgnoreCase))
        {
            report($"{name} is already bound to WinUSB.");
            return;
        }

        report($"Device:          {name}");
        report($"Instance:        {device.InstanceId}");
        report($"Current driver:  {current} ({device.DriverInfPath ?? "unknown inf"})");

        using var package = DriverPackage.Create(hardwareId, description, report);
        report($"Driver package:  {package.InfPath}");

        var published = AddToDriverStore(package.InfPath);
        report($"Published as:    {published}");

        // Installing the package is not always enough. A vendor driver that is WHQL signed
        // outranks this one whatever the INF says, because Windows weighs the signature before it
        // weighs how specifically the hardware id matched. Forcing the choice is a documented
        // call, not a trick: it installs the named package and skips the ranking.
        if (!IsOnWinUsb(device))
        {
            report($"Still on '{device.Service}' - forcing the driver onto the device...");
            ForceInstall(package.InfPath, hardwareId);
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        // Windows can record the new driver and still leave the old stack standing: a composite
        // device keeps the interface nodes its previous driver created, and while those hold the
        // interfaces nothing can open it. A port cycle is the replug that clears them.
        var again = DeviceNode.Open(device.InstanceId) ?? device;
        if (!IsOnWinUsb(again) || again.Children().Count > 0)
        {
            report("Cycling the hub port so the device comes back on its new driver...");
            report("  " + again.CyclePort());
            Thread.Sleep(TimeSpan.FromSeconds(6));
        }

        var settled = DeviceNode.Open(device.InstanceId);
        var service = settled?.Service ?? "gone";
        report($"Driver now:      {service}");

        var entries = Bound().Where(b => !b.HardwareId.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Add(new BoundDevice
        {
            HardwareId = hardwareId,
            InstanceId = device.InstanceId,
            FriendlyName = name,
            OriginalDriver = current,
            OriginalInf = device.DriverInfPath ?? "",
            PublishedName = published,
            InterfaceGuid = package.InterfaceGuid,
        });
        Save(entries);

        if (!string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase))
            throw new UsbBackendException(
                $"The package installed but the device still reports '{service}'. Unplug it and plug it in again.");

        report("The device is ready to be shared.");
    }

    /// <summary>Gives every device this tool has taken back to the driver it had before.</summary>
    public void RestoreAll()
    {
        var entries = Bound();
        if (entries.Count == 0)
        {
            report("No devices are claimed.");
            Save([]);
            return;
        }

        // Which hub each device hangs off has to be noted first: uninstalling a driver package
        // takes the device node out of the tree, and once it is gone there is nothing left to ask.
        var hubs = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var node = DeviceNode.Open(entry.InstanceId);
            var parent = node?.Parent();
            if (parent is not null) hubs[entry.InstanceId] = parent;
        }

        foreach (var published in entries.Select(e => e.PublishedName).Where(p => p.Length > 0).Distinct())
        {
            report($"Removing driver package {published}...");
            if (!DriverNative.SetupUninstallOEMInfW(published, DriverNative.SuoiForceDelete, IntPtr.Zero))
                report($"  {published} could not be removed (Win32 {Marshal.GetLastWin32Error()}).");
        }

        foreach (var entry in entries)
        {
            var node = DeviceNode.Open(entry.InstanceId);

            // Uninstalling the package removes the device node, and a USB hub only reports a port
            // when something changes on it — so nothing brings the device back on its own. Asking
            // the hub to enumerate again is the software equivalent of a replug.
            if (node is null && hubs.TryGetValue(entry.InstanceId, out var hub))
            {
                report($"  {entry.FriendlyName}: gone from the device tree, asking its hub to look again...");
                hub.Reenumerate();
                Thread.Sleep(TimeSpan.FromSeconds(4));
                node = DeviceNode.Open(entry.InstanceId);
            }

            report(node?.Service is { } service
                ? $"  {entry.FriendlyName}: driver now {service} (was {entry.OriginalDriver})"
                : $"  {entry.FriendlyName}: did not come back; unplug it and plug it in again.");
        }

        Save([]);
        report("Done.");
    }

    private static bool IsOnWinUsb(DeviceNode device)
    {
        var fresh = DeviceNode.Open(device.InstanceId);
        return string.Equals(fresh?.Service, "WinUSB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies the package into the driver store and returns its published oemNN.inf name.</summary>
    private static string AddToDriverStore(string infPath)
    {
        var destination = new char[260];
        if (!DriverNative.SetupCopyOEMInfW(infPath, null, DriverNative.SpostNone, DriverNative.SpcopyNewer,
                destination, (uint)destination.Length, out _, IntPtr.Zero))
        {
            throw new UsbBackendException(
                $"The driver package was not accepted (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var end = Array.IndexOf(destination, '\0');
        var path = new string(destination, 0, end >= 0 ? end : destination.Length);
        return Path.GetFileName(path);
    }

    private void ForceInstall(string infPath, string hardwareId)
    {
        if (!DriverNative.UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath,
                DriverNative.InstallflagForce, out var rebootNeeded))
        {
            report($"  the forced install did not take (Win32 {Marshal.GetLastWin32Error()})");
            return;
        }

        report(rebootNeeded ? "  forced; Windows wants a reboot to finish" : "  forced");
    }
}
