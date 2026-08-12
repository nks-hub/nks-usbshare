using System.Runtime.Versioning;

namespace NksUsbShare.Core.Usb.Linux;

/// <summary>
/// Reads the bus topology out of sysfs and drives devices through usbfs.
/// Both are kernel interfaces, so nothing outside the OS is involved.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxUsbBackend : IUsbBackend
{
    private const string SysfsUsbDevices = "/sys/bus/usb/devices";
    private const string DevBusUsb = "/dev/bus/usb";

    public string Name => "Linux usbfs";

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<UsbDeviceInfo>>(Enumerate, cancellationToken);

    private static List<UsbDeviceInfo> Enumerate()
    {
        var results = new List<UsbDeviceInfo>();
        if (!Directory.Exists(SysfsUsbDevices)) return results;

        foreach (var path in Directory.EnumerateDirectories(SysfsUsbDevices).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);

            // Entries containing ':' are interfaces, not devices.
            if (name.Contains(':')) continue;
            if (!File.Exists(Path.Combine(path, "idVendor"))) continue;

            try
            {
                results.Add(Describe(path, name));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(new UsbDeviceInfo
                {
                    DeviceId = name,
                    VendorId = 0,
                    ProductId = 0,
                    Product = "Unreadable USB device",
                    BusPath = name,
                    ShareBlockedReason = $"Enumeration failed: {ex.Message}",
                });
            }
        }

        return results;
    }

    private static UsbDeviceInfo Describe(string path, string name)
    {
        var busNumber = ReadInt(path, "busnum");
        var deviceNumber = ReadInt(path, "devnum");
        var nodePath = NodePath(busNumber, deviceNumber);

        var interfaces = Array.Empty<UsbInterfaceInfo>() as IReadOnlyList<UsbInterfaceInfo>;
        var descriptorsFile = Path.Combine(path, "descriptors");
        if (File.Exists(descriptorsFile))
        {
            var raw = ReadAllBytesUnbuffered(descriptorsFile);
            // The blob is the 18 byte device descriptor followed by the configuration descriptors.
            if (raw.Length > 18) interfaces = UsbDescriptorParser.ParseConfiguration(raw.AsSpan(18));
        }

        var boundDrivers = BoundDrivers(path, name);
        var canOpen = File.Exists(nodePath) && HasWriteAccess(nodePath);

        string? blocked = null;
        if (!File.Exists(nodePath))
            blocked = $"No usbfs node at {nodePath}.";
        else if (!canOpen)
            blocked = $"No write access to {nodePath}. Run as root or grant access with a udev rule.";

        return new UsbDeviceInfo
        {
            DeviceId = name,
            VendorId = ReadHex16(path, "idVendor"),
            ProductId = ReadHex16(path, "idProduct"),
            DeviceRelease = ReadHex16(path, "bcdDevice"),
            DeviceClass = (byte)ReadHexInt(path, "bDeviceClass"),
            DeviceSubClass = (byte)ReadHexInt(path, "bDeviceSubClass"),
            DeviceProtocol = (byte)ReadHexInt(path, "bDeviceProtocol"),
            ConfigurationValue = (byte)ReadInt(path, "bConfigurationValue"),
            Speed = ParseSpeed(ReadText(path, "speed")),
            Manufacturer = ReadText(path, "manufacturer"),
            Product = ReadText(path, "product"),
            SerialNumber = ReadText(path, "serial"),
            BusPath = $"bus {busNumber} device {deviceNumber} ({name})",
            Interfaces = interfaces,
            RequiresDriverDetach = boundDrivers.Count > 0,
            ShareBlockedReason = blocked,
        };
    }

    internal static string NodePath(int busNumber, int deviceNumber)
        => $"{DevBusUsb}/{busNumber:D3}/{deviceNumber:D3}";

    /// <summary>Kernel drivers currently attached to this device's interfaces.</summary>
    private static List<string> BoundDrivers(string devicePath, string deviceName)
    {
        var drivers = new List<string>();
        var parent = Path.GetDirectoryName(devicePath)!;

        foreach (var candidate in Directory.EnumerateDirectories(parent, deviceName + ":*"))
        {
            var driverLink = Path.Combine(candidate, "driver");
            if (!Directory.Exists(driverLink) && !File.Exists(driverLink)) continue;
            try
            {
                var target = Directory.ResolveLinkTarget(driverLink, returnFinalTarget: true);
                if (target is not null) drivers.Add(Path.GetFileName(target.FullName));
            }
            catch (IOException) { /* the interface disappeared mid-scan */ }
        }

        return drivers;
    }

    private static bool HasWriteAccess(string nodePath)
    {
        try
        {
            using var probe = new FileStream(nodePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static UsbSpeed ParseSpeed(string text) => text switch
    {
        "1.5" => UsbSpeed.Low,
        "12" => UsbSpeed.Full,
        "480" => UsbSpeed.High,
        "5000" => UsbSpeed.Super,
        "10000" or "20000" => UsbSpeed.SuperPlus,
        _ => UsbSpeed.Unknown,
    };

    private static string ReadText(string path, string file)
    {
        var full = Path.Combine(path, file);
        try { return File.Exists(full) ? File.ReadAllText(full).Trim() : ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static int ReadInt(string path, string file)
        => int.TryParse(ReadText(path, file), out var value) ? value : 0;

    private static int ReadHexInt(string path, string file)
        => int.TryParse(ReadText(path, file), System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : 0;

    private static ushort ReadHex16(string path, string file) => (ushort)ReadHexInt(path, file);

    /// <summary>
    /// sysfs binary attributes report a length of zero, so File.ReadAllBytes returns nothing.
    /// Read until EOF instead of trusting the reported size.
    /// </summary>
    private static byte[] ReadAllBytesUnbuffered(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public async Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var info = devices.FirstOrDefault(d => d.DeviceId == deviceId)
                   ?? throw new UsbBackendException($"No USB device with id '{deviceId}' is present.");

        if (info.ShareBlockedReason is { } reason) throw new UsbBackendException(reason);

        var sysfsPath = Path.Combine(SysfsUsbDevices, deviceId);
        var nodePath = NodePath(ReadInt(sysfsPath, "busnum"), ReadInt(sysfsPath, "devnum"));
        return UsbFsDeviceHandle.Open(nodePath, info);
    }
}
