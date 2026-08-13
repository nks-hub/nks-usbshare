namespace NksUsbShare.Cli.Commands;

/// <summary>Runs the node in this terminal: listening, sharing, and reconnecting to known hosts.</summary>
internal sealed class ServeCommand(Cli cli)
{
    public async Task<int> RunAsync()
    {
        var (node, log) = cli.CreateNode(echo: true);
        await using var _ = node;

        using var stopping = Cli.Stopping();

        try
        {
            await node.StartHostAsync(stopping.Token);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Console.Error.WriteLine(
                $"error: cannot listen on {node.Settings.LinkBindAddress}:{node.Settings.LinkPort} — {ex.Message}");
            Console.Error.WriteLine("Another instance, or the desktop application, may already be running.");
            return 1;
        }

        await node.AutoConnectAsync(stopping.Token);
        log.Info("app", "Running. Press Ctrl+C to stop.");

        await stopping.WaitAsync();
        return 0;
    }
}
