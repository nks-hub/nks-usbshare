using System.Runtime.Versioning;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// The elevated half of a driver change. Every application re-runs its own executable with these
/// arguments and the runas verb; this is what the child does before exiting.
///
/// Elevation cannot redirect the child's output, so progress is written to a file the parent was
/// told about and follows as it appears.
/// </summary>
public static class WinUsbHelper
{
    public const string BindCommand = "__winusb-bind";
    public const string RestoreCommand = "__winusb-restore";

    /// <summary>True when these arguments mean "you are the elevated child".</summary>
    public static bool IsHelperCommand(IReadOnlyList<string> arguments)
        => arguments.Count > 0 && arguments[0] is BindCommand or RestoreCommand;

    /// <summary>
    /// Runs the requested driver change and returns the exit code the child should end with.
    /// Never throws: a failure is reported through the log and the exit code.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int Run(string command, IReadOnlyList<string> arguments, string? logPath)
    {
        using var log = Open(logPath);

        void Report(string line)
        {
            Console.WriteLine(line);
            if (log is null) return;
            log.WriteLine(line);
            log.Flush();
        }

        try
        {
            var installer = new WinUsbDriverInstaller(Report);

            switch (command)
            {
                case BindCommand when arguments.Count >= 1:
                    installer.Bind(arguments[0], arguments.Count > 1 ? arguments[1] : "NKS UsbShare device");
                    return 0;

                case RestoreCommand:
                    installer.RestoreAll();
                    return 0;

                default:
                    Report($"error: '{command}' needs a hardware id.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Report($"error: {ex.Message}");
            return 1;
        }
    }

    private static StreamWriter? Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
