using System.Diagnostics;
using System.Runtime.Versioning;
using NksUsbShare.Core.Diagnostics;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// Hands a Windows device over to the in-box winusb.sys so it can be shared, and gives it back
/// afterwards.
///
/// Windows only allows user-mode transfers on a device bound to winusb.sys, so a device held by
/// its class driver cannot be forwarded as it stands. Rebinding needs a driver package: a
/// generated INF, a catalog signed by a certificate the machine trusts, and an install into the
/// driver store. All of it goes through Windows' own calls, so nothing has to be installed
/// alongside the application and Secure Boot can stay on — winusb.sys itself is already signed by
/// Microsoft, and only the small package pointing at it needs trust.
///
/// It does need administrator rights, which is why the work happens in an elevated child process
/// rather than in this one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbBinder(ActivityLog log)
{
    public sealed record Result(bool Success, string Message);

    /// <summary>Claims the device away from its current driver so it can be shared.</summary>
    public Task<Result> BindAsync(UsbDeviceInfo device, CancellationToken cancellationToken)
    {
        var hardwareId = HardwareIdOf(device);
        if (hardwareId is null)
            return Task.FromResult(new Result(false,
                $"Could not work out a hardware id for {device.DisplayName}."));

        log.Info("winusb", $"Handing {device.DisplayName} ({hardwareId}) to WinUSB.");
        return RunAsync([WinUsbHelper.BindCommand, hardwareId, $"NKS UsbShare - {device.DisplayName}"],
            cancellationToken);
    }

    /// <summary>Gives every claimed device back to the driver it had before.</summary>
    public Task<Result> RestoreAllAsync(CancellationToken cancellationToken)
    {
        log.Info("winusb", "Giving claimed devices back to their original drivers.");
        return RunAsync([WinUsbHelper.RestoreCommand], cancellationToken);
    }

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
        var transcript = Path.Combine(Path.GetTempPath(), $"nksusb-bind-{Guid.NewGuid():N}.log");

        // The elevated half is this same executable, run again with a command nobody types. There
        // is no script and no interpreter in the way — the driver work is the application's own.
        var self = Environment.ProcessPath
                   ?? throw new InvalidOperationException("The running executable could not be located.");

        var command = string.Join(' ', arguments.Append("--log").Append(transcript).Select(Quote));

        var startInfo = new ProcessStartInfo(self, command)
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

            // Signing a driver catalog and installing it takes a minute or two. The helper runs
            // elevated in a hidden window, so its transcript is the only account of what is
            // happening; it is followed as it is written rather than read at the end, otherwise
            // the whole operation is a silent wait that looks like a hang.
            var output = await FollowAsync(process, transcript, timeout.Token).ConfigureAwait(false);

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

    /// <summary>
    /// Waits for the elevated helper, reporting each line of its transcript as it appears. The
    /// file is opened shared because the helper is writing to it at the same time.
    /// </summary>
    private async Task<string> FollowAsync(Process process, string transcript, CancellationToken cancellationToken)
    {
        // The whole file is re-read on every poll and lines already reported are counted, rather
        // than seeking to a saved offset: a character count is not a byte offset, and the point is
        // to show the operator what is happening, not to read the file efficiently.
        var text = "";
        var reported = 0;

        while (true)
        {
            var finished = process.HasExited;

            // A poll that came back empty means the file was momentarily locked, not that the
            // helper unsaid what it had already written.
            var fresh = await ReadSharedAsync(transcript, cancellationToken).ConfigureAwait(false);
            if (fresh.Length > 0) text = fresh;

            var lines = text.Split('\n');

            // While the helper is still writing, the last line may be half-written, so it waits.
            var complete = finished ? lines.Length : lines.Length - 1;
            for (; reported < complete; reported++)
                if (lines[reported].Trim().Length > 0)
                    log.Info("winusb", lines[reported].TrimEnd());

            if (finished) break;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return text;
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return "";

        try
        {
            // The helper still has the file open, so it has to be opened shared.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A transient lock is not worth failing the whole operation for; the next poll retries.
            return "";
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

    /// <summary>Quotes one argument for a command line; a device description has spaces in it.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Contains(' ') && !argument.Contains('"')
            ? argument
            : '"' + argument.Replace("\"", "\\\"") + '"';
}
