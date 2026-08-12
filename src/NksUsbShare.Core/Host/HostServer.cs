using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Host;

/// <summary>
/// Accepts encrypted connections and hands out the USB devices this node has chosen to share.
/// Every connection is wrapped in TLS before a single protocol byte is read.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly NodeSettings _settings;
    private readonly NodeIdentity _identity;
    private readonly IUsbBackend _backend;
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<HostSession, byte> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;

    public HostServer(NodeSettings settings, NodeIdentity identity, IUsbBackend backend, ActivityLog log)
    {
        _settings = settings;
        _identity = identity;
        _backend = backend;
        _log = log;
    }

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public IReadOnlyCollection<HostSession> Sessions => _sessions.Keys.ToArray();

    /// <summary>
    /// Raised when a client finishes its handshake or its link ends, so a window can show the
    /// computer arriving instead of waiting for someone to press Refresh.
    /// </summary>
    public event Action? SessionsChanged;

    /// <summary>Drops every live session belonging to one client. Returns how many were dropped.</summary>
    public async Task<int> DisconnectClientAsync(string fingerprint)
    {
        var matching = Sessions
            .Where(s => s.ClientFingerprint is { } f && NodeIdentity.FingerprintsMatch(f, fingerprint))
            .ToArray();

        foreach (var session in matching)
            await session.DisconnectAsync().ConfigureAwait(false);

        return matching.Length;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null) throw new InvalidOperationException("The host is already running.");

        var address = IPAddress.TryParse(_settings.LinkBindAddress, out var parsed) ? parsed : IPAddress.Any;
        _listener = new TcpListener(address, _settings.LinkPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);

        _log.Info("host", $"Listening on {address}:{Port} as '{_settings.HostName}'.");
        _log.Info("host", $"Identity fingerprint {_identity.Fingerprint}.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        HostSession? session = null;

        try
        {
            client.NoDelay = true;

            var allowed = _settings.RequireClientCertificate
                ? _settings.KnownClients.Where(c => !c.Blocked).Select(c => c.Fingerprint).ToHashSet()
                : new HashSet<string>();

            // Do not let a stalled handshake hold a socket open forever.
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            var ssl = await SecureChannel.AuthenticateServerAsync(
                client.GetStream(), _identity.Certificate, allowed, handshakeTimeout.Token).ConfigureAwait(false);

            string? clientFingerprint = null;
            if (ssl.RemoteCertificate is { } certificate)
                clientFingerprint = NodeIdentity.ComputeFingerprint(
                    certificate as System.Security.Cryptography.X509Certificates.X509Certificate2
                    ?? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));

            session = new HostSession(ssl, clientFingerprint, remoteEndPoint, _settings, _backend, _log);
            _sessions[session] = 0;

            try
            {
                session.HandshakeCompleted += () => SessionsChanged?.Invoke();
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (AuthenticationException ex)
        {
            _log.Warning("host", $"TLS handshake with {remoteEndPoint} failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _log.Warning("host", $"{remoteEndPoint} did not complete its handshake in time.");
        }
        catch (IOException) { /* the peer disappeared */ }
        catch (Exception ex)
        {
            _log.Error("host", $"Connection from {remoteEndPoint} failed: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session, out _);
                if (session.Authenticated) _log.Info("host", $"{session.ClientName} disconnected.");
                SessionsChanged?.Invoke();
            }
            client.Dispose();
        }
    }

    public async Task StopAsync()
    {
        if (_listener is null) return;

        if (_shutdown is not null) await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown?.Dispose();
        _shutdown = null;
        _log.Info("host", "Stopped listening.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
