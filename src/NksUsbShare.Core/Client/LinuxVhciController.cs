using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using NksUsbShare.Core.Diagnostics;

namespace NksUsbShare.Core.Client;

/// <summary>
/// Attaches a device to the Linux kernel's own virtual USB host controller.
///
/// `vhci_hcd` is part of the kernel, so nothing has to be installed or signed. It is driven
/// entirely through sysfs: userspace opens a socket that speaks USB/IP, then hands the file
/// descriptor to the kernel by writing a line to `attach`. From that moment the kernel owns the
/// socket and this process must leave it alone, which is why the socket handle is deliberately
/// leaked rather than disposed.
///
/// Writing to those sysfs files requires root.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxVhciController : LocalUsbController
{
    private static readonly string[] CandidateRoots =
    [
        "/sys/devices/platform/vhci_hcd.0",
        "/sys/devices/platform/vhci_hcd",
    ];

    private readonly ActivityLog _log;
    private readonly string _root;

    /// <summary>Bus id to the controller port it was attached to.</summary>
    private readonly Dictionary<string, int> _ports = [];

    private LinuxVhciController(string root, ActivityLog log)
    {
        _root = root;
        _log = log;
    }

    public override string Name => "Linux vhci_hcd";

    /// <summary>Returns the controller when the kernel exposes one, otherwise null.</summary>
    public static LinuxVhciController? TryCreate(ActivityLog log)
    {
        var root = CandidateRoots.FirstOrDefault(path => File.Exists(Path.Combine(path, "status")));
        return root is null ? null : new LinuxVhciController(root, log);
    }

    public override async Task<bool> AttachAsync(int port, string busId, CancellationToken cancellationToken)
    {
        var free = FindFreePort();
        if (free is null)
        {
            _log.Error("vhci", "The kernel controller has no free port left.");
            return false;
        }

        // The socket is handed to the kernel, so it must outlive this method. It is closed again
        // by detaching, which is why it is not wrapped in a using.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);

            var imported = await UsbIpHandshake.ImportAsync(socket, busId, cancellationToken).ConfigureAwait(false);
            if (imported is null)
            {
                socket.Dispose();
                _log.Error("vhci", $"The endpoint refused to hand over bus id {busId}.");
                return false;
            }

            // "port sockfd devid speed", exactly as the kernel's attach_store expects it.
            var line = $"{free.Value} {(int)socket.Handle} {imported.DevId} {imported.Speed}";
            await File.WriteAllTextAsync(Path.Combine(_root, "attach"), line, cancellationToken).ConfigureAwait(false);

            _ports[busId] = free.Value;
            _log.Info("vhci", $"Attached {busId} to kernel controller port {free.Value}.");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            socket.Dispose();
            _log.Error("vhci", "Writing to the kernel controller needs root.");
            return false;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            _log.Error("vhci", $"Attaching {busId} failed: {ex.Message}");
            return false;
        }
    }

    public override async Task<bool> DetachAsync(string busId, CancellationToken cancellationToken)
    {
        if (!_ports.TryGetValue(busId, out var port)) return false;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "detach"), port.ToString(), cancellationToken)
                .ConfigureAwait(false);
            _ports.Remove(busId);
            _log.Info("vhci", $"Detached kernel controller port {port}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("vhci", $"Detaching port {port} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads the controller's port table and returns the first unused port.
    /// The table looks like:
    /// <code>
    /// hub port sta spd dev      sockfd local_busid
    /// hs  0000 004 000 00000000 000000 0-0
    /// </code>
    /// where a status of 4 means the port is free.
    /// </summary>
    private int? FindFreePort()
    {
        const int statusFree = 4;

        foreach (var file in Directory.EnumerateFiles(_root, "status*").OrderBy(f => f, StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (var line in text.Split('\n').Skip(1))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 3) continue;

                // High speed ports come first and suit every device this project carries today.
                if (!columns[0].Equals("hs", StringComparison.Ordinal)) continue;
                if (!int.TryParse(columns[1], out var port)) continue;
                if (!int.TryParse(columns[2], out var status)) continue;

                if (status == statusFree) return port;
            }
        }

        return null;
    }
}
