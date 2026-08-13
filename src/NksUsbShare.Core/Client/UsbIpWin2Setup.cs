using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Usb.Windows;

namespace NksUsbShare.Core.Client;

/// <summary>
/// Installs the virtual USB host controller on Windows without asking anyone to go and find it.
///
/// Only kernel-mode code can present a virtual USB device, and a driver has to be signed by
/// someone Windows already trusts, so this is the one component the project cannot build itself.
/// It uses the controller from usbip-win2, whose drivers carry a Microsoft Windows Hardware
/// Compatibility Publisher signature and therefore install on stock Windows with Secure Boot on.
///
/// The download is a pinned release checked against a pinned hash: the version that is fetched is
/// the version that was tested, and a substituted file is refused rather than run. The installer
/// itself needs administrator rights, so Windows asks for consent once — that prompt is the only
/// thing that cannot be taken off the user.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsbIpWin2Setup
{
    /// <summary>The release this project was verified against.</summary>
    public const string Version = "0.9.7.8";

    private const string ReleaseBase = "https://github.com/vadimgrn/usbip-win2/releases/download/v." + Version + "/";

    private static readonly (string Asset, string Sha256) X64 =
        ($"USBip-{Version}-x64.exe", "44451FE06F4186125C2A5ECD25B099C5560A61A60B1E56F5A0758E77A60AFA44");

    private static readonly (string Asset, string Sha256) Arm64 =
        ($"USBip-{Version}-arm64.exe", "0FDA9D3FC19E31753F57FCF8E3401939C41E259964A960B101F848C89FC19F19");

    private static readonly string[] ToolPaths =
    [
        @"C:\Program Files\USBip\usbip.exe",
        @"C:\Program Files (x86)\USBip\usbip.exe",
    ];

    /// <summary>The controller's command line tool, or null when it is not installed.</summary>
    public static string? ToolPath() => ToolPaths.FirstOrDefault(File.Exists);

    /// <summary>
    /// Whether the controller is there and working. The question is put to the tool itself, which
    /// answers "VHCI device not found" when the driver is not loaded — three weaker checks all
    /// say yes in states where an attach still fails: the tool can be on disk without its driver,
    /// the driver can be installed without being started, and the device node can survive an
    /// uninstall that took the driver away.
    /// </summary>
    public static async Task<bool> ControllerWorksAsync(string toolPath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(toolPath)
            {
                ArgumentList = { "port" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 &&
                   !stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure the controller is installed, downloading and installing it if it is not, and
    /// returns the path to its tool. Returns null when it could not be put in place.
    /// </summary>
    public static async Task<string?> EnsureInstalledAsync(ActivityLog log, CancellationToken cancellationToken)
    {
        var existing = ToolPath();
        if (existing is not null &&
            await ControllerWorksAsync(existing, cancellationToken).ConfigureAwait(false))
        {
            return existing;
        }

        if (existing is not null)
            log.Warning("controller", "usbip-win2 is on disk but its controller is not working; installing it again.");

        var architecture = RuntimeInformation.ProcessArchitecture;
        var (asset, expectedHash) = architecture switch
        {
            Architecture.X64 => X64,
            Architecture.Arm64 => Arm64,
            _ => default,
        };

        if (asset is null)
        {
            log.Warning("controller", $"There is no usbip-win2 build for {architecture}.");
            return null;
        }

        var installer = Path.Combine(Path.GetTempPath(), $"nksusb-{asset}");

        try
        {
            log.Info("controller", $"No virtual USB controller is installed. Fetching usbip-win2 {Version}...");
            await DownloadAsync(ReleaseBase + asset, installer, cancellationToken).ConfigureAwait(false);

            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installer, cancellationToken)
                .ConfigureAwait(false)));

            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                log.Error("controller", "The downloaded installer does not match the expected hash; it was not run.");
                return null;
            }

            log.Info("controller", "Installing it. Windows will ask for administrator approval.");
            if (!StartInstaller(installer)) return null;

            // What matters is the controller being ready, not the installer having exited: when a
            // copy is already on disk the installer stops to ask about it, long after the driver
            // is in place. So the wait watches the device tree instead of the process.
            var tool = await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
            if (tool is null)
            {
                log.Error("controller", "The installer ran but the controller did not appear.");
                return null;
            }

            // Installing a driver that filters the USB class parks the hubs until the machine is
            // restarted, and everything on them — keyboards and mice included — stops working.
            // Restarting the controllers rebuilds the stack there and then, so nobody is left
            // without input waiting for a reboot.
            RepairParkedUsbDevices(log);

            log.Info("controller", $"usbip-win2 {Version} is installed.");
            return tool;
        }
        catch (HttpRequestException ex)
        {
            log.Error("controller", $"usbip-win2 could not be downloaded: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("controller", $"usbip-win2 could not be installed: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(installer); } catch (IOException) { }
        }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var file = File.Create(destination);
        await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the installer without a window. It is an Inno Setup package, so it takes
    /// /VERYSILENT — the NSIS spelling (/S) is quietly ignored and only half of it installs.
    /// /SP- drops the "this will install..." question a reinstall would otherwise stop on.
    /// </summary>
    private static bool StartInstaller(string installer)
    {
        var startInfo = new ProcessStartInfo(installer)
        {
            Arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;   // the elevation prompt was declined
        }
    }

    /// <summary>Waits until the tool is on disk and answers that its controller is working.</summary>
    private static async Task<string?> WaitForControllerAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);

        while (true)
        {
            var tool = ToolPath();
            if (tool is not null && await ControllerWorksAsync(tool, cancellationToken).ConfigureAwait(false))
                return tool;

            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restarts the controllers of any USB devices Windows parked while installing the driver.
    /// pnputil ships with Windows; restarting a host controller rebuilds everything below it.
    /// </summary>
    private static void RepairParkedUsbDevices(ActivityLog log)
    {
        var parked = DeviceNode.All()
            .Where(node => node.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && node.NeedsRestart())
            .ToList();

        if (parked.Count == 0) return;

        log.Warning("controller",
            $"{parked.Count} USB device(s) were parked by the driver install; restarting their controllers.");

        var controllers = parked
            .Select(node => node.Parent()?.Parent() ?? node.Parent())
            .Where(node => node is not null)
            .Select(node => node!.InstanceId)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var controller in controllers) RestartDevice(controller, log);
    }

    private static void RestartDevice(string instanceId, ActivityLog log)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("pnputil.exe")
            {
                ArgumentList = { "/restart-device", instanceId },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(30_000);
            log.Info("controller", $"Restarted {instanceId}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warning("controller", $"{instanceId} could not be restarted: {ex.Message}");
        }
    }
}
