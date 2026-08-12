using System.Runtime.InteropServices;

namespace NksUsbShare.Core.Usb.MacOs;

/// <summary>
/// IOKit and CoreFoundation entry points. Both frameworks ship with macOS, so nothing
/// outside the OS is required.
/// </summary>
internal static partial class MacNative
{
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal const uint KernSuccess = 0;
    internal const int CfStringEncodingUtf8 = 0x08000100;

    [LibraryImport(IoKit, EntryPoint = "IOServiceMatching", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IoKit, EntryPoint = "IOServiceGetMatchingServices")]
    internal static partial uint IOServiceGetMatchingServices(uint masterPort, IntPtr matching, out uint iterator);

    [LibraryImport(IoKit, EntryPoint = "IOIteratorNext")]
    internal static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IoKit, EntryPoint = "IOObjectRelease")]
    internal static partial uint IOObjectRelease(uint obj);

    [LibraryImport(IoKit, EntryPoint = "IORegistryEntryCreateCFProperties")]
    internal static partial uint IORegistryEntryCreateCFProperties(uint entry, out IntPtr properties, IntPtr allocator, uint options);

    [LibraryImport(IoKit, EntryPoint = "IORegistryEntryGetRegistryEntryID")]
    internal static partial uint IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    internal static partial void CFRelease(IntPtr obj);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, int encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetValue")]
    internal static partial IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [LibraryImport(CoreFoundation, EntryPoint = "CFGetTypeID")]
    internal static partial nuint CFGetTypeID(IntPtr obj);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetTypeID")]
    internal static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetTypeID")]
    internal static partial nuint CFStringGetTypeID();

    /// <summary>kCFNumberSInt64Type = 4.</summary>
    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFNumberGetValue(IntPtr number, nint theType, out long value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFStringGetCString(IntPtr theString, [Out] byte[] buffer, nint bufferSize, int encoding);

    private const nint CfNumberSInt64Type = 4;

    /// <summary>Reads a numeric IORegistry property, or null when it is absent or a different type.</summary>
    internal static long? GetNumber(IntPtr properties, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, CfStringEncodingUtf8);
        try
        {
            var value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFNumberGetTypeID()) return null;
            return CFNumberGetValue(value, CfNumberSInt64Type, out var result) ? result : null;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>Reads a string IORegistry property, or an empty string when it is absent.</summary>
    internal static string GetString(IntPtr properties, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, CfStringEncodingUtf8);
        try
        {
            var value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return "";

            var buffer = new byte[512];
            if (!CFStringGetCString(value, buffer, buffer.Length, CfStringEncodingUtf8)) return "";

            var end = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }
}
