using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.App.Web;

/// <summary>Shapes returned by the local HTTP API. Kept separate from the domain types so the
/// wire format of the user interface can change without disturbing the protocol.</summary>
public sealed record DeviceView(
    string DeviceId,
    string Name,
    string Manufacturer,
    string HardwareKey,
    string SerialNumber,
    string Speed,
    string BusPath,
    int InterfaceCount,
    int EndpointCount,
    string DeviceKind,
    bool Shared,
    bool ShareEnabled,
    string? BlockedReason,
    IReadOnlyList<string> AllowedClients);

public sealed record RemoteHostView(
    string Name, string Address, int Port, bool Connected, bool AutoConnect, string? Fingerprint);

public sealed record ImportView(
    string Key, string HostName, string HostAddress, string DeviceId, string Name, string Speed, DateTimeOffset ImportedAt);

public sealed record SessionView(
    string ClientName, string ClientPlatform, string? Fingerprint, string? RemoteEndPoint,
    DateTimeOffset ConnectedAt, IReadOnlyCollection<string> OpenDevices);

public sealed record LogView(string Time, string Level, string Category, string Message);

public sealed record NodeStateView(
    string HostName,
    string Platform,
    string Backend,
    string Fingerprint,
    string PairingToken,
    bool HostRunning,
    int LinkPort,
    string LinkBindAddress,
    bool RequireClientCertificate,
    IReadOnlyList<string> LocalAddresses,
    IReadOnlyList<DeviceView> Devices,
    IReadOnlyList<RemoteHostView> RemoteHosts,
    IReadOnlyList<ImportView> Imports,
    IReadOnlyList<SessionView> Sessions,
    IReadOnlyList<LogView> Log);

public static class ViewMapper
{
    public static DeviceView ToView(UsbDeviceInfo device, DeviceShare? share) => new(
        device.DeviceId,
        device.DisplayName,
        device.Manufacturer,
        device.HardwareKey,
        device.SerialNumber,
        Describe(device.Speed),
        device.BusPath,
        device.Interfaces.Count,
        device.Interfaces.Sum(i => i.Endpoints.Count),
        DescribeKind(device),
        share is not null,
        share?.Enabled ?? false,
        device.ShareBlockedReason,
        share?.AllowedClients ?? []);

    public static string Describe(UsbSpeed speed) => speed switch
    {
        UsbSpeed.Low => "1.5 Mbit/s",
        UsbSpeed.Full => "12 Mbit/s",
        UsbSpeed.High => "480 Mbit/s",
        UsbSpeed.Super => "5 Gbit/s",
        UsbSpeed.SuperPlus => "10 Gbit/s",
        _ => "unknown",
    };

    /// <summary>
    /// A human label for what the device is, taken from the USB class of the device or its first
    /// interface. Devices report class 0 and defer to their interfaces, so both are consulted.
    /// </summary>
    public static string DescribeKind(UsbDeviceInfo device)
    {
        var deviceClass = device.DeviceClass != 0
            ? device.DeviceClass
            : device.Interfaces.Select(i => i.InterfaceClass).FirstOrDefault();

        return deviceClass switch
        {
            0x01 => "Audio",
            0x02 => "Communications",
            0x03 => "Input device",
            0x05 => "Physical",
            0x06 => "Camera",
            0x07 => "Printer",
            0x08 => "Storage",
            0x09 => "Hub",
            0x0A => "Data",
            0x0B => "Smart card",
            0x0D => "Content security",
            0x0E => "Video",
            0x0F => "Health",
            0x10 => "Audio/video",
            0xE0 => "Wireless",
            0xEF => "Composite",
            0xFE => "Application specific",
            0xFF => "Vendor specific",
            _ => "Unclassified",
        };
    }

    public static LogView ToView(LogEntry entry)
        => new(entry.Timestamp.ToString("HH:mm:ss"), entry.Level.ToString().ToLowerInvariant(), entry.Category, entry.Message);

    public static ImportView ToView(ActiveImport import) => new(
        import.Key, import.HostName, import.HostAddress, import.Device.Info.DeviceId,
        import.Device.Info.DisplayName, Describe(import.Device.Info.Speed), import.ImportedAt);
}
