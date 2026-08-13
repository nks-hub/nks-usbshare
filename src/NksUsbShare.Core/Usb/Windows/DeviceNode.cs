using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// One node in the Windows device tree, with the handful of questions this project asks of it:
/// which driver is on it, what it is called, where it hangs, and which hub port it sits on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceNode
{
    private readonly uint _handle;

    private DeviceNode(uint handle, string instanceId)
    {
        _handle = handle;
        InstanceId = instanceId;
    }

    public string InstanceId { get; }

    public string? Service => StringProperty(ref DriverNative.DevPkeyDeviceService);

    /// <summary>The name to show, falling back to the description as Windows' own tools do.</summary>
    public string? FriendlyName => StringProperty(ref DriverNative.DevPkeyDeviceFriendlyName)
                                   ?? StringProperty(ref DriverNative.DevPkeyDeviceDescription);
    public string? DriverInfPath => StringProperty(ref DriverNative.DevPkeyDeviceDriverInfPath);
    public IReadOnlyList<string> HardwareIds => StringListProperty(ref DriverNative.DevPkeyDeviceHardwareIds);

    /// <summary>The port number this device occupies on its parent hub.</summary>
    public uint? HubPort => UInt32Property(ref DriverNative.DevPkeyDeviceAddress);

    /// <summary>Opens a node by instance id, or null when nothing matches.</summary>
    public static DeviceNode? Open(string instanceId)
    {
        var result = DriverNative.CM_Locate_DevNodeW(out var handle, instanceId, 0);
        return result == DriverNative.CrSuccess ? new DeviceNode(handle, instanceId) : null;
    }

    public DeviceNode? Parent()
    {
        if (DriverNative.CM_Get_Parent(out var parent, _handle, 0) != DriverNative.CrSuccess) return null;
        var id = IdOf(parent);
        return id is null ? null : new DeviceNode(parent, id);
    }

    /// <summary>The nodes this one enumerated, which for a composite device are its interfaces.</summary>
    public IReadOnlyList<DeviceNode> Children()
    {
        var children = new List<DeviceNode>();
        if (DriverNative.CM_Get_Child(out var child, _handle, 0) != DriverNative.CrSuccess) return children;

        while (true)
        {
            var id = IdOf(child);
            if (id is not null) children.Add(new DeviceNode(child, id));
            if (DriverNative.CM_Get_Sibling(out var sibling, child, 0) != DriverNative.CrSuccess) break;
            child = sibling;
        }

        return children;
    }

    /// <summary>Asks Windows to enumerate this node's children again.</summary>
    public bool Reenumerate() => DriverNative.CM_Reenumerate_DevNode(_handle, 0) == DriverNative.CrSuccess;

    /// <summary>
    /// True when Windows has parked this device until the machine restarts. Installing a driver
    /// that filters a whole device class does this to the USB hubs, and every device on them —
    /// keyboards and mice included — stops working until the stack is rebuilt.
    /// </summary>
    public bool NeedsRestart()
        => DriverNative.CM_Get_DevNode_Status(out _, out var problem, _handle, 0) == DriverNative.CrSuccess
           && problem == DriverNative.CmProblemNeedRestart;

    /// <summary>Every node under the root, depth first. Used to look for devices in trouble.</summary>
    public static IEnumerable<DeviceNode> All()
    {
        if (DriverNative.CM_Locate_DevNodeW(out var root, null, 0) != DriverNative.CrSuccess) yield break;

        var stack = new Stack<uint>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (DriverNative.CM_Get_Child(out var child, current, 0) == DriverNative.CrSuccess)
            {
                var walker = child;
                while (true)
                {
                    stack.Push(walker);
                    if (DriverNative.CM_Get_Sibling(out var sibling, walker, 0) != DriverNative.CrSuccess) break;
                    walker = sibling;
                }
            }

            var id = IdOf(current);
            if (id is not null) yield return new DeviceNode(current, id);
        }
    }

    /// <summary>
    /// Finds the present device whose hardware ids include <paramref name="hardwareId"/>. A USB
    /// instance id starts with its hardware id, so the direct lookup below is tried first; walking
    /// the whole tree and reading every node's ids is the fallback and is far slower.
    /// </summary>
    public static DeviceNode? FindByHardwareId(string hardwareId)
    {
        foreach (var candidate in EnumerateChildrenOfRootMatching(hardwareId))
            return candidate;

        return null;
    }

    private static IEnumerable<DeviceNode> EnumerateChildrenOfRootMatching(string hardwareId)
    {
        // The device tree is walked from the root. Only nodes whose instance id begins with the
        // hardware id can match, which keeps this cheap even on a machine with hundreds of nodes.
        if (DriverNative.CM_Locate_DevNodeW(out var root, null, 0) != DriverNative.CrSuccess) yield break;

        var stack = new Stack<uint>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (DriverNative.CM_Get_Child(out var child, current, 0) == DriverNative.CrSuccess)
            {
                var walker = child;
                while (true)
                {
                    stack.Push(walker);
                    if (DriverNative.CM_Get_Sibling(out var sibling, walker, 0) != DriverNative.CrSuccess) break;
                    walker = sibling;
                }
            }

            var id = IdOf(current);
            if (id is null) continue;
            if (!id.StartsWith(hardwareId + "\\", StringComparison.OrdinalIgnoreCase)) continue;

            yield return new DeviceNode(current, id);
        }
    }

    private static string? IdOf(uint handle)
    {
        var buffer = new char[512];
        return DriverNative.CM_Get_Device_IDW(handle, buffer, (uint)buffer.Length, 0) == DriverNative.CrSuccess
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end && end >= 0 ? end : buffer.Length)
            : null;
    }

    private byte[]? RawProperty(ref DriverNative.DevPropKey key, out uint type)
    {
        uint size = 0;
        type = 0;
        DriverNative.CM_Get_DevNode_PropertyW(_handle, ref key, out type, null, ref size, 0);
        if (size == 0) return null;

        var buffer = new byte[size];
        return DriverNative.CM_Get_DevNode_PropertyW(_handle, ref key, out type, buffer, ref size, 0)
               == DriverNative.CrSuccess
            ? buffer
            : null;
    }

    private string? StringProperty(ref DriverNative.DevPropKey key)
    {
        var raw = RawProperty(ref key, out var type);
        if (raw is null || type != DriverNative.DevPropTypeString) return null;

        var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private IReadOnlyList<string> StringListProperty(ref DriverNative.DevPropKey key)
    {
        var raw = RawProperty(ref key, out var type);
        if (raw is null || type != DriverNative.DevPropTypeStringList) return [];

        return Encoding.Unicode.GetString(raw)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    private uint? UInt32Property(ref DriverNative.DevPropKey key)
    {
        var raw = RawProperty(ref key, out var type);
        return raw is not null && type == DriverNative.DevPropTypeUInt32 && raw.Length >= 4
            ? BitConverter.ToUInt32(raw, 0)
            : null;
    }

    /// <summary>
    /// Turns the hub port this device sits on off and on again — a replug with nobody at the
    /// cable. Windows often records a new driver but leaves the old stack standing until the
    /// device disappears and comes back; on a composite device the interface nodes its previous
    /// driver created keep holding the interfaces and nothing can open it. Restarting the node,
    /// restarting the hub and disabling the device are all refused while the machine has a pending
    /// reboot. A port cycle is a hardware event and goes through regardless.
    /// </summary>
    public string CyclePort()
    {
        var parent = Parent();
        var port = HubPort;
        if (parent is null || port is null) return "the hub or port could not be worked out";

        var hubPath = @"\\?\" + parent.InstanceId.ToLowerInvariant().Replace('\\', '#')
                              + "#" + DriverNative.UsbHubInterfaceGuid;

        var hub = DriverNative.CreateFileW(hubPath, DriverNative.GenericReadWrite,
            DriverNative.FileShareReadWrite, IntPtr.Zero, DriverNative.OpenExisting, 0, IntPtr.Zero);

        if (hub == new IntPtr(-1))
            return $"the hub could not be opened (Win32 {Marshal.GetLastWin32Error()})";

        try
        {
            var input = new DriverNative.CyclePortParams { ConnectionIndex = port.Value };
            var output = new DriverNative.CyclePortParams();
            var size = Marshal.SizeOf<DriverNative.CyclePortParams>();

            return DriverNative.DeviceIoControl(hub, DriverNative.IoctlUsbHubCyclePort,
                ref input, size, ref output, size, out _, IntPtr.Zero)
                ? $"port {port} cycled"
                : $"the port could not be cycled (Win32 {Marshal.GetLastWin32Error()})";
        }
        finally
        {
            DriverNative.CloseHandle(hub);
        }
    }
}
