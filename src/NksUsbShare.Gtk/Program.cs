using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Gtk;

// Synchronous on purpose: GTK expects every widget call to come from the thread that initialised
// it, and an await before that point would move the rest of the work onto a thread pool thread.

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    Console.Error.WriteLine("This is the Linux application. On Windows use NksUsbShare.exe, on macOS the Mac application.");
    return 1;
}

var settings = NodeSettings.LoadOrCreate();
var log = new ActivityLog { EchoToConsole = true };
var node = new NodeService(settings, log);

try
{
    node.StartHostAsync(CancellationToken.None).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    // A window with no listener still shows what is attached, so this is reported, not fatal.
    log.Error("app", $"Could not start sharing: {ex.Message}");
}

_ = Task.Run(() => node.AutoConnectAsync(CancellationToken.None));

try
{
    return new GtkApp(node).Run() ? 0 : 1;
}
finally
{
    node.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
