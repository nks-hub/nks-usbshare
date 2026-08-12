using System.Text.Json;
using System.Text.Json.Nodes;

namespace NksUsbShare.Mcp;

/// <summary>
/// Line-delimited JSON-RPC 2.0 over standard input and output, which is what the Model Context
/// Protocol runs on. Implemented directly rather than pulled in, so the server has no dependencies.
/// </summary>
public sealed class JsonRpcConnection
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonRpcConnection(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads one request. Returns null at end of input, and skips lines that are not JSON.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Trim().Length == 0) continue;

            try
            {
                if (JsonNode.Parse(line) is JsonObject request) return request;
            }
            catch (JsonException)
            {
                // A malformed line has no id, so there is nobody to answer; ignore it.
            }
        }
    }

    public async Task RespondAsync(JsonNode? id, JsonNode? result, CancellationToken cancellationToken)
        => await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }, cancellationToken).ConfigureAwait(false);

    public async Task FailAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken)
        => await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }, cancellationToken).ConfigureAwait(false);

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var text = message.ToJsonString(Options);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
