using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static NksUsbShare.Core.Usb.Windows.WindowsNative;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// Enumerates devices through SetupAPI and the USB hub driver, and drives them through WinUSB.
///
/// Enumeration works for every device on the bus because the hub driver answers descriptor
/// queries regardless of which function driver owns the device. Opening, however, requires the
/// device to be bound to winusb.sys: that is the only in-box Windows path for user-mode transfers.
/// Devices owned by a class driver report a <see cref="UsbDeviceInfo.ShareBlockedReason"/> instead
/// of pretending to be shareable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbBackend : IUsbBackend
{
    private const ushort DefaultLanguageId = 0x0409;

    public string Name => "Windows WinUSB";

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<UsbDeviceInfo>>(Enumerate, cancellationToken);

    private static List<UsbDeviceInfo> Enumerate()
    {
        var results = new List<UsbDeviceInfo>();
        var set = SetupDiGetClassDevs(GuidDevInterfaceUsbDevice, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new UsbBackendException($"SetupDiGetClassDevs failed (Win32 {Marshal.GetLastWin32Error()}).");

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, GuidDevInterfaceUsbDevice, index, ref interfaceData))
                    break;

                var devInfo = new SpDevinfoData { CbSize = Marshal.SizeOf<SpDevinfoData>() };
                var devicePath = GetInterfaceDetail(set, ref interfaceData, ref devInfo);
                if (devicePath is null) continue;

                try
                {
                    var device = DescribeDevice(set, ref devInfo, devicePath);
                    if (device is not null) results.Add(device);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One unreadable device must not hide the rest of the bus.
                    results.Add(new UsbDeviceInfo
                    {
                        DeviceId = devicePath,
                        VendorId = 0,
                        ProductId = 0,
                        Product = "Unreadable USB device",
                        ShareBlockedReason = $"Enumeration failed: {ex.Message}",
                    });
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return results;
    }

    private static string? GetInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData interfaceData, ref SpDevinfoData devInfo)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var required, ref devInfo);
        if (required == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on x64 (4 byte size + 2 byte char + padding), 6 on x86.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, out _, ref devInfo))
                return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static UsbDeviceInfo? DescribeDevice(IntPtr set, ref SpDevinfoData devInfo, string devicePath)
    {
        var instanceId = GetDeviceInstanceId(devInfo.DevInst);
        var hardwareId = GetStringProperty(set, ref devInfo, SpdrpHardwareId) ?? "";
        var service = GetStringProperty(set, ref devInfo, SpdrpService) ?? "";
        var description = GetStringProperty(set, ref devInfo, SpdrpFriendlyName)
                          ?? GetStringProperty(set, ref devInfo, SpdrpDeviceDesc)
                          ?? "";
        var port = GetUInt32Property(set, ref devInfo, SpdrpAddress) ?? 0;

        var (vendorId, productId) = ParseVidPid(hardwareId);

        var info = new DeviceProbe
        {
            VendorId = vendorId,
            ProductId = productId,
            Product = description,
        };

        // Ask the parent hub for the authoritative descriptors and link speed.
        if (CM_Get_Parent(out var parentDevInst, devInfo.DevInst, 0) == CrSuccess)
        {
            var parentId = GetDeviceInstanceId(parentDevInst);
            var hubPath = GetInterfacePath(parentId, GuidDevInterfaceUsbHub);
            if (hubPath is not null && port > 0)
                QueryHub(hubPath, port, info);
        }

        var claimable = service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase);

        return new UsbDeviceInfo
        {
            DeviceId = instanceId.Length > 0 ? instanceId : devicePath,
            VendorId = info.VendorId,
            ProductId = info.ProductId,
            DeviceRelease = info.DeviceRelease,
            DeviceClass = info.DeviceClass,
            DeviceSubClass = info.DeviceSubClass,
            DeviceProtocol = info.DeviceProtocol,
            ConfigurationValue = info.ConfigurationValue,
            Speed = info.Speed,
            Manufacturer = info.Manufacturer,
            Product = !string.IsNullOrWhiteSpace(info.Product) ? info.Product : description,
            SerialNumber = info.SerialNumber,
            BusPath = port > 0 ? $"port {port}" : "",
            Interfaces = info.Interfaces,
            RequiresDriverDetach = !claimable,
            ShareBlockedReason = claimable
                ? null
                : $"Device is driven by '{(service.Length > 0 ? service : "a class driver")}'. Windows only permits user-mode transfers on devices bound to winusb.sys.",
        };
    }

    private sealed class DeviceProbe
    {
        public ushort VendorId, ProductId, DeviceRelease;
        public byte DeviceClass, DeviceSubClass, DeviceProtocol, ConfigurationValue;
        public UsbSpeed Speed = UsbSpeed.Unknown;
        public string Manufacturer = "", Product = "", SerialNumber = "";
        public IReadOnlyList<UsbInterfaceInfo> Interfaces = [];
    }

    private static void QueryHub(string hubPath, uint port, DeviceProbe probe)
    {
        using var hub = CreateFile(hubPath, GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (hub.IsInvalid) return;

        // USB_NODE_CONNECTION_INFORMATION_EX: 36 byte header, parsed by offset to avoid marshalling ambiguity.
        var buffer = new byte[36 + 30 * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, port);
        if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationEx, buffer, (uint)buffer.Length,
                buffer, (uint)buffer.Length, out var returned, IntPtr.Zero) || returned < 36)
            return;

        // Device descriptor sits at offset 4 and is 18 bytes.
        var descriptor = buffer.AsSpan(4, 18);
        probe.DeviceClass = descriptor[4];
        probe.DeviceSubClass = descriptor[5];
        probe.DeviceProtocol = descriptor[6];
        probe.VendorId = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(8, 2));
        probe.ProductId = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(10, 2));
        probe.DeviceRelease = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(12, 2));
        var iManufacturer = descriptor[14];
        var iProduct = descriptor[15];
        var iSerial = descriptor[16];

        probe.ConfigurationValue = buffer[22];
        probe.Speed = buffer[23] switch
        {
            0 => UsbSpeed.Low,
            1 => UsbSpeed.Full,
            2 => UsbSpeed.High,
            3 => UsbSpeed.Super,
            _ => UsbSpeed.Unknown,
        };

        if (iManufacturer != 0) probe.Manufacturer = ReadStringDescriptor(hub, port, iManufacturer);
        if (iProduct != 0) probe.Product = ReadStringDescriptor(hub, port, iProduct);
        if (iSerial != 0) probe.SerialNumber = ReadStringDescriptor(hub, port, iSerial);

        var configuration = ReadConfigurationDescriptor(hub, port);
        if (configuration.Length > 0) probe.Interfaces = UsbDescriptorParser.ParseConfiguration(configuration);
    }

    /// <summary>
    /// Builds a USB_DESCRIPTOR_REQUEST: 4 byte connection index, 8 byte setup packet, then the data buffer.
    /// </summary>
    private static byte[]? HubDescriptorRequest(SafeFileHandle hub, uint port, byte descriptorType, byte descriptorIndex, ushort languageId, int dataLength)
    {
        const int headerSize = 12;
        var buffer = new byte[headerSize + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), port);
        buffer[4] = 0x80; // device to host, standard, device
        buffer[5] = 0x06; // GET_DESCRIPTOR
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), (ushort)((descriptorType << 8) | descriptorIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), languageId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), (ushort)dataLength);

        if (!DeviceIoControl(hub, IoctlUsbGetDescriptorFromNodeConnection, buffer, (uint)buffer.Length,
                buffer, (uint)buffer.Length, out var returned, IntPtr.Zero) || returned <= headerSize)
            return null;

        return buffer.AsSpan(headerSize, (int)returned - headerSize).ToArray();
    }

    private static string ReadStringDescriptor(SafeFileHandle hub, uint port, byte index)
    {
        var data = HubDescriptorRequest(hub, port, UsbStringDescriptorType, index, DefaultLanguageId, 255);
        return data is null ? "" : UsbDescriptorParser.DecodeStringDescriptor(data);
    }

    private static byte[] ReadConfigurationDescriptor(SafeFileHandle hub, uint port)
    {
        var header = HubDescriptorRequest(hub, port, UsbConfigurationDescriptorType, 0, 0, 9);
        if (header is null) return [];

        var total = UsbDescriptorParser.ReadConfigurationTotalLength(header);
        if (total is <= 9 or > 4096) return header;

        return HubDescriptorRequest(hub, port, UsbConfigurationDescriptorType, 0, 0, total) ?? header;
    }

    private static (ushort Vid, ushort Pid) ParseVidPid(string hardwareId)
    {
        ushort vid = 0, pid = 0;
        var vidIndex = hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex >= 0 && vidIndex + 8 <= hardwareId.Length)
            ushort.TryParse(hardwareId.AsSpan(vidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid);

        var pidIndex = hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pidIndex >= 0 && pidIndex + 8 <= hardwareId.Length)
            ushort.TryParse(hardwareId.AsSpan(pidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);

        return (vid, pid);
    }

    public async Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var info = devices.FirstOrDefault(d => d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new UsbBackendException($"No USB device with id '{deviceId}' is present.");

        if (info.ShareBlockedReason is { } reason)
            throw new UsbBackendException(reason);

        var path = GetInterfacePath(deviceId, GuidDevInterfaceUsbDevice)
                   ?? throw new UsbBackendException($"Device '{deviceId}' exposes no usable device interface.");

        return WinUsbDeviceHandle.Open(path, info);
    }
}
