using System.Runtime.InteropServices;

namespace NksUsbShare.Core.Usb.MacOs;

/// <summary>
/// IOKit exposes USB devices through CFPlugIn interfaces: a pointer to a pointer to a table of
/// function pointers, in the style of COM. There is no header to import, so the layout is
/// reproduced here.
///
/// Only the base interfaces are used — <c>kIOUSBDeviceInterfaceID</c> and
/// <c>kIOUSBInterfaceInterfaceID</c> — because their tables have been frozen since the first
/// release and later versions only append. Reaching for a newer version would buy little and
/// risk calling the wrong slot.
/// </summary>
internal static unsafe partial class IoKitInterop
{
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct CFUUIDBytes
    {
        public byte B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15;

        public static CFUUIDBytes From(params byte[] bytes)
        {
            if (bytes.Length != 16) throw new ArgumentException("A UUID is sixteen bytes.", nameof(bytes));
            var value = new CFUUIDBytes();
            var target = MemoryMarshal.CreateSpan(ref value.B0, 16);
            bytes.CopyTo(target);
            return value;
        }
    }

    // Identifiers from IOUSBLib.h. The user client types select which family the plug-in speaks
    // for; the interface ids select which table QueryInterface hands back.
    internal static readonly CFUUIDBytes DeviceUserClientType = CFUUIDBytes.From(
        0x9d, 0xc7, 0xb7, 0x80, 0x9e, 0xc0, 0x11, 0xD4, 0xa5, 0x4f, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61);

    internal static readonly CFUUIDBytes InterfaceUserClientType = CFUUIDBytes.From(
        0x2d, 0x97, 0x86, 0xc6, 0x9e, 0xf3, 0x11, 0xD4, 0xad, 0x51, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61);

    internal static readonly CFUUIDBytes DeviceInterfaceId = CFUUIDBytes.From(
        0x5c, 0x81, 0x87, 0xd0, 0x9e, 0xf3, 0x11, 0xD4, 0x8b, 0x45, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61);

    internal static readonly CFUUIDBytes InterfaceInterfaceId = CFUUIDBytes.From(
        0x73, 0xc9, 0x7a, 0xe8, 0x9e, 0xf3, 0x11, 0xD4, 0xb1, 0xd0, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61);

    internal static readonly CFUUIDBytes PlugInInterfaceId = CFUUIDBytes.From(
        0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4, 0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F);

    [LibraryImport(IoKit, EntryPoint = "IOCreatePlugInInterfaceForService")]
    internal static partial int IOCreatePlugInInterfaceForService(
        uint service, CFUUIDBytes pluginType, CFUUIDBytes interfaceType, out IntPtr plugin, out int score);

    [LibraryImport(CoreFoundation, EntryPoint = "CFUUIDGetConstantUUIDWithBytes")]
    internal static partial IntPtr CFUUIDGetConstantUUIDWithBytes(
        IntPtr allocator,
        byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7,
        byte b8, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14, byte b15);

    [LibraryImport(IoKit, EntryPoint = "IOObjectRelease")]
    internal static partial uint IOObjectRelease(uint obj);

    [LibraryImport(IoKit, EntryPoint = "IOIteratorNext")]
    internal static partial uint IOIteratorNext(uint iterator);

    // ---- vtable access ----

    /// <summary>Reads slot <paramref name="index"/> out of the function table an interface points at.</summary>
    internal static IntPtr Slot(IntPtr interfacePointer, int index)
    {
        var table = *(IntPtr**)interfacePointer;
        return table[index];
    }

    // The first four slots are the IUnknown-style preamble every plug-in interface starts with.
    private const int SlotQueryInterface = 1;
    private const int SlotRelease = 3;

    internal static int QueryInterface(IntPtr plugin, CFUUIDBytes interfaceId, out IntPtr result)
    {
        IntPtr value;
        var function = (delegate* unmanaged[Cdecl]<IntPtr, CFUUIDBytes, IntPtr*, int>)Slot(plugin, SlotQueryInterface);
        var status = function(plugin, interfaceId, &value);
        result = value;
        return status;
    }

    internal static void Release(IntPtr interfacePointer)
    {
        if (interfacePointer == IntPtr.Zero) return;
        var function = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Slot(interfacePointer, SlotRelease);
        function(interfacePointer);
    }

    // ---- IOUSBDeviceInterface slots ----

    internal static class Device
    {
        internal const int Open = 8;
        internal const int Close = 9;
        internal const int GetNumberOfConfigurations = 19;
        internal const int GetConfigurationDescriptorPtr = 21;
        internal const int GetConfiguration = 22;
        internal const int SetConfiguration = 23;
        internal const int ResetDevice = 25;
        internal const int Request = 26;
        internal const int CreateInterfaceIterator = 28;
    }

    // ---- IOUSBInterfaceInterface slots ----

    internal static class Interface
    {
        internal const int Open = 8;
        internal const int Close = 9;
        internal const int GetInterfaceNumber = 17;
        internal const int GetNumEndpoints = 19;
        internal const int SetAlternateInterface = 22;
        internal const int ControlRequest = 24;
        internal const int GetPipeProperties = 26;
        internal const int AbortPipe = 28;
        internal const int ResetPipe = 29;
        internal const int ClearPipeStall = 30;
        internal const int ReadPipe = 31;
        internal const int WritePipe = 32;
    }

    /// <summary>
    /// IOUSBDevRequest, as the device interface's Request slot expects it. The layout is fixed
    /// by IOUSBLib and the trailing pointer is naturally aligned.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceRequest
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
        public IntPtr Data;
        public uint CompletedLength;
    }

    /// <summary>Describes which interfaces to hand back from CreateInterfaceIterator; 0xFFFF means any.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceRequest
    {
        public ushort InterfaceClass;
        public ushort InterfaceSubClass;
        public ushort InterfaceProtocol;
        public ushort AlternateSetting;

        public static InterfaceRequest Any => new()
        {
            InterfaceClass = 0xFFFF,
            InterfaceSubClass = 0xFFFF,
            InterfaceProtocol = 0xFFFF,
            AlternateSetting = 0xFFFF,
        };
    }

    // ---- typed callers ----

    internal static int CallOpen(IntPtr target, int slot)
    {
        var function = (delegate* unmanaged[Cdecl]<IntPtr, int>)Slot(target, slot);
        return function(target);
    }

    internal static int CallWithByte(IntPtr target, int slot, byte value)
    {
        var function = (delegate* unmanaged[Cdecl]<IntPtr, byte, int>)Slot(target, slot);
        return function(target, value);
    }

    internal static int CallDeviceRequest(IntPtr device, ref DeviceRequest request)
    {
        fixed (DeviceRequest* pointer = &request)
        {
            var function = (delegate* unmanaged[Cdecl]<IntPtr, DeviceRequest*, int>)Slot(device, Device.Request);
            return function(device, pointer);
        }
    }

    internal static int CallCreateInterfaceIterator(IntPtr device, ref InterfaceRequest request, out uint iterator)
    {
        uint result;
        fixed (InterfaceRequest* pointer = &request)
        {
            var function = (delegate* unmanaged[Cdecl]<IntPtr, InterfaceRequest*, uint*, int>)
                Slot(device, Device.CreateInterfaceIterator);
            var status = function(device, pointer, &result);
            iterator = result;
            return status;
        }
    }

    internal static int CallSetAlternateInterface(IntPtr usbInterface, byte alternateSetting)
        => CallWithByte(usbInterface, Interface.SetAlternateInterface, alternateSetting);

    internal static int CallGetNumEndpoints(IntPtr usbInterface, out byte count)
    {
        byte value;
        var function = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Slot(usbInterface, Interface.GetNumEndpoints);
        var status = function(usbInterface, &value);
        count = value;
        return status;
    }

    internal static int CallGetInterfaceNumber(IntPtr usbInterface, out byte number)
    {
        byte value;
        var function = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Slot(usbInterface, Interface.GetInterfaceNumber);
        var status = function(usbInterface, &value);
        number = value;
        return status;
    }

    /// <summary>
    /// GetPipeProperties(pipeRef, &amp;direction, &amp;number, &amp;transferType, &amp;maxPacketSize, &amp;interval).
    /// Pipe references start at one; zero is the default control pipe.
    /// </summary>
    internal static int CallGetPipeProperties(
        IntPtr usbInterface, byte pipeRef,
        out byte direction, out byte number, out byte transferType, out ushort maxPacketSize, out byte interval)
    {
        byte d, n, t, i;
        ushort m;

        var function = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, byte*, byte*, ushort*, byte*, int>)
            Slot(usbInterface, Interface.GetPipeProperties);
        var status = function(usbInterface, pipeRef, &d, &n, &t, &m, &i);

        direction = d;
        number = n;
        transferType = t;
        maxPacketSize = m;
        interval = i;
        return status;
    }

    internal static int CallReadPipe(IntPtr usbInterface, byte pipeRef, byte[] buffer, ref uint size)
    {
        fixed (byte* data = buffer)
        fixed (uint* length = &size)
        {
            var function = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, uint*, int>)
                Slot(usbInterface, Interface.ReadPipe);
            return function(usbInterface, pipeRef, data, length);
        }
    }

    internal static int CallWritePipe(IntPtr usbInterface, byte pipeRef, byte[] buffer, uint size)
    {
        fixed (byte* data = buffer)
        {
            var function = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, uint, int>)
                Slot(usbInterface, Interface.WritePipe);
            return function(usbInterface, pipeRef, data, size);
        }
    }

    internal static int CallPipeOperation(IntPtr usbInterface, int slot, byte pipeRef)
        => CallWithByte(usbInterface, slot, pipeRef);

    /// <summary>Turns an IOKit return code into something a person can read.</summary>
    internal static string Describe(int code) => (uint)code switch
    {
        0 => "success",
        0xE00002C0 => "device or interface is not open",
        0xE00002C1 => "no device",
        0xE00002C2 => "not permitted",
        0xE00002BD => "exclusive access is held by another process",
        0xE00002C7 => "timed out",
        0xE00002ED => "pipe stalled",
        0xE00002EB => "transfer was aborted",
        0xE00002BE => "bad argument",
        0xE00002D9 => "not open",
        _ => $"IOKit error 0x{code:X8}",
    };
}
