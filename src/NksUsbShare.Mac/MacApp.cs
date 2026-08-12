using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Usb;
using static NksUsbShare.Mac.ObjC;

namespace NksUsbShare.Mac;

/// <summary>
/// A native AppKit window, built directly on the Objective-C runtime. No binding library is
/// involved, which keeps the project free of dependencies at the cost of writing the message
/// sends by hand.
/// </summary>
public sealed class MacApp
{
    private const long StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4, StyleResizable = 8;
    private const long BackingBuffered = 2;
    private const long ActivationPolicyRegular = 0;
    private const long OrientationVertical = 1;
    private const long OrientationHorizontal = 0;

    private readonly NodeService _node;
    private IntPtr _application;
    private IntPtr _window;
    private IntPtr _root;
    private IntPtr _statusLabel;

    /// <summary>Button tag to the action it runs. Tags travel through Objective-C as plain integers.</summary>
    private static readonly Dictionary<long, Func<Task>> Actions = [];
    private static long _nextTag = 1;
    private static IntPtr _targetClass;
    private static IntPtr _target;

    public MacApp(NodeService node) => _node = node;

    public void Run()
    {
        var applicationClass = Class("NSApplication");
        if (IsNull(applicationClass))
            throw new InvalidOperationException("AppKit is not loaded, so no window can be created.");

        _application = Call(applicationClass, "sharedApplication");
        if (IsNull(_application))
            throw new InvalidOperationException(
                "NSApplication could not be created. A window needs a graphical login session.");

        CallLong(_application, "setActivationPolicy:", ActivationPolicyRegular);
        BuildMenuBar();

        CreateTargetClass();
        BuildWindow();

        Refresh();

        CallBool(_application, "activateIgnoringOtherApps:", true);
        CallVoid(_application, "run");
    }

    /// <summary>
    /// A regular application is expected to own a menu bar. Without one the Dock icon appears but
    /// the application never becomes properly active.
    /// </summary>
    private void BuildMenuBar()
    {
        var menuBar = New("NSMenu");
        var appMenuItem = New("NSMenuItem");
        CallVoid(menuBar, "addItem:", appMenuItem);
        CallVoid(_application, "setMainMenu:", menuBar);

        var appMenu = New("NSMenu");
        var quitItem = Send(Send(Class("NSMenuItem"), Sel("alloc")),
            Sel("initWithTitle:action:keyEquivalent:"),
            Str("Quit NKS UsbShare"), Sel("terminate:"), Str("q"));

        CallVoid(appMenu, "addItem:", quitItem);
        CallVoid(appMenuItem, "setSubmenu:", appMenu);
    }

    private void BuildWindow()
    {
        var style = StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable;
        var frame = new CGRect(0, 0, 780, 560);

        _window = SendWindowInit(
            Send(Class("NSWindow"), Sel("alloc")),
            Sel("initWithContentRect:styleMask:backing:defer:"),
            frame, (ulong)style, (ulong)BackingBuffered, false);

        CallVoid(_window, "setTitle:", Str("NKS UsbShare"));
        CallVoid(_window, "center");

        // A stack view laid out by constraints collapses to nothing when it is the content view
        // and nobody supplies any. Putting it inside the window's own content view and letting the
        // old autoresizing path size it keeps the layout simple and predictable.
        var content = Call(_window, "contentView");

        _root = VerticalStack(8);
        CallBool(_root, "setTranslatesAutoresizingMaskIntoConstraints:", true);
        SendRect(_root, Sel("setFrame:"), new CGRect(16, 16, frame.Size.Width - 32, frame.Size.Height - 32));

        // NSViewWidthSizable | NSViewHeightSizable
        CallLong(_root, "setAutoresizingMask:", 2 | 16);
        CallVoid(content, "addSubview:", _root);

        CallVoid(_window, "makeKeyAndOrderFront:", IntPtr.Zero);
    }

    private static IntPtr VerticalStack(double spacing)
    {
        var stack = New("NSStackView");
        CallLong(stack, "setOrientation:", OrientationVertical);
        CallDouble(stack, "setSpacing:", spacing);
        // NSLayoutAttributeLeading keeps rows flush to the left edge.
        CallLong(stack, "setAlignment:", 5);
        return stack;
    }

    private static IntPtr HorizontalStack(double spacing)
    {
        var stack = New("NSStackView");
        CallLong(stack, "setOrientation:", OrientationHorizontal);
        CallDouble(stack, "setSpacing:", spacing);
        return stack;
    }

    private static IntPtr Label(string text, bool bold = false, double size = 13)
    {
        var label = Call(Class("NSTextField"), "labelWithString:", Str(text));
        var font = bold
            ? SendDouble(Class("NSFont"), Sel("boldSystemFontOfSize:"), size)
            : SendDouble(Class("NSFont"), Sel("systemFontOfSize:"), size);
        CallVoid(label, "setFont:", font);
        return label;
    }

    private static IntPtr Button(string title, Func<Task> action)
    {
        var tag = _nextTag++;
        Actions[tag] = action;

        var button = Send(Class("NSButton"), Sel("buttonWithTitle:target:action:"),
            Str(title), _target, Sel("act:"));

        CallLong(button, "setTag:", tag);
        return button;
    }

    /// <summary>
    /// AppKit delivers button clicks to an Objective-C object, so one is created at runtime whose
    /// single method hands control back to managed code.
    /// </summary>
    private static unsafe void CreateTargetClass()
    {
        if (_target != IntPtr.Zero) return;

        _targetClass = objc_allocateClassPair(Class("NSObject"), "NksUsbShareTarget", 0);
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback = &OnButtonClicked;

        // "v@:@" is void, taking self, the selector, and one object argument.
        class_addMethod(_targetClass, Sel("act:"), (IntPtr)callback, "v@:@");
        objc_registerClassPair(_targetClass);

        _target = Call(Send(_targetClass, Sel("alloc")), "init");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnButtonClicked(IntPtr self, IntPtr selector, IntPtr sender)
    {
        var tag = SendForLong(sender, Sel("tag"));
        if (!Actions.TryGetValue(tag, out var action)) return;

        // The click arrives on the main thread; the work is awaited there so AppKit stays
        // consistent, which is acceptable for operations that take milliseconds.
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
        }
    }

    // ---- content ----

    /// <summary>
    /// Rebuilds the window contents. Deliberately synchronous: AppKit views may only be touched
    /// from the main thread, and an await here would move the rest of the work off it.
    /// </summary>
    private void Refresh()
    {
        var devices = _node.ListLocalDevicesWithSharesAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Rebuilding the whole tree is cheap at this size and avoids tracking view identity.
        foreach (var view in Children(_root)) CallVoid(view, "removeFromSuperview");

        Add(_root, Label($"{_node.Settings.HostName}  ·  {_node.Backend.Name}", bold: true, size: 16));
        Add(_root, Label($"Pairing token   {_node.Settings.PairingToken}"));
        Add(_root, Label($"Key fingerprint {_node.Identity.Fingerprint}"));
        Add(_root, Label($"Listening on    {_node.Settings.LinkBindAddress}:{_node.Settings.LinkPort}" +
                         (_node.Host.IsRunning ? "" : "  (stopped)")));
        Add(_root, Label($"Reachable at    {string.Join("   ", UsbDisplay.LocalAddresses())}"));

        Add(_root, Label(""));
        Add(_root, Label("USB devices on this Mac", bold: true));

        if (devices.Count == 0)
        {
            Add(_root, Label("   Nothing is plugged in."));
        }
        else
        {
            foreach (var (device, share) in devices)
            {
                var row = HorizontalStack(12);
                var shared = share is { Enabled: true };

                Add(row, Label($"{device.HardwareKey}   {Pad(device.DisplayName, 30)} " +
                               $"{Pad(UsbDisplay.Kind(device), 14)} {UsbDisplay.Speed(device.Speed)}"));

                if (device.ShareBlockedReason is { } reason)
                {
                    Add(row, Label(reason));
                }
                else
                {
                    var id = device.DeviceId;
                    Add(row, Button(shared ? "Stop sharing" : "Share", async () =>
                    {
                        if (shared) _node.UnshareDevice(id);
                        else await _node.ShareDeviceAsync(id, CancellationToken.None);
                        Refresh();
                        SetStatus(shared ? "Stopped sharing." : "Shared.");
                    }));
                }

                Add(_root, row);
            }
        }

        Add(_root, Label(""));
        Add(_root, Label("Other computers", bold: true));

        if (_node.Settings.RemoteHosts.Count == 0)
        {
            Add(_root, Label("   None paired yet. Use the command line to pair: nksusb pair <host> <token>"));
        }
        else
        {
            var connected = _node.ConnectedHosts.ToHashSet();
            foreach (var host in _node.Settings.RemoteHosts)
            {
                var row = HorizontalStack(12);
                var live = connected.Contains($"{host.Address}:{host.Port}");
                Add(row, Label($"{Pad(host.Name.Length > 0 ? host.Name : host.Address, 24)} " +
                               $"{Pad($"{host.Address}:{host.Port}", 24)} {(live ? "connected" : "not connected")}"));

                var remote = host;
                Add(row, Button("Show devices", async () =>
                {
                    var offered = await _node.ListRemoteDevicesAsync(remote, CancellationToken.None);
                    SetStatus(offered.Count == 0
                        ? $"{remote.Address} offers nothing to this Mac."
                        : $"{remote.Address} offers: {string.Join(", ", offered.Select(d => d.DisplayName))}");
                }));

                Add(_root, row);
            }
        }

        Add(_root, Label(""));

        var controls = HorizontalStack(10);
        Add(controls, Button("Refresh", async () => { Refresh(); SetStatus("Refreshed."); }));
        Add(controls, Button(_node.Host.IsRunning ? "Stop sharing" : "Start sharing", async () =>
        {
            if (_node.Host.IsRunning) await _node.StopHostAsync();
            else await _node.StartHostAsync(CancellationToken.None);
            Refresh();
        }));
        Add(_root, controls);

        _statusLabel = Label("Ready.");
        Add(_root, _statusLabel);
    }

    private void SetStatus(string text)
    {
        if (_statusLabel != IntPtr.Zero) CallVoid(_statusLabel, "setStringValue:", Str(text));
    }

    private static void Add(IntPtr stack, IntPtr view) => CallVoid(stack, "addArrangedSubview:", view);

    private static List<IntPtr> Children(IntPtr view)
    {
        var result = new List<IntPtr>();
        var subviews = Call(view, "subviews");
        var count = SendForLong(subviews, Sel("count"));
        for (long i = 0; i < count; i++)
            result.Add(SendLong(subviews, Sel("objectAtIndex:"), i));
        return result;
    }

    private static string Pad(string value, int width)
        => value.Length >= width ? value[..width] : value.PadRight(width);
}
