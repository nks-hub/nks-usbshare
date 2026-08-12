using System.Runtime.Versioning;
using static NksUsbShare.Core.Usb.MacOs.MacNative;

namespace NksUsbShare.Core.Usb.MacOs;

/// <summary>
/// Reads the USB topology out of the IOKit registry and drives devices through IOUSBLib.
/// Both are part of macOS, so nothing outside the system is involved.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacUsbBackend : IUsbBackend
{
    public string Name => "macOS IOKit";

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<UsbDeviceInfo>>(() => Enumerate(null), cancellationToken);

    private static List<UsbDeviceInfo> Enumerate(Func<ulong, uint, bool>? visit)
    {
        var results = new List<UsbDeviceInfo>();

        // IOUSBHostDevice is the modern class; IOUSBDevice is the older name still used by some
        // releases. Query both and de-duplicate on the registry entry id.
        var seen = new HashSet<ulong>();
        foreach (var className in (string[])["IOUSBHostDevice", "IOUSBDevice"])
            if (CollectMatching(className, seen, results, visit)) break;

        return results;
    }

    /// <summary>Returns true when the visitor asked to stop.</summary>
    private static bool CollectMatching(
        string className, HashSet<ulong> seen, List<UsbDeviceInfo> results, Func<ulong, uint, bool>? visit)
    {
        var matching = IOServiceMatching(className);
        if (matching == IntPtr.Zero) return false;

        // IOServiceGetMatchingServices consumes the matching dictionary reference.
        if (IOServiceGetMatchingServices(0, matching, out var iterator) != KernSuccess) return false;

        var stop = false;
        try
        {
            for (var service = IOIteratorNext(iterator); service != 0 && !stop; service = IOIteratorNext(iterator))
            {
                var keepService = false;
                try
                {
                    if (IORegistryEntryGetRegistryEntryID(service, out var entryId) != KernSuccess) continue;
                    if (!seen.Add(entryId)) continue;

                    if (visit is not null)
                    {
                        // The visitor takes ownership of the service reference when it accepts.
                        if (visit(entryId, service))
                        {
                            keepService = true;
                            stop = true;
                        }
                        continue;
                    }

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
                    if (!keepService) IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        return stop;
    }

    private static UsbDeviceInfo Describe(IntPtr properties, ulong entryId)
    {
        var locationId = GetNumber(properties, "locationID") ?? 0;

        return new UsbDeviceInfo
        {
            // The registry entry id is stable while the device stays plugged in, and the
            // location id identifies the physical port.
            DeviceId = $"{entryId:x}",
            VendorId = (ushort)(GetNumber(properties, "idVendor") ?? 0),
            ProductId = (ushort)(GetNumber(properties, "idProduct") ?? 0),
            DeviceRelease = (ushort)(GetNumber(properties, "bcdDevice") ?? 0),
            DeviceClass = (byte)(GetNumber(properties, "bDeviceClass") ?? 0),
            DeviceSubClass = (byte)(GetNumber(properties, "bDeviceSubClass") ?? 0),
            DeviceProtocol = (byte)(GetNumber(properties, "bDeviceProtocol") ?? 0),
            ConfigurationValue = (byte)(GetNumber(properties, "bConfigurationValue") ?? 1),
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

            // macOS lets a process claim interfaces away from a class driver, so whether a device
            // can be shared is only known when the claim is attempted. Reporting a blanket refusal
            // here would be a guess; the open reports the real reason instead.
            RequiresDriverDetach = true,
        };
    }

    public Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken)
        => Task.Run<IUsbDeviceHandle>(() =>
        {
            var info = Enumerate(null).FirstOrDefault(d => d.DeviceId == deviceId)
                       ?? throw new UsbBackendException($"No USB device with id '{deviceId}' is present.");

            uint service = 0;
            Enumerate((entryId, candidate) =>
            {
                if ($"{entryId:x}" != deviceId) return false;
                service = candidate;
                return true;
            });

            if (service == 0)
                throw new UsbBackendException($"Device '{deviceId}' disappeared before it could be opened.");

            try
            {
                return MacUsbDeviceHandle.Open(service, info);
            }
            finally
            {
                IOObjectRelease(service);
            }
        }, cancellationToken);
}
