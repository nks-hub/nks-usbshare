using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Host;

/// <summary>One connected client, from the TLS handshake until the socket closes.</summary>
public sealed class HostSession
{
    private readonly FramedConnection _connection;
    private readonly SslStream _stream;
    private readonly NodeSettings _settings;
    private readonly IUsbBackend _backend;
    private readonly ActivityLog _log;

    private readonly ConcurrentDictionary<uint, OpenDevice> _openDevices = new();
    private uint _nextSessionId;

    public string ClientName { get; private set; } = "unknown";
    public string ClientPlatform { get; private set; } = "";
    public string? ClientFingerprint { get; }
    public IPEndPoint? RemoteEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public bool Authenticated { get; private set; }

    /// <summary>Raised once the client has proved itself and is named, not merely connected.</summary>
    public event Action? HandshakeCompleted;

    public IReadOnlyCollection<string> ImportedDeviceIds
        => _openDevices.Values.Select(d => d.Handle.Info.DeviceId).ToArray();

    private sealed record OpenDevice(IUsbDeviceHandle Handle, DeviceShare Share);

    public HostSession(
        SslStream stream,
        string? clientFingerprint,
        IPEndPoint? remoteEndPoint,
        NodeSettings settings,
        IUsbBackend backend,
        ActivityLog log)
    {
        _stream = stream;
        _connection = new FramedConnection(stream, ownsStream: false);
        ClientFingerprint = clientFingerprint;
        RemoteEndPoint = remoteEndPoint;
        _settings = settings;
        _backend = backend;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await HandshakeAsync(cancellationToken).ConfigureAwait(false)) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;
                await DispatchAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (ProtocolViolationExceptionEx ex)
        {
            _log.Warning("host", $"{ClientName} sent a malformed frame: {ex.Message}");
        }
        catch (IOException)
        {
            // A client that vanishes is routine, not an error worth shouting about.
        }
        finally
        {
            foreach (var device in _openDevices.Values)
            {
                _log.Info("host", $"{ClientName} released {device.Handle.Info.DisplayName}");
                device.Handle.Dispose();
            }
            _openDevices.Clear();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops the link straight away. The read loop unwinds and releases every device this client
    /// was holding, which is what makes blocking a computer take effect at once rather than only
    /// on its next connection.
    /// </summary>
    public async Task DisconnectAsync()
    {
        // The TLS stream has to go, not just the framing on top of it: the read loop is parked
        // inside a read on that stream and only closing it underneath wakes it up.
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> HandshakeAsync(CancellationToken cancellationToken)
    {
        var frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) return false;

        if (frame.Value.Type != MessageType.Hello)
        {
            await SendErrorAsync(frame.Value.Sequence, ProtocolError.MalformedRequest,
                "The first message must be Hello.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var hello = HelloMessage.Deserialize(frame.Value.Payload);
        ClientName = string.IsNullOrWhiteSpace(hello.ClientName) ? "unnamed client" : hello.ClientName;
        ClientPlatform = hello.ClientPlatform;

        if (!AccessToken.Verify(hello.Token, _settings.PairingToken))
        {
            _log.Warning("host", $"Rejected {ClientName} from {RemoteEndPoint}: wrong pairing token.");
            await SendErrorAsync(frame.Value.Sequence, ProtocolError.Unauthorized,
                "The pairing token is not valid for this host.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (ClientFingerprint is not null)
        {
            var known = _settings.TouchClient(ClientFingerprint, ClientName);
            if (known.Blocked)
            {
                _log.Warning("host", $"Rejected {ClientName}: the client is blocked.");
                await SendErrorAsync(frame.Value.Sequence, ProtocolError.AccessDenied,
                    "This client is blocked on the host.", cancellationToken).ConfigureAwait(false);
                return false;
            }
            _settings.Save();
        }

        Authenticated = true;
        _log.Info("host", $"{ClientName} ({hello.ClientPlatform}) connected from {RemoteEndPoint}.");
        HandshakeCompleted?.Invoke();

        await _connection.SendAsync(MessageType.HelloAck,
            new HelloAckMessage(_settings.HostName, UsbBackend.DescribeCurrentPlatform(), _backend.Name,
                FrameHeader.CurrentVersion).Serialize(),
            frame.Value.Sequence, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task DispatchAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case MessageType.Ping:
                await _connection.SendAsync(MessageType.Pong, [], frame.Sequence, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.ListDevicesRequest:
                await SendDeviceListAsync(frame.Sequence, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.OpenDeviceRequest:
                await OpenDeviceAsync(frame, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.CloseDeviceRequest:
                CloseDevice(SessionMessage.Deserialize(frame.Payload).SessionId);
                await _connection.SendAsync(MessageType.CloseDeviceResponse, [], frame.Sequence, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MessageType.UrbSubmit:
                // Deliberately not awaited: several URBs may be in flight at once, and the read
                // loop must stay free to accept the next one.
                _ = HandleUrbAsync(UrbSubmitMessage.Deserialize(frame.Payload), cancellationToken);
                break;

            case MessageType.UrbCancel:
                var cancel = UrbCancelMessage.Deserialize(frame.Payload);
                if (_openDevices.TryGetValue(cancel.SessionId, out var target)) target.Handle.TryCancel(cancel.UrbId);
                break;

            case MessageType.ResetDeviceRequest:
            case MessageType.SetConfigurationRequest:
            case MessageType.ClaimInterfaceRequest:
            case MessageType.ReleaseInterfaceRequest:
            case MessageType.SetAltSettingRequest:
            case MessageType.ClearHaltRequest:
                await HandleDeviceControlAsync(frame, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendErrorAsync(frame.Sequence, ProtocolError.MalformedRequest,
                    $"Unexpected message type {frame.Type}.", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendDeviceListAsync(uint sequence, CancellationToken cancellationToken)
    {
        var devices = await _backend.EnumerateAsync(cancellationToken).ConfigureAwait(false);

        // Only devices that are shared and that this client is allowed to see.
        var visible = devices
            .Select(device => (Device: device, Share: _settings.FindShare(device.DeviceId, device.HardwareKey, device.SerialNumber)))
            .Where(pair => pair.Share is { Enabled: true } && pair.Share.AllowsClient(ClientFingerprint))
            .Select(pair => pair.Device)
            .ToArray();

        await _connection.SendAsync(MessageType.ListDevicesResponse,
            new DeviceListMessage(visible).Serialize(), sequence, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenDeviceAsync(Frame frame, CancellationToken cancellationToken)
    {
        var request = OpenDeviceRequestMessage.Deserialize(frame.Payload);

        var devices = await _backend.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.DeviceId == request.DeviceId);
        if (device is null)
        {
            await SendErrorAsync(frame.Sequence, ProtocolError.UnknownDevice,
                $"No device with id '{request.DeviceId}' is attached.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var share = _settings.FindShare(device.DeviceId, device.HardwareKey, device.SerialNumber);
        if (share is not { Enabled: true } || !share.AllowsClient(ClientFingerprint))
        {
            _log.Warning("host", $"{ClientName} was refused {device.DisplayName}: not shared with this client.");
            await SendErrorAsync(frame.Sequence, ProtocolError.AccessDenied,
                "This device is not shared with you.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_openDevices.Values.Any(d => d.Handle.Info.DeviceId == device.DeviceId))
        {
            await SendErrorAsync(frame.Sequence, ProtocolError.DeviceBusy,
                "You already have this device open.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var handle = await _backend.OpenAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
            var sessionId = Interlocked.Increment(ref _nextSessionId);
            _openDevices[sessionId] = new OpenDevice(handle, share);

            _log.Info("host", $"{ClientName} imported {device.DisplayName} ({device.HardwareKey}).");

            await _connection.SendAsync(MessageType.OpenDeviceResponse,
                new OpenDeviceResponseMessage(sessionId, handle.Info).Serialize(), frame.Sequence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UsbBackendException ex)
        {
            _log.Error("host", $"{ClientName} could not open {device.DisplayName}: {ex.Message}");
            await SendErrorAsync(frame.Sequence, ProtocolError.BackendFailure, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void CloseDevice(uint sessionId)
    {
        if (!_openDevices.TryRemove(sessionId, out var device)) return;
        _log.Info("host", $"{ClientName} released {device.Handle.Info.DisplayName}.");
        device.Handle.Dispose();
    }

    private async Task HandleUrbAsync(UrbSubmitMessage message, CancellationToken cancellationToken)
    {
        UrbResult result;

        if (!_openDevices.TryGetValue(message.SessionId, out var device))
        {
            result = new UrbResult
            {
                UrbId = message.Request.UrbId,
                Status = UrbStatus.DeviceGone,
                Detail = $"Session {message.SessionId} is not open.",
            };
        }
        else
        {
            try
            {
                result = await device.Handle.SubmitAsync(message.Request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new UrbResult { UrbId = message.Request.UrbId, Status = UrbStatus.IoError, Detail = ex.Message };
            }
        }

        try
        {
            await _connection.SendAsync(MessageType.UrbComplete,
                new UrbCompleteMessage(message.SessionId, result).Serialize(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away while the transfer was running; nothing left to report to.
        }
    }

    private async Task HandleDeviceControlAsync(Frame frame, CancellationToken cancellationToken)
    {
        var request = DeviceControlMessage.Deserialize(frame.Payload);

        if (!_openDevices.TryGetValue(request.SessionId, out var device))
        {
            await SendErrorAsync(frame.Sequence, ProtocolError.UnknownDevice,
                $"Session {request.SessionId} is not open.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var task = frame.Type switch
            {
                MessageType.ResetDeviceRequest => device.Handle.ResetAsync(cancellationToken),
                MessageType.SetConfigurationRequest => device.Handle.SetConfigurationAsync(request.Arg0, cancellationToken),
                MessageType.ClaimInterfaceRequest => device.Handle.ClaimInterfaceAsync(request.Arg0, cancellationToken),
                MessageType.ReleaseInterfaceRequest => device.Handle.ReleaseInterfaceAsync(request.Arg0, cancellationToken),
                MessageType.SetAltSettingRequest => device.Handle.SetAltSettingAsync(request.Arg0, request.Arg1, cancellationToken),
                MessageType.ClearHaltRequest => device.Handle.ClearHaltAsync(request.Arg0, cancellationToken),
                _ => Task.CompletedTask,
            };

            await task.ConfigureAwait(false);
            await _connection.SendAsync(MessageType.SimpleResponse,
                new SimpleResponseMessage(true, "").Serialize(), frame.Sequence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _connection.SendAsync(MessageType.SimpleResponse,
                new SimpleResponseMessage(false, ex.Message).Serialize(), frame.Sequence, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task SendErrorAsync(uint sequence, ProtocolError code, string message, CancellationToken cancellationToken)
        => _connection.SendAsync(MessageType.Error, new ErrorMessage(code, message).Serialize(), sequence, cancellationToken);
}
