using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NksUsbShare.Core;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.App.Web;

/// <summary>
/// The local control plane: a small HTTP API plus the single-page interface that drives it.
/// It binds to loopback by default, because whoever can reach it can hand out USB devices.
/// </summary>
public static class WebInterface
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static WebApplication Build(NodeService node, string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();

        var address = IPAddress.TryParse(node.Settings.WebUiBindAddress, out var parsed) ? parsed : IPAddress.Loopback;
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, node.Settings.WebUiPort));

        var app = builder.Build();
        app.UseWebSockets();

        MapStaticFiles(app);
        MapApi(app, node);
        return app;
    }

    // ---- static assets ----

    private static void MapStaticFiles(WebApplication app)
    {
        // The interface is three files compiled in as resources, so a published binary is
        // genuinely self-contained and there is no wwwroot to lose.
        app.MapGet("/", () => Results.Content(Assets.IndexHtml, "text/html; charset=utf-8"));
        app.MapGet("/app.css", () => Results.Content(Assets.Css, "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => Results.Content(Assets.JavaScript, "text/javascript; charset=utf-8"));
        app.MapGet("/favicon.svg", () => Results.Content(Assets.FaviconSvg, "image/svg+xml"));
    }

    // ---- api ----

    private static void MapApi(WebApplication app, NodeService node)
    {
        app.MapGet("/api/state", async (CancellationToken ct) => Results.Json(await BuildStateAsync(node, ct), Json));

        app.MapPost("/api/share", async (ShareRequest request, CancellationToken ct) =>
        {
            try
            {
                await node.ShareDeviceAsync(request.DeviceId, ct);
                return Results.Json(new { ok = true }, Json);
            }
            catch (UsbBackendException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, Json, statusCode: 400);
            }
        });

        app.MapPost("/api/unshare", (ShareRequest request) =>
        {
            node.UnshareDevice(request.DeviceId);
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/share/enabled", (ShareEnabledRequest request) =>
        {
            node.SetShareEnabled(request.DeviceId, request.Enabled);
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/share/clients", (ShareClientsRequest request) =>
        {
            var share = node.Settings.Shares.FirstOrDefault(s => s.DeviceId == request.DeviceId);
            if (share is null) return Results.Json(new { ok = false, error = "Not shared." }, Json, statusCode: 404);

            share.AllowedClients = request.AllowedClients.Where(c => c.Trim().Length > 0).ToList();
            node.Settings.Save();
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/host/start", async (CancellationToken ct) =>
        {
            try
            {
                await node.StartHostAsync(ct);
                return Results.Json(new { ok = true }, Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, Json, statusCode: 400);
            }
        });

        app.MapPost("/api/host/stop", async () =>
        {
            await node.StopHostAsync();
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/token/regenerate", () =>
        {
            node.Settings.PairingToken = AccessToken.Generate();
            node.Settings.Save();
            node.Log.Warning("host", "The pairing token was regenerated; existing clients must be re-paired.");
            return Results.Json(new { ok = true, token = node.Settings.PairingToken }, Json);
        });

        app.MapPost("/api/hosts", (AddHostRequest request) =>
        {
            var remote = node.AddRemote(request.Address, request.Port, request.Token, request.Name);
            remote.AutoConnect = request.AutoConnect;
            node.Settings.Save();
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/hosts/remove", async (HostRefRequest request) =>
        {
            await node.DisconnectAsync(request.Address, request.Port);
            node.RemoveRemote(request.Address, request.Port);
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/hosts/devices", async (HostRefRequest request, CancellationToken ct) =>
        {
            var remote = FindRemote(node, request);
            if (remote is null) return Results.Json(new { ok = false, error = "Unknown host." }, Json, statusCode: 404);

            try
            {
                var devices = await node.ListRemoteDevicesAsync(remote, ct);
                var imported = node.Imports.Select(i => i.Key).ToHashSet();
                return Results.Json(new
                {
                    ok = true,
                    devices = devices.Select(d => new
                    {
                        d.DeviceId,
                        Name = d.DisplayName,
                        d.Manufacturer,
                        d.HardwareKey,
                        d.SerialNumber,
                        Speed = ViewMapper.Describe(d.Speed),
                        Kind = ViewMapper.DescribeKind(d),
                        InterfaceCount = d.Interfaces.Count,
                        Imported = imported.Contains($"{request.Address}:{request.Port}|{d.DeviceId}"),
                    }),
                }, Json);
            }
            catch (Exception ex) when (ex is HostRejectedException or PinnedCertificateMismatchException
                                           or SocketException or IOException or InvalidOperationException)
            {
                return Results.Json(new { ok = false, error = ex.Message }, Json, statusCode: 502);
            }
        });

        app.MapPost("/api/import", async (ImportRequest request, CancellationToken ct) =>
        {
            var remote = FindRemote(node, new HostRefRequest(request.Address, request.Port));
            if (remote is null) return Results.Json(new { ok = false, error = "Unknown host." }, Json, statusCode: 404);

            try
            {
                var import = await node.ImportDeviceAsync(remote, request.DeviceId, ct);
                return Results.Json(new { ok = true, key = import.Key }, Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, Json, statusCode: 502);
            }
        });

        app.MapPost("/api/release", (ReleaseRequest request) =>
        {
            node.ReleaseImport(request.Key);
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPost("/api/clients/block", (BlockClientRequest request) =>
        {
            var client = node.Settings.KnownClients
                .FirstOrDefault(c => NodeIdentity.FingerprintsMatch(c.Fingerprint, request.Fingerprint));
            if (client is null) return Results.Json(new { ok = false, error = "Unknown client." }, Json, statusCode: 404);

            client.Blocked = request.Blocked;
            node.Settings.Save();
            return Results.Json(new { ok = true }, Json);
        });

        // Live updates: the page holds a socket open and redraws whenever the node says so.
        app.Map("/api/events", async (HttpContext context, CancellationToken ct) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var signal = new SemaphoreSlim(1, 1);
            void OnChanged() => signal.Release();

            node.StateChanged += OnChanged;
            node.Log.Entered += _ => { try { signal.Release(); } catch (SemaphoreFullException) { } };

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    await signal.WaitAsync(ct);

                    // Collapse bursts so a run of log lines is one redraw, not twenty.
                    await Task.Delay(120, ct);
                    while (signal.CurrentCount > 0) await signal.WaitAsync(ct);

                    var state = await BuildStateAsync(node, ct);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(state, Json);
                    await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // The page navigated away.
            }
            finally
            {
                node.StateChanged -= OnChanged;
                signal.Dispose();
            }
        });
    }

    private static RemoteHost? FindRemote(NodeService node, HostRefRequest request)
        => node.Settings.RemoteHosts.FirstOrDefault(h =>
            h.Address.Equals(request.Address, StringComparison.OrdinalIgnoreCase) && h.Port == request.Port);

    private static async Task<NodeStateView> BuildStateAsync(NodeService node, CancellationToken cancellationToken)
    {
        var devices = await node.ListLocalDevicesWithSharesAsync(cancellationToken);
        var connected = node.ConnectedHosts.ToHashSet();

        return new NodeStateView(
            node.Settings.HostName,
            UsbBackend.DescribeCurrentPlatform(),
            node.Backend.Name,
            node.Identity.Fingerprint,
            node.Settings.PairingToken,
            node.Host.IsRunning,
            node.Host.IsRunning ? node.Host.Port : node.Settings.LinkPort,
            node.Settings.LinkBindAddress,
            node.Settings.RequireClientCertificate,
            LocalAddresses(),
            devices.Select(d => ViewMapper.ToView(d.Device, d.Share)).ToArray(),
            node.Settings.RemoteHosts
                .Select(h => new RemoteHostView(
                    h.Name.Length > 0 ? h.Name : h.Address, h.Address, h.Port,
                    connected.Contains($"{h.Address}:{h.Port}"), h.AutoConnect, h.PinnedFingerprint))
                .ToArray(),
            node.Imports.Select(ViewMapper.ToView).ToArray(),
            node.Host.Sessions
                .Where(s => s.Authenticated)
                .Select(s => new SessionView(s.ClientName, s.ClientPlatform, s.ClientFingerprint,
                    s.RemoteEndPoint?.ToString(), s.ConnectedAt, s.ImportedDeviceIds))
                .ToArray(),
            node.Log.Recent(120).Select(ViewMapper.ToView).ToArray());
    }

    /// <summary>Addresses a client could reach this host on, so the pairing panel can show them.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Some sandboxes refuse to enumerate interfaces; the panel simply shows fewer options.
        }

        return addresses.Distinct().OrderBy(a => a, StringComparer.Ordinal).ToArray();
    }

    // ---- request bodies ----

    private sealed record ShareRequest(string DeviceId);
    private sealed record ShareEnabledRequest(string DeviceId, bool Enabled);
    private sealed record ShareClientsRequest(string DeviceId, List<string> AllowedClients);
    private sealed record AddHostRequest(string Address, int Port, string Token, string? Name, bool AutoConnect);
    private sealed record HostRefRequest(string Address, int Port);
    private sealed record ImportRequest(string Address, int Port, string DeviceId);
    private sealed record ReleaseRequest(string Key);
    private sealed record BlockClientRequest(string Fingerprint, bool Blocked);
}
