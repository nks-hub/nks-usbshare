using System.Runtime.InteropServices;

namespace NksUsbShare.Core.Usb.Linux;

/// <summary>
/// libc entry points and the usbfs ioctl numbers, computed the same way the kernel's
/// _IOC macros compute them so the values stay correct on both 32 and 64 bit userlands.
/// </summary>
internal static partial class LinuxNative
{
    private const uint IocNone = 0, IocWrite = 1, IocRead = 2;

    private static uint Ioc(uint dir, uint type, uint nr, uint size)
        => (dir << 30) | (size << 16) | (type << 8) | nr;

    private const uint UsbType = (uint)'U';

    // Pointer width decides the size of every usbfs struct that embeds a pointer.
    private static uint PointerSize => (uint)IntPtr.Size;
    private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>struct usbdevfs_ctrltransfer: 8 bytes of setup, u32 timeout, then a pointer.</summary>
    private static uint CtrlTransferSize => Align(12, PointerSize) + PointerSize;

    /// <summary>struct usbdevfs_bulktransfer: three u32 then a pointer.</summary>
    private static uint BulkTransferSize => Align(12, PointerSize) + PointerSize;

    /// <summary>struct usbdevfs_urb, without the trailing iso descriptor array.</summary>
    internal static int UrbSize => IntPtr.Size == 8 ? 56 : 44;

    internal static int UrbBufferOffset => IntPtr.Size == 8 ? 16 : 12;
    internal static int UrbBufferLengthOffset => IntPtr.Size == 8 ? 24 : 16;
    internal static int UrbActualLengthOffset => IntPtr.Size == 8 ? 28 : 20;
    internal static int UrbStartFrameOffset => IntPtr.Size == 8 ? 32 : 24;
    internal static int UrbNumberOfPacketsOffset => IntPtr.Size == 8 ? 36 : 28;
    internal static int UrbErrorCountOffset => IntPtr.Size == 8 ? 40 : 32;
    internal static int UrbSignrOffset => IntPtr.Size == 8 ? 44 : 36;
    internal static int UrbUserContextOffset => IntPtr.Size == 8 ? 48 : 40;
    internal const int UrbStatusOffset = 4;
    internal const int UrbFlagsOffset = 8;
    internal const int IsoPacketDescSize = 12;

    internal static uint UsbdevfsControl => Ioc(IocRead | IocWrite, UsbType, 0, CtrlTransferSize);
    internal static uint UsbdevfsBulk => Ioc(IocRead | IocWrite, UsbType, 2, BulkTransferSize);
    internal static uint UsbdevfsResetEp => Ioc(IocRead, UsbType, 3, 4);
    internal static uint UsbdevfsSetInterface => Ioc(IocRead, UsbType, 4, 8);
    internal static uint UsbdevfsSetConfiguration => Ioc(IocRead, UsbType, 5, 4);
    internal static uint UsbdevfsGetDriver => Ioc(IocWrite, UsbType, 8, 4 + 256);
    internal static uint UsbdevfsSubmitUrb => Ioc(IocRead, UsbType, 10, (uint)UrbSize);
    internal static uint UsbdevfsDiscardUrb => Ioc(IocNone, UsbType, 11, 0);
    internal static uint UsbdevfsReapUrb => Ioc(IocWrite, UsbType, 12, PointerSize);
    internal static uint UsbdevfsReapUrbNdelay => Ioc(IocWrite, UsbType, 13, PointerSize);
    internal static uint UsbdevfsClaimInterface => Ioc(IocRead, UsbType, 15, 4);
    internal static uint UsbdevfsReleaseInterface => Ioc(IocRead, UsbType, 16, 4);
    internal static uint UsbdevfsIoctl => Ioc(IocRead | IocWrite, UsbType, 18, 2 * 4 + PointerSize);
    internal static uint UsbdevfsReset => Ioc(IocNone, UsbType, 20, 0);
    internal static uint UsbdevfsClearHalt => Ioc(IocRead, UsbType, 21, 4);

    /// <summary>Passed inside USBDEVFS_IOCTL to make the kernel driver let go of an interface.</summary>
    internal static uint UsbdevfsDisconnect => Ioc(IocNone, UsbType, 22, 0);
    internal static uint UsbdevfsConnect => Ioc(IocNone, UsbType, 23, 0);

    internal const byte UrbTypeIsochronous = 0;
    internal const byte UrbTypeInterrupt = 1;
    internal const byte UrbTypeControl = 2;
    internal const byte UrbTypeBulk = 3;

    internal const int OpenReadWrite = 2;
    internal const int OpenCloExec = 0x80000;

    internal const short PollOut = 0x0004;
    internal const short PollErr = 0x0008;
    internal const short PollHup = 0x0010;

    // errno values that matter for mapping a URB status onto UrbStatus.
    internal const int Enoent = 2;
    internal const int Eintr = 4;
    internal const int Enodev = 19;
    internal const int Einval = 22;
    internal const int Epipe = 32;
    internal const int Eagain = 11;
    internal const int Ebusy = 16;
    internal const int Eoverflow = 75;
    internal const int Econnreset = 104;
    internal const int Eshutdown = 108;
    internal const int Etimedout = 110;
    internal const int Eremoteio = 121;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, IntPtr argument);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, uint argument);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int Poll(ref PollFd fds, nuint count, int timeoutMs);

    internal static string Strerror(int errno) => new System.ComponentModel.Win32Exception(errno).Message;
}
