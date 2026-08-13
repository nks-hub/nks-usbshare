using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// The Windows calls needed to hand a device to WinUSB and take it back: the device tree
/// (cfgmgr32), the driver store (setupapi), driver selection (newdev), catalog signing (wintrust)
/// and the hub port cycle (kernel32).
///
/// Every one of these ships with Windows. Nothing here needs the WDK, a toolkit, or a script
/// interpreter — the work used to run through PowerShell, which meant quoting, encoding and a
/// second copy of the logic to keep in step.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DriverNative
{
    // ---- cfgmgr32: the device tree ----

    internal const int CrSuccess = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key,
        out uint propertyType, byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        public Guid Format;
        public uint Pid;

        public DevPropKey(string format, uint pid)
        {
            Format = new Guid(format);
            Pid = pid;
        }
    }

    // The property keys used here, from devpkey.h.
    internal static DevPropKey DevPkeyDeviceHardwareIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    internal static DevPropKey DevPkeyDeviceService = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    internal static DevPropKey DevPkeyDeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    internal static DevPropKey DevPkeyDeviceDriverInfPath = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 5);
    internal static DevPropKey DevPkeyDeviceAddress = new("a45c254e-df1c-4efd-8020-67d146a850e0", 30);

    internal const uint DevPropTypeString = 0x00000012;
    internal const uint DevPropTypeStringList = 0x00002012;
    internal const uint DevPropTypeUInt32 = 0x00000007;

    // ---- setupapi: the driver store ----

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupCopyOEMInfW(string sourceInfFileName, string? oemSourceMediaLocation,
        uint oemSourceMediaType, uint copyStyle, char[]? destinationInfFileName, uint destinationInfFileNameSize,
        out uint requiredSize, IntPtr destinationInfFileNameComponent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupUninstallOEMInfW(string infFileName, uint flags, IntPtr reserved);

    internal const uint SpcopyNewer = 0x0004;
    internal const uint SpostNone = 0;
    internal const uint SuoiForceDelete = 0x0001;

    // ---- newdev: putting a driver on a device ----

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr parent, string hardwareId,
        string fullInfPath, uint installFlags, out bool rebootRequired);

    /// <summary>Install the named package even when Windows ranks another driver higher.</summary>
    internal const uint InstallflagForce = 0x0001;

    // ---- kernel32: the hub port cycle ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(IntPtr device, uint code, ref CyclePortParams input,
        int inputSize, ref CyclePortParams output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CyclePortParams
    {
        public uint ConnectionIndex;
        public uint StatusReturned;
    }

    internal const uint GenericReadWrite = 0xC0000000;
    internal const uint FileShareReadWrite = 0x00000003;
    internal const uint OpenExisting = 3;

    /// <summary>IOCTL_USB_HUB_CYCLE_PORT: turns a hub port off and on, a replug without the cable.</summary>
    internal const uint IoctlUsbHubCyclePort = 0x220444;

    /// <summary>GUID_DEVINTERFACE_USB_HUB, the interface a hub is opened through.</summary>
    internal const string UsbHubInterfaceGuid = "{f18a0e88-c30c-11d0-8815-00a0c906bed8}";

    // ---- wintrust: building the catalog ----

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CryptCATOpen(string fileName, uint openFlags, IntPtr cryptProvider,
        uint publicVersion, uint encodingType);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CryptCATPutMemberInfo(IntPtr catalog, string? fileName, string referenceTag,
        ref Guid subject, uint certVersion, uint indirectDataSize, IntPtr indirectData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CryptCATPutAttrInfo(IntPtr catalog, IntPtr member, string referenceTag,
        uint attributeFlags, uint valueSize, IntPtr value);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CryptCATPutCatAttrInfo(IntPtr catalog, string referenceTag,
        uint attributeFlags, uint valueSize, IntPtr value);

    [DllImport("wintrust.dll", SetLastError = true)]
    internal static extern bool CryptCATPersistStore(IntPtr catalog);

    [DllImport("wintrust.dll", SetLastError = true)]
    internal static extern bool CryptCATClose(IntPtr catalog);

    internal const uint CryptcatOpenCreatenew = 0x00000001;
    internal const uint CryptcatVersion1 = 0x00000100;

    internal const uint CryptcatAttrNameascii = 0x00000001;
    internal const uint CryptcatAttrDataascii = 0x00000002;
    internal const uint CryptcatAttrAuthenticated = 0x10000000;

    // ---- wintrust: hashing a file the way a catalog records it ----

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CryptSIPRetrieveSubjectGuid(string fileName, IntPtr fileHandle, out Guid subject);

    [DllImport("wintrust.dll", SetLastError = true)]
    internal static extern bool CryptSIPLoad(ref Guid subject, uint flags, ref SipDispatchInfo dispatch);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SipDispatchInfo
    {
        public uint Size;
        public uint Version;
        public IntPtr EventHandle;
        public IntPtr GetSignedDataMsg;
        public IntPtr PutSignedDataMsg;
        public IntPtr CreateIndirectData;
        public IntPtr VerifyIndirectData;
        public IntPtr RemoveSignedDataMsg;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SipSubjectInfo
    {
        public uint Size;
        public IntPtr SubjectGuid;
        public uint DisplayInfoFlags;
        public IntPtr MemoryHandle;
        public IntPtr FileName;
        public IntPtr FileHandle;
        public IntPtr Reserved;
        public uint Index;
        public uint Reserved2;
        public IntPtr Reserved3;
        public IntPtr DigestAlgorithm;
        public uint Flags;
        public uint Reserved4;
        public IntPtr Reserved5;
        public IntPtr Reserved6;
        public IntPtr Reserved7;
        public IntPtr CryptProvider;
        public IntPtr Reserved8;
        public IntPtr Reserved9;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool CryptSipCreateIndirectData(ref SipSubjectInfo subject, ref uint dataSize, IntPtr data);
}
