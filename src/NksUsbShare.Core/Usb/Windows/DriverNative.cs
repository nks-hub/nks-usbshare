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

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// <summary>CM_PROB_NEED_RESTART: the device is waiting for the machine to be restarted.</summary>
    internal const uint CmProblemNeedRestart = 14;

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

    // Plenty of devices have no friendly name and are known only by their description; the tools
    // that show a "friendly name" are quietly falling back to this one.
    internal static DevPropKey DevPkeyDeviceDescription = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
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

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATOpen(string fileName, uint openFlags, IntPtr cryptProvider,
        uint publicVersion, uint encodingType);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATPutMemberInfo(IntPtr catalog, string? fileName, string referenceTag,
        ref Guid subject, uint certVersion, uint indirectDataSize, IntPtr indirectData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATPutAttrInfo(IntPtr catalog, IntPtr member, string referenceTag,
        uint attributeFlags, uint valueSize, IntPtr value);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr CryptCATPutCatAttrInfo(IntPtr catalog, string referenceTag,
        uint attributeFlags, uint valueSize, IntPtr value);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool CryptCATPersistStore(IntPtr catalog);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool CryptCATClose(IntPtr catalog);

    internal const uint CryptcatOpenCreatenew = 0x00000001;

    /// <summary>CRYPTCAT_VERSION_2 — the version that records SHA-256 hashes.</summary>
    internal const uint CryptcatVersion2 = 0x00000200;

    // The attribute flags split into two groups: how the name is given (the low bits) and how the
    // value is given (the 0x000n0000 bits). Mixing them up is rewarded with ERROR_INVALID_PARAMETER.
    internal const uint CryptcatAttrNameascii = 0x00000001;
    internal const uint CryptcatAttrDataascii = 0x00010000;
    internal const uint CryptcatAttrAuthenticated = 0x10000000;

    // ---- crypt32: hashing a file the way a catalog records it ----
    //
    // The subject-interface-package calls live in crypt32, not wintrust, even though everything
    // else about catalogs is in wintrust.

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern bool CryptSIPRetrieveSubjectGuid(string fileName, IntPtr fileHandle, out Guid subject);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool CryptSIPLoad(ref Guid subject, uint flags, ref SipDispatchInfo dispatch);

    // SIP_DISPATCH_INFO from mssip.h. The field order is what makes the function pointers land in
    // the right place, so it follows the header exactly.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SipDispatchInfo
    {
        public uint Size;
        public IntPtr Sip;
        public IntPtr GetSignedDataMsg;
        public IntPtr PutSignedDataMsg;
        public IntPtr CreateIndirectData;
        public IntPtr VerifyIndirectData;
        public IntPtr RemoveSignedDataMsg;
    }

    /// <summary>CRYPT_ALGORITHM_IDENTIFIER, held inline inside SIP_SUBJECTINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptAlgorithmIdentifier
    {
        public IntPtr ObjectId;      // LPSTR
        public uint ParametersSize;
        public IntPtr Parameters;
    }

    // SIP_SUBJECTINFO from mssip.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SipSubjectInfo
    {
        public uint Size;
        public IntPtr SubjectType;     // GUID*
        public IntPtr File;            // HANDLE
        public IntPtr FileName;        // LPCWSTR
        public IntPtr DisplayName;     // LPCWSTR
        public uint Reserved1;
        public uint IntVersion;
        public IntPtr CryptProvider;
        public CryptAlgorithmIdentifier DigestAlgorithm;
        public uint Flags;
        public uint EncodingType;
        public uint Reserved2;
        public uint CapiSettings;
        public uint SecuritySettings;
        public uint Index;
        public uint UnionChoice;
        public IntPtr Union;
        public IntPtr ClientData;
    }

    internal const uint X509AsnEncoding = 0x00000001;
    internal const uint Pkcs7AsnEncoding = 0x00010000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool CryptSipCreateIndirectData(ref SipSubjectInfo subject, ref uint dataSize, IntPtr data);

    // ---- mssign32: signing the catalog ----
    //
    // .NET can build a PKCS#7 signature, but only through a NuGet package, and this project has
    // none. SignerSignEx is the Windows call signtool itself makes, so the signature is produced
    // by the operating system with nothing added to the build.

    [DllImport("mssign32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern int SignerSignEx(uint flags, ref SignerSubjectInfo subjectInfo,
        ref SignerCert signerCert, ref SignerSignatureInfo signatureInfo, IntPtr providerInfo,
        string? timestampUrl, IntPtr request, IntPtr sipData, out IntPtr context);

    [DllImport("mssign32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int SignerFreeSignerContext(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerFileInfo
    {
        public uint Size;
        public IntPtr FileName;
        public IntPtr File;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerSubjectInfo
    {
        public uint Size;
        public IntPtr Index;
        public uint SubjectChoice;
        public IntPtr SubjectInfo;   // SIGNER_FILE_INFO*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerCertStoreInfo
    {
        public uint Size;
        public IntPtr SigningCert;   // PCCERT_CONTEXT
        public uint CertPolicy;
        public IntPtr CertStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerCert
    {
        public uint Size;
        public uint CertChoice;
        public IntPtr CertInfo;      // SIGNER_CERT_STORE_INFO*
        public IntPtr Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerSignatureInfo
    {
        public uint Size;
        public uint AlgidHash;
        public uint AttrChoice;
        public IntPtr AttrAuthcode;
        public IntPtr AuthenticatedAttributes;
        public IntPtr UnauthenticatedAttributes;
    }

    internal const uint SignerSubjectFile = 0x01;
    internal const uint SignerCertStore = 0x02;
    internal const uint SignerNoAttr = 0x00;

    /// <summary>Use the certificate as given rather than rebuilding its chain.</summary>
    internal const uint CertPolicyStore = 0x00000002;

    /// <summary>CALG_SHA_256.</summary>
    internal const uint CalgSha256 = 0x0000800c;
}
