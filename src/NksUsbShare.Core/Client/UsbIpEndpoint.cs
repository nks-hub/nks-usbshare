using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Client;

/// <summary>
/// Speaks the USB/IP protocol on the loopback interface so a virtual USB host controller can
/// attach to a device that actually lives on another machine.
///
/// The operating system's virtual controller only knows how to talk USB/IP over a socket. Rather
/// than exposing that plaintext protocol to the network, this endpoint binds to loopback only:
/// the controller connects locally, and the network hop is carried by the encrypted link this
/// project defines. Nothing sensitive ever leaves the machine unprotected.
///
/// The wire format is the one the Linux kernel documents, and every field is big endian.
/// </summary>
public sealed class UsbIpEndpoint : IAsyncDisposable
{
    private const ushort ProtocolVersion = 0x0111;

    private const ushort OpRequestDeviceList = 0x8005;
    private const ushort OpReplyDeviceList = 0x0005;
    private const ushort OpRequestImport = 0x8003;
    private const ushort OpReplyImport = 0x0003;

    private const uint CommandSubmit = 1;
    private const uint CommandUnlink = 2;
    private const uint ReplySubmit = 3;
    private const uint ReplyUnlink = 4;

    private const int HeaderSize = 48;
    private const int DeviceRecordSize = 312;

    /// <summary>Linux errno values, negated, as the protocol expects.</summary>
    private const int Success = 0;
    private const int Stall = -32;
    private const int NoDevice = -19;
    private const int Timeout = -110;
    private const int Cancelled = -104;
    private const int Overflow = -75;
    private const int ProtocolError = -71;
    private const int InvalidArgument = -22;

    private readonly ActivityLog _log;
    private readonly List<Export> _exports = [];
    private readonly Lock _exportsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;
    private int _nextBus = 1;

    public UsbIpEndpoint(ActivityLog log) => _log = log;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;

    private sealed record Export(string BusId, int BusNumber, int DeviceNumber, RemoteUsbDevice Device);

    /// <summary>Makes an imported device available for the virtual controller to attach to.</summary>
    public string Publish(RemoteUsbDevice device)
    {
        lock (_exportsLock)
        {
            var existing = _exports.FirstOrDefault(e => e.Device == device);
            if (existing is not null) return existing.BusId;

            var bus = _nextBus++;
            var export = new Export($"{bus}-1", bus, 1, device);
            _exports.Add(export);
            _log.Info("usbip", $"{device.Info.DisplayName} is available to the local controller as bus id {export.BusId}.");
            return export.BusId;
        }
    }

    public void Withdraw(RemoteUsbDevice device)
    {
        lock (_exportsLock)
        {
            var export = _exports.FirstOrDefault(e => e.Device == device);
            if (export is null) return;
            _exports.Remove(export);
            _log.Info("usbip", $"{device.Info.DisplayName} withdrawn from bus id {export.BusId}.");
        }
    }

    public Task StartAsync(int port, CancellationToken cancellationToken)
    {
        if (_listener is not null) return Task.CompletedTask;

        // Loopback only. This side of the link is deliberately not reachable from the network.
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);

        _log.Info("usbip", $"Local controller endpoint listening on 127.0.0.1:{Port}.");
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();

            // The operation phase: either a listing, or an import that upgrades the connection
            // into a stream of URBs for one device.
            var header = new byte[8];
            if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)) return;

            var version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            var code = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));

            if (version != ProtocolVersion)
            {
                _log.Warning("usbip", $"The local controller speaks protocol {version:x4}, expected {ProtocolVersion:x4}.");
                return;
            }

            switch (code)
            {
                case OpRequestDeviceList:
                    await SendDeviceListAsync(stream, cancellationToken).ConfigureAwait(false);
                    break;

                case OpRequestImport:
                    await ImportAsync(stream, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    _log.Warning("usbip", $"Unexpected operation code 0x{code:x4} from the local controller.");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The controller detached or the process is shutting down.
        }
        catch (Exception ex)
        {
            _log.Error("usbip", $"The local controller session failed: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task SendDeviceListAsync(Stream stream, CancellationToken cancellationToken)
    {
        // A listing request is the eight byte common header and nothing more; the status field
        // is already part of it. Reading further here would block waiting for bytes that the
        // controller never sends.
        Export[] exports;
        lock (_exportsLock) exports = _exports.ToArray();

        var payload = new List<byte>();
        payload.AddRange(OperationHeader(OpReplyDeviceList, 0));

        var count = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, (uint)exports.Length);
        payload.AddRange(count);

        foreach (var export in exports)
        {
            payload.AddRange(DeviceRecord(export));

            // Each interface is reported as class, subclass, protocol and one padding byte.
            foreach (var iface in InterfacesOf(export.Device))
                payload.AddRange([iface.InterfaceClass, iface.InterfaceSubClass, iface.InterfaceProtocol, 0]);
        }

        await stream.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportAsync(Stream stream, CancellationToken cancellationToken)
    {
        // The common header has already been read; what follows is the 32 byte bus id.
        var rest = new byte[32];
        if (!await ReadExactAsync(stream, rest, cancellationToken).ConfigureAwait(false)) return;

        var busId = Encoding.ASCII.GetString(rest, 0, 32).TrimEnd('\0');

        Export? export;
        lock (_exportsLock) export = _exports.FirstOrDefault(e => e.BusId == busId);

        if (export is null)
        {
            _log.Warning("usbip", $"The local controller asked for bus id '{busId}', which is not published.");
            await stream.WriteAsync(OperationHeader(OpReplyImport, 1), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var reply = new List<byte>();
        reply.AddRange(OperationHeader(OpReplyImport, 0));
        reply.AddRange(DeviceRecord(export));

        await stream.WriteAsync(reply.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("usbip", $"The local controller attached {export.Device.Info.DisplayName} on bus id {busId}.");
        await PumpUrbsAsync(stream, export, cancellationToken).ConfigureAwait(false);
        _log.Info("usbip", $"The local controller detached {export.Device.Info.DisplayName}.");
    }

    /// <summary>
    /// Carries URBs until the controller closes the socket. Requests are answered concurrently,
    /// because the controller submits many at once and expects them to overlap.
    /// </summary>
    private async Task PumpUrbsAsync(Stream stream, Export export, CancellationToken cancellationToken)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<uint, ulong>();
        var pending = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = new byte[HeaderSize];
                if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)) break;

                var command = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                var sequence = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
                var direction = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                var endpointNumber = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));

                if (command == CommandUnlink)
                {
                    var victim = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
                    if (inFlight.TryGetValue(victim, out var urbId)) export.Device.TryCancel(urbId);
                    await WriteUnlinkReplyAsync(stream, writeLock, sequence, header, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (command != CommandSubmit)
                {
                    _log.Warning("usbip", $"Unexpected command {command} from the local controller.");
                    break;
                }

                var transferLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24, 4));
                var packetCount = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32, 4));
                var setup = header.AsSpan(40, 8).ToArray();

                if (transferLength is < 0 or > 16 * 1024 * 1024)
                {
                    _log.Warning("usbip", $"Refusing a transfer of {transferLength} bytes.");
                    break;
                }

                // Isochronous descriptors follow the header, and outbound payloads follow those.
                var isoLengths = Array.Empty<int>();
                if (packetCount > 0)
                {
                    var descriptors = new byte[packetCount * 16];
                    if (!await ReadExactAsync(stream, descriptors, cancellationToken).ConfigureAwait(false)) break;

                    isoLengths = new int[packetCount];
                    for (var i = 0; i < packetCount; i++)
                        isoLengths[i] = BinaryPrimitives.ReadInt32BigEndian(descriptors.AsSpan(i * 16 + 4, 4));
                }

                var isIn = direction == 1;
                var outbound = Array.Empty<byte>();
                if (!isIn && transferLength > 0)
                {
                    outbound = new byte[transferLength];
                    if (!await ReadExactAsync(stream, outbound, cancellationToken).ConfigureAwait(false)) break;
                }

                var request = BuildRequest(export.Device, (byte)endpointNumber, isIn, setup, outbound,
                    transferLength, isoLengths);

                inFlight[sequence] = request.UrbId;

                pending.Add(CompleteAsync(stream, writeLock, export, sequence, header, request, isIn, inFlight, cancellationToken));
                pending.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
            writeLock.Dispose();
        }
    }

    private UrbRequest BuildRequest(
        RemoteUsbDevice device, byte endpointNumber, bool isIn, byte[] setup, byte[] outbound,
        int transferLength, int[] isoLengths)
    {
        var address = (byte)(endpointNumber | (isIn ? 0x80 : 0x00));

        // Endpoint zero is always the control pipe; anything else takes its type from the
        // device's own descriptors, falling back to bulk when the endpoint is unknown.
        var transferType = endpointNumber == 0
            ? UsbTransferType.Control
            : device.Info.Interfaces
                  .SelectMany(i => i.Endpoints)
                  .FirstOrDefault(e => e.Address == address)?.TransferType
              ?? UsbTransferType.Bulk;

        return new UrbRequest
        {
            UrbId = NextUrbId(device),
            Endpoint = address,
            TransferType = transferType,
            Setup = setup,
            Data = outbound,
            ExpectedLength = isIn ? transferLength : 0,
            IsoPacketLengths = isoLengths,
            TimeoutMs = 0, // the controller decides when to give up and unlinks
        };
    }

    private static ulong NextUrbId(RemoteUsbDevice device) => device.NextUrbId();

    private async Task CompleteAsync(
        Stream stream, SemaphoreSlim writeLock, Export export, uint sequence, byte[] requestHeader,
        UrbRequest request, bool isIn,
        System.Collections.Concurrent.ConcurrentDictionary<uint, ulong> inFlight,
        CancellationToken cancellationToken)
    {
        UrbResult result;
        try
        {
            result = await export.Device.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new UrbResult { UrbId = request.UrbId, Status = UrbStatus.IoError, Detail = ex.Message };
        }
        finally
        {
            inFlight.TryRemove(sequence, out _);
        }

        var reply = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(0, 4), ReplySubmit);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), sequence);
        // devid, direction and endpoint are echoed back unchanged.
        requestHeader.AsSpan(8, 12).CopyTo(reply.AsSpan(8, 12));
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(20, 4), ToErrno(result.Status));
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(24, 4), result.ActualLength);
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(28, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(32, 4), result.IsoResults.Length);
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(36, 4), result.IsoResults.Count(r => r.Status != UrbStatus.Ok));

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

            if (isIn && result.Data.Length > 0)
                await stream.WriteAsync(result.Data, cancellationToken).ConfigureAwait(false);

            if (result.IsoResults.Length > 0)
            {
                var descriptors = new byte[result.IsoResults.Length * 16];
                var offset = 0;
                for (var i = 0; i < result.IsoResults.Length; i++)
                {
                    var span = descriptors.AsSpan(i * 16);
                    BinaryPrimitives.WriteInt32BigEndian(span[..4], offset);
                    BinaryPrimitives.WriteInt32BigEndian(span.Slice(4, 4), request.IsoPacketLengths.ElementAtOrDefault(i));
                    BinaryPrimitives.WriteInt32BigEndian(span.Slice(8, 4), result.IsoResults[i].ActualLength);
                    BinaryPrimitives.WriteInt32BigEndian(span.Slice(12, 4), ToErrno(result.IsoResults[i].Status));
                    offset += request.IsoPacketLengths.ElementAtOrDefault(i);
                }
                await stream.WriteAsync(descriptors, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The controller detached while this transfer was running.
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task WriteUnlinkReplyAsync(
        Stream stream, SemaphoreSlim writeLock, uint sequence, byte[] requestHeader, CancellationToken cancellationToken)
    {
        var reply = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(0, 4), ReplyUnlink);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), sequence);
        requestHeader.AsSpan(8, 12).CopyTo(reply.AsSpan(8, 12));
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(20, 4), Cancelled);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            writeLock.Release();
        }
    }

    // ---- encoding helpers ----

    private static byte[] OperationHeader(ushort code, uint status)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), code);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), status);
        return header;
    }

    private static IReadOnlyList<UsbInterfaceInfo> InterfacesOf(RemoteUsbDevice device)
    {
        // Only the first setting of each interface is reported, which is what the protocol expects.
        return device.Info.Interfaces
            .GroupBy(i => i.Number)
            .Select(g => g.First())
            .ToArray();
    }

    private static byte[] DeviceRecord(Export export)
    {
        var info = export.Device.Info;
        var record = new byte[DeviceRecordSize];

        var path = Encoding.ASCII.GetBytes($"/sys/devices/nks/{export.BusId}");
        path.AsSpan(0, Math.Min(path.Length, 255)).CopyTo(record.AsSpan(0, 255));

        var busId = Encoding.ASCII.GetBytes(export.BusId);
        busId.AsSpan(0, Math.Min(busId.Length, 31)).CopyTo(record.AsSpan(256, 31));

        var span = record.AsSpan(288);
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], (uint)export.BusNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), (uint)export.DeviceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8, 4), ToUsbIpSpeed(info.Speed));
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(12, 2), info.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(14, 2), info.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(16, 2), info.DeviceRelease);

        span[18] = info.DeviceClass;
        span[19] = info.DeviceSubClass;
        span[20] = info.DeviceProtocol;
        span[21] = info.ConfigurationValue == 0 ? (byte)1 : info.ConfigurationValue;
        span[22] = 1; // one configuration is exposed
        span[23] = (byte)InterfacesOf(export.Device).Count;

        return record;
    }

    /// <summary>Maps onto the kernel's usb_device_speed enumeration.</summary>
    private static uint ToUsbIpSpeed(UsbSpeed speed) => speed switch
    {
        UsbSpeed.Low => 1,
        UsbSpeed.Full => 2,
        UsbSpeed.High => 3,
        UsbSpeed.Super => 5,
        UsbSpeed.SuperPlus => 6,
        _ => 0,
    };

    private static int ToErrno(UrbStatus status) => status switch
    {
        UrbStatus.Ok or UrbStatus.ShortPacket => Success,
        UrbStatus.Stalled => Stall,
        UrbStatus.Timeout => Timeout,
        UrbStatus.Cancelled => Cancelled,
        UrbStatus.DeviceGone => NoDevice,
        UrbStatus.Overflow => Overflow,
        UrbStatus.NotSupported => InvalidArgument,
        _ => ProtocolError,
    };

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown is not null) await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown?.Dispose();
        _shutdown = null;
    }
}
