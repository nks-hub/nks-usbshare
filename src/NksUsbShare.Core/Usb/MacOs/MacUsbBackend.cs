using System.Runtime.Versioning;
using static NksUsbShare.Core.Usb.MacOs.MacNative;

namespace NksUsbShare.Core.Usb.MacOs;

/// <summary>
/// Reads the USB topology out of the IOKit registry.
///
/// Enumeration is complete. Opening a device goes through the IOUSBLib CFPlugIn interface,
/// which lives in <see cref="MacUsbDeviceHandle"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacUsbBackend : IUsbBackend
{
    public string Name => "macOS IOKit";

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<UsbDeviceInfo>>(Enumerate, cancellationToken);

    private static List<UsbDeviceInfo> Enumerate()
    {
        var results = new List<UsbDeviceInfo>();

        // IOUSBHostDevice is the modern class; IOUSBDevice is the legacy name still used by
        // older releases. Query both and de-duplicate on the registry entry id.
        var seen = new HashSet<ulong>();
        foreach (var className in (string[])["IOUSBHostDevice", "IOUSBDevice"])
            CollectMatching(className, seen, results);

        return results;
    }

    private static void CollectMatching(string className, HashSet<ulong> seen, List<UsbDeviceInfo> results)
    {
        var matching = IOServiceMatching(className);
        if (matching == IntPtr.Zero) return;

        // IOServiceGetMatchingServices consumes the matching dictionary reference.
        if (IOServiceGetMatchingServices(0, matching, out var iterator) != KernSuccess) return;

        try
        {
            for (var service = IOIteratorNext(iterator); service != 0; service = IOIteratorNext(iterator))
            {
                try
                {
                    if (IORegistryEntryGetRegistryEntryID(service, out var entryId) != KernSuccess) continue;
                    if (!seen.Add(entryId)) continue;

                    if (IORegistryEntryCreateCFProperties(service, out var properties, IntPtr.Zero, 0) != KernSuccess)
                        continue;

                    try
                    {
                        results.Add(Describe(properties, entryId));
                    }
                    finally
                    {
                        CFRelease(properties);
                    }
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }
    }

    private static UsbDeviceInfo Describe(IntPtr properties, ulong entryId)
    {
        var locationId = GetNumber(properties, "locationID") ?? 0;

        return new UsbDeviceInfo
        {
            // The registry entry id is stable for as long as the device stays plugged in,
            // and the location id identifies the physical port.
            DeviceId = $"{entryId:x}",
            VendorId = (ushort)(GetNumber(properties, "idVendor") ?? 0),
            ProductId = (ushort)(GetNumber(properties, "idProduct") ?? 0),
            DeviceRelease = (ushort)(GetNumber(properties, "bcdDevice") ?? 0),
            DeviceClass = (byte)(GetNumber(properties, "bDeviceClass") ?? 0),
            DeviceSubClass = (byte)(GetNumber(properties, "bDeviceSubClass") ?? 0),
            DeviceProtocol = (byte)(GetNumber(properties, "bDeviceProtocol") ?? 0),
            ConfigurationValue = (byte)(GetNumber(properties, "bConfigurationValue") ?? 0),
            Speed = (GetNumber(properties, "Device Speed") ?? -1) switch
            {
                0 => UsbSpeed.Low,
                1 => UsbSpeed.Full,
                2 => UsbSpeed.High,
                3 => UsbSpeed.Super,
                4 or 5 => UsbSpeed.SuperPlus,
                _ => UsbSpeed.Unknown,
            },
            Manufacturer = GetString(properties, "USB Vendor Name"),
            Product = GetString(properties, "USB Product Name"),
            SerialNumber = GetString(properties, "USB Serial Number"),
            BusPath = $"location 0x{locationId:x8}",
            Interfaces = [],
            RequiresDriverDetach = true,
            ShareBlockedReason = MacUsbDeviceHandle.UnsupportedReason,
        };
    }

    public Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken)
        => throw new UsbBackendException(MacUsbDeviceHandle.UnsupportedReason);
}

/// <summary>
/// Placeholder for the IOUSBLib transfer path.
///
/// Opening a device on macOS means walking the IOCFPlugInInterface vtable to obtain an
/// IOUSBDeviceInterface, which is exact, version specific pointer arithmetic. Writing that
/// blind and calling it done would be dishonest, so until it is implemented and exercised on
/// real hardware this backend refuses to open devices and says why.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacUsbDeviceHandle
{
    internal const string UnsupportedReason =
        "Sharing from macOS is not implemented yet: the IOUSBLib transfer path still needs to be written and " +
        "verified on real hardware. Enumeration works, so macOS can already be used as the importing side.";
}
