using System.Text.Json.Nodes;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Mcp;

// The Model Context Protocol speaks JSON-RPC over stdio, so nothing may be written to standard
// output except protocol messages. Diagnostics go to standard error.
var settings = NodeSettings.LoadOrCreate(NodeSettings.StateDirectoryFromArguments(args));
var log = new ActivityLog();
await using var node = new NodeService(settings, log);

// The listener stays off: this process manages configuration and client-side use, while the
// desktop application or `nksusb serve` owns the port when a node is actually sharing.
var registry = new ToolRegistry(node);
var tools = registry.All.ToDictionary(tool => tool.Name);

var connection = new JsonRpcConnection(Console.In, Console.Out);
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

await Console.Error.WriteLineAsync($"nksusb-mcp ready — {tools.Count} tools, node '{settings.HostName}'.");

while (!stopping.IsCancellationRequested)
{
    JsonObject? request;
    try
    {
        request = await connection.ReadAsync(stopping.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (request is null) break;

    var id = request["id"];
    var method = request["method"]?.GetValue<string>() ?? "";

    // Notifications carry no id and must not be answered.
    if (id is null)
    {
        continue;
    }

    try
    {
        switch (method)
        {
            case "initialize":
                await connection.RespondAsync(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "nks-usbshare",
                        ["version"] = typeof(NodeService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                    },
                }, stopping.Token);
                break;

            case "ping":
                await connection.RespondAsync(id, new JsonObject(), stopping.Token);
                break;

            case "tools/list":
                var list = new JsonArray();
                foreach (var tool in registry.All)
                {
                    list.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone(),
                    });
                }
                await connection.RespondAsync(id, new JsonObject { ["tools"] = list }, stopping.Token);
                break;

            case "tools/call":
                await CallToolAsync(id, request, stopping.Token);
                break;

            default:
                await connection.FailAsync(id, -32601, $"Unknown method '{method}'.", stopping.Token);
                break;
        }
    }
    catch (Exception ex)
    {
        await connection.FailAsync(id, -32603, ex.Message, stopping.Token);
    }
}

return 0;

async Task CallToolAsync(JsonNode? id, JsonObject request, CancellationToken cancellationToken)
{
    var parameters = request["params"] as JsonObject;
    var name = parameters?["name"]?.GetValue<string>() ?? "";
    var arguments = parameters?["arguments"] as JsonObject ?? [];

    if (!tools.TryGetValue(name, out var tool))
    {
        await connection.FailAsync(id, -32602, $"Unknown tool '{name}'.", cancellationToken);
        return;
    }

    string text;
    var failed = false;
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        text = await tool.Invoke(arguments, timeout.Token);
    }
    catch (Exception ex)
    {
        // A tool failure is reported inside the result so the model can read and react to it,
        // which is what the protocol asks for.
        text = ex.Message;
        failed = true;
    }

    await connection.RespondAsync(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = failed,
    }, cancellationToken);
}
