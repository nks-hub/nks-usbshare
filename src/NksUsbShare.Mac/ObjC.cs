using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NksUsbShare.Mac;

[StructLayout(LayoutKind.Sequential)]
public struct CGPoint
{
    public double X, Y;
    public CGPoint(double x, double y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
public struct CGSize
{
    public double Width, Height;
    public CGSize(double width, double height) { Width = width; Height = height; }
}

[StructLayout(LayoutKind.Sequential)]
public struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;
    public CGRect(double x, double y, double width, double height)
    {
        Origin = new CGPoint(x, y);
        Size = new CGSize(width, height);
    }
}

/// <summary>
/// The Objective-C runtime, which is how a .NET process talks to AppKit. There is no binding
/// library involved: classes are looked up by name, selectors are interned, and messages are sent
/// through objc_msgSend, which has to be declared once per argument shape.
/// </summary>
public static partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";
    private const string Dl = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(Dl, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DlOpen(string path, int mode);

    /// <summary>
    /// The Objective-C runtime only knows classes from images the process has loaded, and .NET
    /// links neither Foundation nor AppKit. Without this, every class lookup returns nil and
    /// messages to nil quietly do nothing — which looks exactly like a window that never appears.
    /// </summary>
    static ObjC()
    {
        const int rtldNow = 2;
        foreach (var framework in (string[])
                 [
                     "/System/Library/Frameworks/Foundation.framework/Foundation",
                     "/System/Library/Frameworks/AppKit.framework/AppKit",
                 ])
        {
            if (DlOpen(framework, rtldNow) == IntPtr.Zero)
                throw new InvalidOperationException($"Could not load {framework}.");
        }
    }

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getClass(string name);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr sel_registerName(string name);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(Runtime)]
    public static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    // One declaration per argument shape. The names differ only so the compiler can tell them
    // apart; every one binds to objc_msgSend.

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendLong(IntPtr receiver, IntPtr selector, long a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendDouble(IntPtr receiver, IntPtr selector, double a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendWithBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial long SendForLong(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendRect(IntPtr receiver, IntPtr selector, CGRect frame);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendWindowInit(
        IntPtr receiver, IntPtr selector, CGRect frame, ulong style, ulong backing,
        [MarshalAs(UnmanagedType.U1)] bool defer);

    // ---- conveniences ----

    private static readonly Dictionary<string, IntPtr> SelectorCache = [];

    public static IntPtr Sel(string name)
    {
        if (SelectorCache.TryGetValue(name, out var cached)) return cached;
        var selector = sel_registerName(name);
        SelectorCache[name] = selector;
        return selector;
    }

    public static IntPtr Class(string name) => objc_getClass(name);

    /// <summary>Allocates and initialises an instance of a class by name.</summary>
    public static IntPtr New(string className)
        => Send(Send(Class(className), Sel("alloc")), Sel("init"));

    public static IntPtr Call(IntPtr receiver, string selector) => Send(receiver, Sel(selector));
    public static IntPtr Call(IntPtr receiver, string selector, IntPtr a) => Send(receiver, Sel(selector), a);
    public static IntPtr Call(IntPtr receiver, string selector, IntPtr a, IntPtr b) => Send(receiver, Sel(selector), a, b);

    public static void CallVoid(IntPtr receiver, string selector) => Send(receiver, Sel(selector));
    public static void CallVoid(IntPtr receiver, string selector, IntPtr a) => Send(receiver, Sel(selector), a);
    public static void CallBool(IntPtr receiver, string selector, bool value) => SendWithBool(receiver, Sel(selector), value);
    public static void CallLong(IntPtr receiver, string selector, long value) => SendLong(receiver, Sel(selector), value);
    public static void CallDouble(IntPtr receiver, string selector, double value) => SendDouble(receiver, Sel(selector), value);

    /// <summary>Wraps a managed string as an autoreleased NSString.</summary>
    public static IntPtr Str(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, bytes.Length, 0);

            // stringWithUTF8String: copies, so the buffer can go straight back.
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string FromStr(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return "";
        var utf8 = Call(nsString, "UTF8String");
        return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNull(IntPtr pointer) => pointer == IntPtr.Zero;
}
