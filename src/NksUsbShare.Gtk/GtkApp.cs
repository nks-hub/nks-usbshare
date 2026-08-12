using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Gtk;

/// <summary>
/// A GTK window over the same node the command line drives. GTK ships with every Linux desktop,
/// so the application still carries nothing of its own.
///
/// The header stays pinned while the device tables scroll, and the tables are grids so their
/// columns line up instead of drifting with the text.
/// </summary>
public sealed class GtkApp
{
    private const int AlignFill = 0, AlignStart = 1, AlignCenter = 3;
    private const int EllipsizeEnd = 3;

    private readonly NodeService _node;
    private IntPtr _window;
    private IntPtr _content;
    private IntPtr _status;
    private IntPtr _headerState;

    /// <summary>Widget pointer to the action it runs, looked up when a click arrives.</summary>
    private static readonly Dictionary<IntPtr, Action> Actions = [];
    private static GtkApp? _instance;

    public GtkApp(NodeService node)
    {
        _node = node;
        _instance = this;
    }

    public bool Run()
    {
        var argc = 0;
        var argv = IntPtr.Zero;
        if (!Gtk.InitCheck(ref argc, ref argv))
        {
            Console.Error.WriteLine(
                "No graphical display is available, so the window cannot open. Use nksusb from the terminal instead.");
            return false;
        }

        BuildWindow();
        Refresh();
        Gtk.WidgetShowAll(_window);
        Gtk.MainLoop();
        return true;
    }

    private unsafe void BuildWindow()
    {
        _window = Gtk.WindowNew(Gtk.WindowToplevel);
        Gtk.WindowSetTitle(_window, "NKS UsbShare");
        Gtk.WindowSetDefaultSize(_window, 900, 620);
        Gtk.WindowSetPosition(_window, 1);

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> onDestroy = &OnWindowDestroyed;
        Gtk.SignalConnect(_window, "destroy", (IntPtr)onDestroy, IntPtr.Zero, IntPtr.Zero, 0);

        var outer = Gtk.BoxNew(Gtk.OrientationVertical, 0);
        Gtk.ContainerAdd(_window, outer);

        // Identity and connection details stay put; only the device tables scroll.
        var header = BuildHeader();
        Gtk.BoxPackStart(outer, header, expand: false, fill: false, 0);
        Gtk.BoxPackStart(outer, Gtk.SeparatorNew(Gtk.OrientationHorizontal), expand: false, fill: false, 0);

        var scroller = Gtk.ScrolledWindowNew(IntPtr.Zero, IntPtr.Zero);
        Gtk.ScrolledWindowSetPolicy(scroller, Gtk.PolicyAutomatic, Gtk.PolicyAutomatic);
        Gtk.BoxPackStart(outer, scroller, expand: true, fill: true, 0);

        _content = Gtk.BoxNew(Gtk.OrientationVertical, 0);
        Gtk.ContainerSetBorderWidth(_content, 16);
        Gtk.ContainerAdd(scroller, _content);

        Gtk.BoxPackStart(outer, Gtk.SeparatorNew(Gtk.OrientationHorizontal), expand: false, fill: false, 0);

        var statusRow = Gtk.BoxNew(Gtk.OrientationHorizontal, 0);
        Gtk.ContainerSetBorderWidth(statusRow, 8);
        _status = Label("Ready.", dim: true);
        Gtk.BoxPackStart(statusRow, _status, expand: true, fill: true, 0);
        Gtk.BoxPackStart(outer, statusRow, expand: false, fill: false, 0);
    }

    private IntPtr BuildHeader()
    {
        var header = Gtk.BoxNew(Gtk.OrientationVertical, 4);
        Gtk.ContainerSetBorderWidth(header, 16);

        var title = Gtk.BoxNew(Gtk.OrientationHorizontal, 10);
        Gtk.BoxPackStart(title, Markup($"<big><b>{Gtk.Escape(_node.Settings.HostName)}</b></big>"),
            expand: false, fill: false, 0);
        Gtk.BoxPackStart(title, Label(_node.Backend.Name, dim: true), expand: true, fill: true, 0);

        AddButton(title, "Refresh", () => { Refresh(); SetStatus("Refreshed."); });
        AddButton(title, _node.Host.IsRunning ? "Stop sharing" : "Start sharing", ToggleSharing);
        Gtk.BoxPackStart(header, title, expand: false, fill: false, 0);

        var details = Gtk.GridNew();
        Gtk.GridSetColumnSpacing(details, 14);
        Gtk.GridSetRowSpacing(details, 2);
        Gtk.WidgetSetMarginTop(details, 8);

        var row = 0;
        Detail(details, ref row, "Pairing token", _node.Settings.PairingToken, selectable: true);
        Detail(details, ref row, "Key fingerprint", _node.Identity.Fingerprint, selectable: true);

        _headerState = Label(StateText(), dim: true);
        Gtk.GridAttach(details, Label("Listening on", dim: true), 0, row, 1, 1);
        Gtk.GridAttach(details, _headerState, 1, row, 1, 1);
        row++;

        Detail(details, ref row, "Reachable at", string.Join("   ", UsbDisplay.LocalAddresses()), selectable: true);
        Gtk.BoxPackStart(header, details, expand: false, fill: false, 0);

        return header;
    }

    private string StateText() =>
        $"{_node.Settings.LinkBindAddress}:{_node.Settings.LinkPort}" + (_node.Host.IsRunning ? "" : "   (stopped)");

    private static void Detail(IntPtr grid, ref int row, string name, string value, bool selectable = false)
    {
        Gtk.GridAttach(grid, Label(name, dim: true), 0, row, 1, 1);

        var valueLabel = Label(value);
        if (selectable) Gtk.LabelSetSelectable(valueLabel, true);
        Gtk.GridAttach(grid, valueLabel, 1, row, 1, 1);
        row++;
    }

    // ---- content ----

    private void Refresh()
    {
        var devices = _node.ListLocalDevicesWithSharesAsync(CancellationToken.None).GetAwaiter().GetResult();

        foreach (var child in Gtk.Children(_content)) Gtk.ContainerRemove(_content, child);
        Actions.Clear();

        Gtk.LabelSetText(_headerState, StateText());

        Gtk.BoxPackStart(_content, SectionTitle("USB devices on this computer"), expand: false, fill: false, 0);

        if (devices.Count == 0)
        {
            Gtk.BoxPackStart(_content, Label("Nothing is plugged in.", dim: true), expand: false, fill: false, 0);
        }
        else
        {
            var table = Table("Device", "ID", "Type", "Speed", "");
            var row = 1;

            foreach (var (device, share) in devices)
            {
                var shared = share is { Enabled: true };

                Cell(table, 0, row, device.DisplayName, wide: true);
                Cell(table, 1, row, device.HardwareKey);
                Cell(table, 2, row, UsbDisplay.Kind(device));
                Cell(table, 3, row, UsbDisplay.Speed(device.Speed));

                if (device.ShareBlockedReason is { } reason)
                {
                    var blocked = Label("unavailable", dim: true);
                    Gtk.WidgetSetTooltip(blocked, reason);
                    Gtk.GridAttach(table, blocked, 4, row, 1, 1);
                }
                else
                {
                    var id = device.DeviceId;
                    var name = device.DisplayName;
                    var button = MakeButton(shared ? "Stop sharing" : "Share", () =>
                    {
                        if (shared)
                        {
                            _node.UnshareDevice(id);
                            SetStatus($"{name} is no longer shared.");
                        }
                        else
                        {
                            _node.ShareDeviceAsync(id, CancellationToken.None).GetAwaiter().GetResult();
                            SetStatus($"{name} is now offered to paired computers.");
                        }
                        Refresh();
                    });

                    Gtk.WidgetSetValign(button, AlignCenter);
                    Gtk.GridAttach(table, button, 4, row, 1, 1);
                }

                row++;
            }

            Gtk.BoxPackStart(_content, table, expand: false, fill: false, 0);
        }

        Gtk.BoxPackStart(_content, SectionTitle("Other computers"), expand: false, fill: false, 0);

        if (_node.Settings.RemoteHosts.Count == 0)
        {
            Gtk.BoxPackStart(_content,
                Label("None paired yet. Pair from a terminal with:  nksusb pair <host> <token>", dim: true),
                expand: false, fill: false, 0);
        }
        else
        {
            var connected = _node.ConnectedHosts.ToHashSet();
            var table = Table("Computer", "Address", "Status", "");
            var row = 1;

            foreach (var host in _node.Settings.RemoteHosts)
            {
                var live = connected.Contains($"{host.Address}:{host.Port}");

                Cell(table, 0, row, host.Name.Length > 0 ? host.Name : host.Address, wide: true);
                Cell(table, 1, row, $"{host.Address}:{host.Port}");
                Cell(table, 2, row, live ? "connected" : "not connected");

                var remote = host;
                var button = MakeButton("Show devices", () =>
                {
                    var offered = _node.ListRemoteDevicesAsync(remote, CancellationToken.None).GetAwaiter().GetResult();
                    SetStatus(offered.Count == 0
                        ? $"{remote.Address} offers nothing to this computer."
                        : $"{remote.Address} offers: {string.Join(", ", offered.Select(d => d.DisplayName))}");
                });

                Gtk.WidgetSetValign(button, AlignCenter);
                Gtk.GridAttach(table, button, 3, row, 1, 1);
                row++;
            }

            Gtk.BoxPackStart(_content, table, expand: false, fill: false, 0);
        }

        Gtk.BoxPackStart(_content, SectionTitle("Computers allowed in"), expand: false, fill: false, 0);

        if (_node.Settings.KnownClients.Count == 0)
        {
            Gtk.BoxPackStart(_content,
                Label("No computer has connected to this one yet.", dim: true),
                expand: false, fill: false, 0);
        }
        else
        {
            var live = _node.Host.Sessions
                .Select(s => s.ClientFingerprint)
                .Where(f => f is not null)
                .ToArray();

            var table = Table("Computer", "Key fingerprint", "Last seen", "Status", "");
            var row = 1;

            foreach (var client in _node.Settings.KnownClients)
            {
                var connected = live.Any(f => NodeIdentity.FingerprintsMatch(f!, client.Fingerprint));

                Cell(table, 0, row, client.Name.Length > 0 ? client.Name : "unnamed", wide: true);
                Cell(table, 1, row, client.Fingerprint);
                Cell(table, 2, row, client.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                Cell(table, 3, row, client.Blocked ? "blocked" : connected ? "connected" : "allowed");

                var target = client;
                var button = MakeButton(target.Blocked ? "Unblock" : "Block", () =>
                {
                    target.Blocked = !target.Blocked;
                    _node.Settings.Save();

                    // Blocking should bite now, not whenever the computer happens to reconnect.
                    if (target.Blocked)
                        _node.Host.DisconnectClientAsync(target.Fingerprint).GetAwaiter().GetResult();

                    SetStatus(target.Blocked
                        ? $"{target.Name} is blocked and cannot come back."
                        : $"{target.Name} may connect again.");
                    Refresh();
                });

                Gtk.WidgetSetValign(button, AlignCenter);
                Gtk.GridAttach(table, button, 4, row, 1, 1);
                row++;
            }

            Gtk.BoxPackStart(_content, table, expand: false, fill: false, 0);
        }

        Gtk.WidgetShowAll(_window);
    }

    private void ToggleSharing()
    {
        if (_node.Host.IsRunning)
        {
            _node.StopHostAsync().GetAwaiter().GetResult();
            SetStatus("Stopped. Other computers can no longer reach this one.");
        }
        else
        {
            _node.StartHostAsync(CancellationToken.None).GetAwaiter().GetResult();
            SetStatus($"Listening on {_node.Settings.LinkBindAddress}:{_node.Host.Port}.");
        }
        Refresh();
    }

    // ---- widgets ----

    private static IntPtr Label(string text, bool dim = false)
    {
        var label = Gtk.LabelNew(text);
        Gtk.LabelSetXalign(label, 0f);
        Gtk.WidgetSetHalign(label, AlignStart);
        if (dim) Gtk.LabelSetMarkup(label, $"<span alpha='65%'>{Gtk.Escape(text)}</span>");
        return label;
    }

    private static IntPtr Markup(string markup)
    {
        var label = Gtk.LabelNew(null);
        Gtk.LabelSetMarkup(label, markup);
        Gtk.LabelSetXalign(label, 0f);
        Gtk.WidgetSetHalign(label, AlignStart);
        return label;
    }

    private static IntPtr SectionTitle(string text)
    {
        var label = Markup($"<b>{Gtk.Escape(text)}</b>");
        Gtk.WidgetSetMarginTop(label, 18);
        Gtk.WidgetSetMarginBottom(label, 6);
        return label;
    }

    /// <summary>A grid with a dimmed header row, so columns line up across rows.</summary>
    private static IntPtr Table(params string[] headings)
    {
        var grid = Gtk.GridNew();
        Gtk.GridSetColumnSpacing(grid, 18);
        Gtk.GridSetRowSpacing(grid, 6);

        for (var column = 0; column < headings.Length; column++)
        {
            if (headings[column].Length == 0) continue;
            Gtk.GridAttach(grid, Markup($"<small><b>{Gtk.Escape(headings[column])}</b></small>"), column, 0, 1, 1);
        }

        return grid;
    }

    private static void Cell(IntPtr grid, int column, int row, string text, bool wide = false)
    {
        var label = Label(text);
        if (wide)
        {
            Gtk.WidgetSetHexpand(label, true);
            Gtk.LabelSetEllipsize(label, EllipsizeEnd);
        }
        Gtk.WidgetSetValign(label, AlignCenter);
        Gtk.GridAttach(grid, label, column, row, 1, 1);
    }

    private static unsafe IntPtr MakeButton(string title, Action action)
    {
        var button = Gtk.ButtonNew(title);
        Actions[button] = action;

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback = &OnButtonClicked;
        Gtk.SignalConnect(button, "clicked", (IntPtr)callback, IntPtr.Zero, IntPtr.Zero, 0);
        return button;
    }

    private static void AddButton(IntPtr row, string title, Action action)
        => Gtk.BoxPackStart(row, MakeButton(title, action), expand: false, fill: false, 0);

    private void SetStatus(string text)
    {
        if (_status != IntPtr.Zero) Gtk.LabelSetMarkup(_status, $"<span alpha='65%'>{Gtk.Escape(text)}</span>");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnButtonClicked(IntPtr widget, IntPtr data)
    {
        if (!Actions.TryGetValue(widget, out var action)) return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _instance?.SetStatus(ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnWindowDestroyed(IntPtr widget, IntPtr data) => Gtk.MainQuit();
}
