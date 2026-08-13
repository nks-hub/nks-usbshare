using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using NksUsbShare.Core.Diagnostics;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// Hands a Windows device over to the in-box winusb.sys so it can be shared, and gives it back
/// afterwards.
///
/// Windows only allows user-mode transfers on a device bound to winusb.sys, so a device held by
/// its class driver cannot be forwarded as it stands. Rebinding needs a driver package: a
/// generated INF, a catalog signed by a certificate the machine trusts, and an install through
/// pnputil. Every one of those steps is done by tooling that ships with Windows, so no toolkit
/// has to be installed and Secure Boot can stay on — winusb.sys itself is already signed by
/// Microsoft, and only the small package pointing at it needs trust.
///
/// It does need administrator rights, which is why the work happens in an elevated child process
/// rather than in this one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbBinder(ActivityLog log)
{
    private const string ScriptResource = "NksUsbShare.Core.Usb.Windows.bind-winusb.ps1";

    public sealed record Result(bool Success, string Message);

    /// <summary>Claims the device away from its current driver so it can be shared.</summary>
    public Task<Result> BindAsync(UsbDeviceInfo device, CancellationToken cancellationToken)
    {
        var hardwareId = HardwareIdOf(device);
        if (hardwareId is null)
            return Task.FromResult(new Result(false,
                $"Could not work out a hardware id for {device.DisplayName}."));

        log.Info("winusb", $"Handing {device.DisplayName} ({hardwareId}) to WinUSB.");
        return RunAsync(["-HardwareId", hardwareId, "-Description", $"NKS UsbShare - {device.DisplayName}"],
            cancellationToken);
    }

    /// <summary>Gives every claimed device back to the driver it had before.</summary>
    public Task<Result> RestoreAllAsync(CancellationToken cancellationToken)
    {
        log.Info("winusb", "Giving claimed devices back to their original drivers.");
        return RunAsync(["-Restore"], cancellationToken);
    }

    /// <summary>Devices this tool currently holds, as reported by the script's own record.</summary>
    public Task<Result> StatusAsync(CancellationToken cancellationToken) => RunAsync(["-Status"], cancellationToken);

    /// <summary>
    /// Builds the hardware id Windows matches on. The device instance id has the form
    /// <c>USB\VID_xxxx&amp;PID_xxxx\serial</c>; the middle section is what an INF matches.
    /// </summary>
    public static string? HardwareIdOf(UsbDeviceInfo device)
    {
        var parts = device.DeviceId.Split('\\');
        if (parts.Length >= 2 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase))
            return $"USB\\{parts[1]}";

        return device is { VendorId: not 0, ProductId: not 0 }
            ? $"USB\\VID_{device.VendorId:X4}&PID_{device.ProductId:X4}"
            : null;
    }

    private async Task<Result> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var scriptPath = await ExtractScriptAsync(cancellationToken).ConfigureAwait(false);
        var transcript = Path.Combine(Path.GetTempPath(), $"nksusb-bind-{Guid.NewGuid():N}.log");

        // Every argument is quoted, not just the ones with spaces: a hardware id looks like
        // USB\VID_1234&PID_5678, and an unquoted ampersand ends the command as far as PowerShell
        // is concerned. Switches start with a dash and must stay bare.
        var quoted = string.Join(' ', arguments.Select(a =>
            a.StartsWith('-') ? a : $"'{a.Replace("'", "''")}'"));
        var command =
            $"-NoProfile -ExecutionPolicy Bypass -Command " +
            $"\"& '{scriptPath}' {quoted} *> '{transcript}'\"";

        var startInfo = new ProcessStartInfo("powershell.exe", command)
        {
            // Elevation is what makes Windows show its consent prompt, and it needs the shell.
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new Result(false, "The elevated helper could not be started.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = File.Exists(transcript) ? await File.ReadAllTextAsync(transcript, CancellationToken.None).ConfigureAwait(false) : "";

            // The helper runs elevated in a hidden window, so its transcript is the only account of
            // what happened to the device. It belongs in the activity log where it can be read,
            // not at a level the window filters out.
            foreach (var line in output.Split('\n').Where(l => l.Trim().Length > 0))
                log.Info("winusb", line.TrimEnd());

            if (output.Trim().Length == 0)
                log.Warning("winusb", "The elevated helper produced no output at all.");

            if (process.ExitCode != 0)
                return new Result(false, Summarise(output) ?? $"The helper exited with code {process.ExitCode}.");

            return new Result(true, Summarise(output) ?? "Done.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 is ERROR_CANCELLED: the user dismissed the elevation prompt.
            return new Result(false, "Administrator approval was declined, so the device was left alone.");
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "The driver change took too long and was abandoned.");
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
        finally
        {
            try { File.Delete(transcript); } catch (IOException) { }
        }
    }

    /// <summary>Picks the most useful line out of the helper's output to show the operator.</summary>
    private static string? Summarise(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        foreach (var marker in (string[])["ready to be shared", "still reports", "Done."])
        {
            var match = lines.LastOrDefault(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        var failure = lines.LastOrDefault(l =>
            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Administrator rights", StringComparison.OrdinalIgnoreCase));

        return failure ?? lines.LastOrDefault();
    }

    /// <summary>
    /// The script travels inside the assembly so a published binary is self-contained; it is
    /// written out fresh each run so an edited copy on disk cannot be picked up instead.
    /// </summary>
    private static async Task<string> ExtractScriptAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nksusb-tools");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bind-winusb.ps1");

        await using var stream = typeof(WinUsbBinder).Assembly.GetManifestResourceStream(ScriptResource)
                                 ?? throw new InvalidOperationException(
                                     $"The helper script is missing from the build. Available: " +
                                     string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames()));

        await using var file = File.Create(path);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
