using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Usb;
using static NksUsbShare.Mac.ObjC;

namespace NksUsbShare.Mac;

/// <summary>
/// A native AppKit window, built directly on the Objective-C runtime. No binding library is
/// involved, which keeps the project free of dependencies at the cost of writing the message
/// sends by hand.
///
/// Device lists are laid out with NSGridView so their columns line up, and the identity block
/// stays at the top rather than scrolling away with them.
/// </summary>
public sealed class MacApp
{
    private const long StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4, StyleResizable = 8;
    private const long BackingBuffered = 2;
    private const long ActivationPolicyRegular = 0;
    private const long OrientationVertical = 1;
    private const long OrientationHorizontal = 0;
    private const long AlignLeading = 5;

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
        var frame = new CGRect(0, 0, 880, 620);

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

        _root = VerticalStack(10);
        CallBool(_root, "setTranslatesAutoresizingMaskIntoConstraints:", true);
        SendRect(_root, Sel("setFrame:"), new CGRect(20, 20, frame.Size.Width - 40, frame.Size.Height - 40));

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
        CallLong(stack, "setAlignment:", AlignLeading);
        return stack;
    }

    private static IntPtr HorizontalStack(double spacing)
    {
        var stack = New("NSStackView");
        CallLong(stack, "setOrientation:", OrientationHorizontal);
        CallDouble(stack, "setSpacing:", spacing);
        return stack;
    }

    private static IntPtr Label(string text, bool bold = false, double size = 13, bool dim = false)
    {
        var label = Call(Class("NSTextField"), "labelWithString:", Str(text));
        var font = bold
            ? SendDouble(Class("NSFont"), Sel("boldSystemFontOfSize:"), size)
            : SendDouble(Class("NSFont"), Sel("systemFontOfSize:"), size);
        CallVoid(label, "setFont:", font);

        if (dim) CallVoid(label, "setTextColor:", Call(Class("NSColor"), "secondaryLabelColor"));
        return label;
    }

    private static IntPtr Selectable(string text)
    {
        var label = Label(text);
        CallBool(label, "setSelectable:", true);
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

    /// <summary>A grid whose first row is a dimmed heading, so columns stay aligned.</summary>
    private static IntPtr Table(params string[] headings)
    {
        var grid = SendTwoLongs(Class("NSGridView"), Sel("gridViewWithNumberOfColumns:rows:"), 0, 0);
        CallDouble(grid, "setColumnSpacing:", 18);
        CallDouble(grid, "setRowSpacing:", 8);

        var cells = headings
            .Select(h => h.Length == 0 ? Label("") : Label(h, bold: true, size: 11, dim: true))
            .ToArray();

        Call(grid, "addRowWithViews:", Array(cells));
        return grid;
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

        // The click arrives on the main thread and the work is done there, which keeps AppKit
        // consistent and is acceptable for operations that take milliseconds.
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

        // --- header ---
        var titleRow = HorizontalStack(12);
        Add(titleRow, Label(_node.Settings.HostName, bold: true, size: 17));
        Add(titleRow, Label(_node.Backend.Name, dim: true));
        Add(titleRow, Button("Refresh", () => { Refresh(); SetStatus("Refreshed."); return Task.CompletedTask; }));
        Add(titleRow, Button(_node.Host.IsRunning ? "Stop sharing" : "Start sharing", async () =>
        {
            if (_node.Host.IsRunning)
            {
                await _node.StopHostAsync();
                SetStatus("Stopped. Other computers can no longer reach this one.");
            }
            else
            {
                await _node.StartHostAsync(CancellationToken.None);
                SetStatus($"Listening on {_node.Settings.LinkBindAddress}:{_node.Host.Port}.");
            }
            Refresh();
        }));
        Add(_root, titleRow);

        var details = SendTwoLongs(Class("NSGridView"), Sel("gridViewWithNumberOfColumns:rows:"), 0, 0);
        CallDouble(details, "setColumnSpacing:", 14);
        CallDouble(details, "setRowSpacing:", 3);

        Detail(details, "Pairing token", _node.Settings.PairingToken);
        Detail(details, "Key fingerprint", _node.Identity.Fingerprint);
        Detail(details, "Listening on", $"{_node.Settings.LinkBindAddress}:{_node.Settings.LinkPort}" +
                                        (_node.Host.IsRunning ? "" : "   (stopped)"));
        Detail(details, "Reachable at", string.Join("   ", UsbDisplay.LocalAddresses()));
        Add(_root, details);

        // --- local devices ---
        Add(_root, Section("USB devices on this Mac"));

        if (devices.Count == 0)
        {
            Add(_root, Label("Nothing is plugged in.", dim: true));
        }
        else
        {
            var table = Table("Device", "ID", "Type", "Speed", "");

            foreach (var (device, share) in devices)
            {
                var shared = share is { Enabled: true };
                var id = device.DeviceId;
                var name = device.DisplayName;

                var action = device.ShareBlockedReason is { } reason
                    ? Label("unavailable", dim: true)
                    : Button(shared ? "Stop sharing" : "Share", async () =>
                    {
                        if (shared)
                        {
                            _node.UnshareDevice(id);
                            SetStatus($"{name} is no longer shared.");
                        }
                        else
                        {
                            await _node.ShareDeviceAsync(id, CancellationToken.None);
                            SetStatus($"{name} is now offered to paired computers.");
                        }
                        Refresh();
                    });

                if (device.ShareBlockedReason is { } tooltip) CallVoid(action, "setToolTip:", Str(tooltip));

                Call(table, "addRowWithViews:", Array(
                    Label(name), Label(device.HardwareKey), Label(UsbDisplay.Kind(device)),
                    Label(UsbDisplay.Speed(device.Speed)), action));
            }

            Add(_root, table);
        }

        // --- remote hosts ---
        Add(_root, Section("Other computers"));

        if (_node.Settings.RemoteHosts.Count == 0)
        {
            Add(_root, Label("None paired yet. Pair from a terminal with:  nksusb pair <host> <token>", dim: true));
        }
        else
        {
            var connected = _node.ConnectedHosts.ToHashSet();
            var table = Table("Computer", "Address", "Status", "");

            foreach (var host in _node.Settings.RemoteHosts)
            {
                var live = connected.Contains($"{host.Address}:{host.Port}");
                var remote = host;

                Call(table, "addRowWithViews:", Array(
                    Label(host.Name.Length > 0 ? host.Name : host.Address),
                    Label($"{host.Address}:{host.Port}"),
                    Label(live ? "connected" : "not connected", dim: !live),
                    Button("Show devices", async () =>
                    {
                        var offered = await _node.ListRemoteDevicesAsync(remote, CancellationToken.None);
                        SetStatus(offered.Count == 0
                            ? $"{remote.Address} offers nothing to this Mac."
                            : $"{remote.Address} offers: {string.Join(", ", offered.Select(d => d.DisplayName))}");
                    })));
            }

            Add(_root, table);
        }

        _statusLabel = Label("Ready.", dim: true);
        Add(_root, _statusLabel);
    }

    private static void Detail(IntPtr grid, string name, string value)
        => Call(grid, "addRowWithViews:", Array(Label(name, dim: true), Selectable(value)));

    private static IntPtr Section(string text) => Label(text, bold: true, size: 13);

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
}
