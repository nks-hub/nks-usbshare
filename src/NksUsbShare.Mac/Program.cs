using System.Runtime.InteropServices;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Mac;

// Deliberately synchronous. AppKit insists that the application object, its menu and its windows
// are created on the process's main thread, and any await before that point would move the rest
// of the work onto a thread pool thread.

if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    Console.Error.WriteLine("This is the macOS application. On Windows use NksUsbShare.exe, elsewhere use nksusb.");
    return 1;
}

var settings = NodeSettings.LoadOrCreate(NodeSettings.StateDirectoryFromArguments(args));
var log = new ActivityLog { EchoToConsole = true };
var node = new NodeService(settings, log);

try
{
    node.StartHostAsync(CancellationToken.None).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    // A window with no listener is still useful for looking at what is attached, so this is
    // reported rather than fatal.
    log.Error("app", $"Could not start sharing: {ex.Message}");
}

// Connecting to known hosts can wait; it must not delay the window or block the main thread.
_ = Task.Run(() => node.AutoConnectAsync(CancellationToken.None));

try
{
    new MacApp(node).Run();
    return 0;
}
finally
{
    node.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
