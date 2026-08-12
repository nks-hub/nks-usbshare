using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NksUsbShare.Core.Client;

/// <summary>
/// The machine's virtual USB host controller — the kernel component that makes a remote device
/// visible to every application rather than only to this process.
///
/// Only kernel-mode code can create a virtual USB device, so this is the one piece the project
/// does not provide itself. What it does provide is everything around it: the encrypted link, the
/// access control, and a plain USB/IP endpoint on loopback that any such controller can attach to.
///
/// Linux has a controller in the kernel already (`vhci_hcd`, driven through `usbip`). On Windows
/// the usbip-win2 project ships one, signed so that it installs with Secure Boot left on.
/// </summary>
public abstract class LocalUsbController
{
    public abstract string Name { get; }

    /// <summary>Tells the controller to attach the device published under <paramref name="busId"/>.</summary>
    public abstract Task<bool> AttachAsync(int port, string busId, CancellationToken cancellationToken);

    public abstract Task<bool> DetachAsync(string busId, CancellationToken cancellationToken);

    /// <summary>Returns the controller present on this machine, or null when none is installed.</summary>
    public static LocalUsbController? Locate()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var tool = WindowsPaths.FirstOrDefault(File.Exists);
            return tool is null ? null : new CommandLineController("usbip-win2", tool);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var tool = LinuxPaths.FirstOrDefault(File.Exists);
            return tool is null ? null : new CommandLineController("vhci_hcd", tool);
        }

        // macOS has no way to create a virtual USB device outside the HID class, so there is
        // nothing to locate. Saying so plainly beats pretending to look.
        return null;
    }

    private static readonly string[] WindowsPaths =
    [
        @"C:\Program Files\USBip\usbip.exe",
        @"C:\Program Files (x86)\USBip\usbip.exe",
    ];

    private static readonly string[] LinuxPaths =
    [
        "/usr/sbin/usbip",
        "/usr/bin/usbip",
        "/usr/local/bin/usbip",
    ];

    /// <summary>
    /// Drives a controller through its command line tool. Both the Windows and the Linux tools
    /// take the same shape of arguments, so one implementation covers them.
    /// </summary>
    private sealed class CommandLineController(string name, string toolPath) : LocalUsbController
    {
        public override string Name => name;

        public override async Task<bool> AttachAsync(int port, string busId, CancellationToken cancellationToken)
        {
            // The port is a global option, so it has to precede the subcommand. --once stops the
            // tool from retrying forever when the endpoint is not answering.
            var (exitCode, output) = await RunAsync(
                ["--tcp-port", port.ToString(), "attach", "--remote", "127.0.0.1", "--bus-id", busId, "--once"],
                cancellationToken).ConfigureAwait(false);

            if (exitCode == 0) return true;

            // The Linux tool spells the same thing with single letters and no --once.
            var (fallbackCode, fallbackOutput) = await RunAsync(
                ["--tcp-port", port.ToString(), "attach", "-r", "127.0.0.1", "-b", busId],
                cancellationToken).ConfigureAwait(false);

            if (fallbackCode == 0) return true;

            var message = output.Trim().Length > 0 ? output.Trim() : fallbackOutput.Trim();
            if (message.Length > 0) Console.Error.WriteLine(message);
            return false;
        }

        public override async Task<bool> DetachAsync(string busId, CancellationToken cancellationToken)
        {
            // Detaching is by port number, which has to be looked up from the attached list.
            var (_, listing) = await RunAsync(["port"], cancellationToken).ConfigureAwait(false);

            var portNumber = ParseAttachedPort(listing, busId);
            if (portNumber is null) return false;

            var (exitCode, _) = await RunAsync(["detach", "--port", portNumber], cancellationToken).ConfigureAwait(false);
            if (exitCode == 0) return true;

            var (fallbackCode, _) = await RunAsync(["detach", "-p", portNumber], cancellationToken).ConfigureAwait(false);
            return fallbackCode == 0;
        }

        /// <summary>Finds the controller port a bus id was attached to, from the tool's listing.</summary>
        private static string? ParseAttachedPort(string listing, string busId)
        {
            string? lastPort = null;
            foreach (var line in listing.Split('\n'))
            {
                var trimmed = line.Trim();

                // Lines look like "Port 00: <device> ..." followed by details including the bus id.
                if (trimmed.StartsWith("Port ", StringComparison.OrdinalIgnoreCase))
                {
                    var colon = trimmed.IndexOf(':');
                    if (colon > 5) lastPort = trimmed[5..colon].Trim().TrimStart('0');
                    if (lastPort?.Length == 0) lastPort = "0";
                }

                if (lastPort is not null && trimmed.Contains(busId, StringComparison.Ordinal)) return lastPort;
            }

            return null;
        }

        private async Task<(int ExitCode, string Output)> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(toolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "the controller tool could not be started");

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "the controller tool did not finish in time");
            }

            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
    }
}
