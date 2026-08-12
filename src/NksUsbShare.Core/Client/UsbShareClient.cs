using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Client;

/// <summary>Raised when the host answers a request with an <see cref="MessageType.Error"/> frame.</summary>
public sealed class HostRejectedException(ProtocolError code, string message) : Exception(message)
{
    public ProtocolError Code { get; } = code;
}

/// <summary>
/// The importing side of a link. Owns one TLS connection to a host, correlates replies to
/// requests, and hands out <see cref="RemoteUsbDevice"/> objects for the devices it opens.
/// </summary>
public sealed class UsbShareClient : IAsyncDisposable
{
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Frame>> _pendingRequests = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<UrbResult>> _pendingUrbs = new();
    private readonly ConcurrentDictionary<uint, RemoteUsbDevice> _devices = new();

    private TcpClient? _socket;
    private FramedConnection? _connection;
    private Task? _readLoop;
    private CancellationTokenSource? _shutdown;
    private ulong _nextUrbId;

    public UsbShareClient(ActivityLog log) => _log = log;

    public bool IsConnected => _connection is not null;
    public string HostName { get; private set; } = "";
    public string HostPlatform { get; private set; } = "";
    public string HostBackend { get; private set; } = "";

    /// <summary>The fingerprint the host actually presented, for the caller to pin.</summary>
    public string HostFingerprint { get; private set; } = "";

    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(
        string address,
        int port,
        string token,
        string? pinnedFingerprint,
        string clientName,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        if (_connection is not null) throw new InvalidOperationException("Already connected.");

        _socket = new TcpClient { NoDelay = true };
        await _socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

        var (ssl, fingerprint) = await SecureChannel.AuthenticateClientAsync(
            _socket.GetStream(), address, pinnedFingerprint, clientCertificate, cancellationToken).ConfigureAwait(false);

        HostFingerprint = fingerprint;
        _connection = new FramedConnection(ssl);
        _shutdown = new CancellationTokenSource();
        _readLoop = ReadLoopAsync(_shutdown.Token);

        var reply = await RequestAsync(MessageType.Hello,
            new HelloMessage(clientName, RuntimeInformation.OSDescription, token).Serialize(),
            cancellationToken).ConfigureAwait(false);

        var ack = HelloAckMessage.Deserialize(reply.Payload);
        HostName = ack.HostName;
        HostPlatform = ack.HostPlatform;
        HostBackend = ack.BackendName;

        _log.Info("client", $"Connected to '{ack.HostName}' ({ack.HostPlatform}, {ack.BackendName}), fingerprint {fingerprint}.");
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _connection!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;

                switch (frame.Value.Type)
                {
                    case MessageType.UrbComplete:
                        var completion = UrbCompleteMessage.Deserialize(frame.Value.Payload);
                        if (_pendingUrbs.TryRemove(completion.Result.UrbId, out var urbWaiter))
                            urbWaiter.TrySetResult(completion.Result);
                        break;

                    default:
                        if (_pendingRequests.TryRemove(frame.Value.Sequence, out var waiter))
                            waiter.TrySetResult(frame.Value);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            FailPending(failure ?? new IOException("The connection to the host closed."));
            Disconnected?.Invoke(failure);
        }
    }

    private void FailPending(Exception reason)
    {
        foreach (var key in _pendingRequests.Keys.ToArray())
            if (_pendingRequests.TryRemove(key, out var waiter)) waiter.TrySetException(reason);

        foreach (var key in _pendingUrbs.Keys.ToArray())
            if (_pendingUrbs.TryRemove(key, out var waiter))
                waiter.TrySetResult(new UrbResult { UrbId = key, Status = UrbStatus.DeviceGone, Detail = reason.Message });
    }

    /// <summary>Sends a request and waits for the reply carrying the same sequence number.</summary>
    private async Task<Frame> RequestAsync(MessageType type, byte[] payload, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("Not connected.");
        var sequence = connection.NextSequence();
        var completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[sequence] = completion;

        try
        {
            await connection.SendAsync(type, payload, sequence, cancellationToken).ConfigureAwait(false);
            await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var frame = await completion.Task.ConfigureAwait(false);

            if (frame.Type == MessageType.Error)
            {
                var error = ErrorMessage.Deserialize(frame.Payload);
                throw new HostRejectedException(error.Code, error.Message);
            }

            return frame;
        }
        catch
        {
            _pendingRequests.TryRemove(sequence, out _);
            throw;
        }
    }

    public async Task<IReadOnlyList<UsbDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(MessageType.ListDevicesRequest, [], cancellationToken).ConfigureAwait(false);
        return DeviceListMessage.Deserialize(reply.Payload).Devices;
    }

    public async Task<RemoteUsbDevice> OpenDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(MessageType.OpenDeviceRequest,
            new OpenDeviceRequestMessage(deviceId).Serialize(), cancellationToken).ConfigureAwait(false);

        var response = OpenDeviceResponseMessage.Deserialize(reply.Payload);
        var device = new RemoteUsbDevice(this, response.SessionId, response.Device);
        _devices[response.SessionId] = device;

        _log.Info("client", $"Opened {response.Device.DisplayName} from '{HostName}'.");
        return device;
    }

    internal async Task CloseDeviceAsync(uint sessionId, CancellationToken cancellationToken)
    {
        _devices.TryRemove(sessionId, out _);
        if (_connection is null) return;

        try
        {
            await RequestAsync(MessageType.CloseDeviceRequest, new SessionMessage(sessionId).Serialize(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HostRejectedException)
        {
            // Closing a device on a link that has already gone is not an error worth surfacing.
        }
    }

    internal async Task<UrbResult> SubmitUrbAsync(uint sessionId, UrbRequest request, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("Not connected.");

        var completion = new TaskCompletionSource<UrbResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingUrbs[request.UrbId] = completion;

        try
        {
            await connection.SendAsync(MessageType.UrbSubmit,
                new UrbSubmitMessage(sessionId, request).Serialize(), cancellationToken).ConfigureAwait(false);

            await using var registration = cancellationToken.Register(() =>
            {
                _ = CancelUrbAsync(sessionId, request.UrbId);
                completion.TrySetCanceled(cancellationToken);
            });

            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pendingUrbs.TryRemove(request.UrbId, out _);
            throw;
        }
    }

    internal async Task CancelUrbAsync(uint sessionId, ulong urbId)
    {
        try
        {
            if (_connection is { } connection)
                await connection.SendAsync(MessageType.UrbCancel,
                    new UrbCancelMessage(sessionId, urbId).Serialize(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing to cancel on a dead link.
        }
    }

    internal async Task DeviceControlAsync(
        MessageType type, uint sessionId, byte arg0, byte arg1, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(type, new DeviceControlMessage(sessionId, arg0, arg1).Serialize(), cancellationToken)
            .ConfigureAwait(false);

        var response = SimpleResponseMessage.Deserialize(reply.Payload);
        if (!response.Success) throw new UsbBackendException(response.Message);
    }

    internal ulong NextUrbId() => Interlocked.Increment(ref _nextUrbId);

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await RequestAsync(MessageType.Ping, [], cancellationToken).ConfigureAwait(false);
        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown is not null) await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _socket?.Dispose();
        _socket = null;
        _shutdown?.Dispose();
        _shutdown = null;
    }
}
