using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// P/Invoke surface for the in-box Windows USB stack: SetupAPI and CfgMgr32 for enumeration,
/// the USB hub driver's IOCTLs for descriptors, and WinUSB for transfers.
/// Nothing outside the OS is used.
/// </summary>
internal static partial class WindowsNative
{
    internal static readonly Guid GuidDevInterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    internal static readonly Guid GuidDevInterfaceUsbHub = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");

    internal const uint DigcfPresent = 0x02;
    internal const uint DigcfDeviceInterface = 0x10;

    internal const uint SpdrpDeviceDesc = 0x00;
    internal const uint SpdrpHardwareId = 0x01;
    internal const uint SpdrpService = 0x04;
    internal const uint SpdrpDriver = 0x09;
    internal const uint SpdrpFriendlyName = 0x0C;
    internal const uint SpdrpAddress = 0x1C;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x01;
    internal const uint FileShareWrite = 0x02;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;

    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorNoMoreItems = 259;
    internal const int ErrorSemTimeout = 121;
    internal const int ErrorGenFailure = 31;
    internal const int ErrorOperationAborted = 995;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorNotEnoughMemory = 8;
    internal const int ErrorBadCommand = 22;
    internal const int ErrorDeviceNotConnected = 1167;

    internal const uint CrSuccess = 0;

    // CTL_CODE(FILE_DEVICE_USB=0x22, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    private static uint UsbCtlCode(uint function) => (0x22u << 16) | (function << 2);

    internal static readonly uint IoctlUsbGetNodeInformation = UsbCtlCode(258);
    internal static readonly uint IoctlUsbGetDescriptorFromNodeConnection = UsbCtlCode(260);
    internal static readonly uint IoctlUsbGetNodeConnectionName = UsbCtlCode(261);
    internal static readonly uint IoctlUsbGetNodeConnectionDriverkeyName = UsbCtlCode(264);
    internal static readonly uint IoctlUsbHubCyclePort = UsbCtlCode(273);
    internal static readonly uint IoctlUsbGetNodeConnectionInformationEx = UsbCtlCode(274);

    internal const byte UsbDeviceDescriptorType = 0x01;
    internal const byte UsbConfigurationDescriptorType = 0x02;
    internal const byte UsbStringDescriptorType = 0x03;

    // ---- SetupAPI ----

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SetupDiGetClassDevs(in Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, in Guid interfaceClassGuid, uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint detailSize, out uint requiredSize,
        ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        [Out] char[]? deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    // ---- CfgMgr32 ----

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_ID(uint devInst, [Out] char[] buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_Interface_List_Size(
        out uint length, in Guid interfaceClassGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_Interface_List(
        in Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Reenumerate_DevNode(uint devInst, uint flags);

    // ---- kernel32 ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        byte[]? inBuffer, uint inBufferSize,
        byte[]? outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    // ---- WinUSB ----

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WinUsbSetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    internal const uint PipeTransferTimeout = 0x03;
    internal const uint RawIo = 0x07;

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Free(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_GetAssociatedInterface(
        IntPtr interfaceHandle, byte associatedInterfaceIndex, out IntPtr associatedInterfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle, byte alternateSettingNumber, out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryPipe(
        IntPtr interfaceHandle, byte alternateSetting, byte pipeIndex, out WinUsbPipeInformation pipeInformation);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_SetCurrentAlternateSetting(IntPtr interfaceHandle, byte settingNumber);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_GetCurrentAlternateSetting(IntPtr interfaceHandle, out byte settingNumber);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ControlTransfer(
        IntPtr interfaceHandle, WinUsbSetupPacket setupPacket,
        byte[]? buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ReadPipe(
        IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_WritePipe(
        IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    // ---- helpers ----

    internal static string? GetStringProperty(IntPtr deviceInfoSet, ref SpDevinfoData devInfo, uint property)
    {
        SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfo, property, out _, null, 0, out var required);
        if (required == 0) return null;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfo, property, out _, buffer, required, out _))
            return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    internal static uint? GetUInt32Property(IntPtr deviceInfoSet, ref SpDevinfoData devInfo, uint property)
    {
        var buffer = new byte[4];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfo, property, out _, buffer, 4, out _))
            return null;
        return BitConverter.ToUInt32(buffer);
    }

    internal static string GetDeviceInstanceId(uint devInst)
    {
        var buffer = new char[512];
        return CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) != CrSuccess
            ? ""
            : new string(buffer, 0, Array.IndexOf(buffer, '\0') is var i && i >= 0 ? i : buffer.Length);
    }

    /// <summary>Resolves the first device interface path exposed by a device instance for a given interface class.</summary>
    internal static string? GetInterfacePath(string deviceInstanceId, in Guid interfaceClass)
    {
        const uint filterPresentDevices = 0;
        if (CM_Get_Device_Interface_List_Size(out var length, interfaceClass, deviceInstanceId, filterPresentDevices) != CrSuccess || length <= 1)
            return null;

        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(interfaceClass, deviceInstanceId, buffer, length, filterPresentDevices) != CrSuccess)
            return null;

        var end = Array.IndexOf(buffer, '\0');
        var first = new string(buffer, 0, end > 0 ? end : 0);
        return string.IsNullOrEmpty(first) ? null : first;
    }
}
