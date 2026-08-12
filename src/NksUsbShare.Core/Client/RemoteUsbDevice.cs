using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Client;

/// <summary>
/// A device that physically sits on another machine, presented with the same surface as a local
/// one. Applications on the importing side can drive it directly through this object; on Linux it
/// is also what the virtual host controller attachment feeds from.
/// </summary>
public sealed class RemoteUsbDevice : IUsbDeviceHandle
{
    private readonly UsbShareClient _client;
    private int _disposed;

    public uint SessionId { get; }
    public UsbDeviceInfo Info { get; }

    internal RemoteUsbDevice(UsbShareClient client, uint sessionId, UsbDeviceInfo info)
    {
        _client = client;
        SessionId = sessionId;
        Info = info;
    }

    public Task<UrbResult> SubmitAsync(UrbRequest request, CancellationToken cancellationToken)
        => _client.SubmitUrbAsync(SessionId, request, cancellationToken);

    /// <summary>
    /// Cancellation travels to the host as its own message, so this only reports that the request
    /// was sent. Whether the transfer was still abortable is decided on the far side.
    /// </summary>
    public bool TryCancel(ulong urbId)
    {
        _ = _client.CancelUrbAsync(SessionId, urbId);
        return true;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.ResetDeviceRequest, SessionId, 0, 0, cancellationToken);

    public Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.SetConfigurationRequest, SessionId, configurationValue, 0, cancellationToken);

    public Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.ClaimInterfaceRequest, SessionId, interfaceNumber, 0, cancellationToken);

    public Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.ReleaseInterfaceRequest, SessionId, interfaceNumber, 0, cancellationToken);

    public Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.SetAltSettingRequest, SessionId, interfaceNumber, alternateSetting, cancellationToken);

    public Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken)
        => _client.DeviceControlAsync(MessageType.ClearHaltRequest, SessionId, endpointAddress, 0, cancellationToken);

    // ---- convenience wrappers, so callers do not have to hand-build URBs ----

    /// <summary>Issues a control transfer and returns what came back (empty for host-to-device).</summary>
    public async Task<byte[]> ControlTransferAsync(
        byte requestType, byte request, ushort value, ushort index,
        byte[]? data = null, ushort expectedLength = 0,
        uint timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        var isIn = (requestType & 0x80) != 0;
        var length = isIn ? expectedLength : (ushort)(data?.Length ?? 0);

        var setup = new byte[8];
        setup[0] = requestType;
        setup[1] = request;
        setup[2] = (byte)value;
        setup[3] = (byte)(value >> 8);
        setup[4] = (byte)index;
        setup[5] = (byte)(index >> 8);
        setup[6] = (byte)length;
        setup[7] = (byte)(length >> 8);

        var result = await SubmitAsync(new UrbRequest
        {
            UrbId = _client.NextUrbId(),
            TransferType = UsbTransferType.Control,
            Setup = setup,
            Data = data ?? [],
            ExpectedLength = length,
            TimeoutMs = timeoutMs,
        }, cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(result, $"control transfer 0x{requestType:x2}/0x{request:x2}");
        return result.Data;
    }

    public async Task<byte[]> ReadAsync(
        byte endpoint, int length, uint timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        var endpointInfo = FindEndpoint(endpoint);
        var result = await SubmitAsync(new UrbRequest
        {
            UrbId = _client.NextUrbId(),
            Endpoint = endpoint,
            TransferType = endpointInfo?.TransferType ?? UsbTransferType.Bulk,
            ExpectedLength = length,
            TimeoutMs = timeoutMs,
        }, cancellationToken).ConfigureAwait(false);

        // A short read is a normal outcome on IN endpoints, not a failure.
        if (result.Status is not (UrbStatus.Ok or UrbStatus.ShortPacket))
            ThrowIfFailed(result, $"read from endpoint 0x{endpoint:x2}");

        return result.Data;
    }

    public async Task<int> WriteAsync(
        byte endpoint, byte[] data, uint timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        var endpointInfo = FindEndpoint(endpoint);
        var result = await SubmitAsync(new UrbRequest
        {
            UrbId = _client.NextUrbId(),
            Endpoint = endpoint,
            TransferType = endpointInfo?.TransferType ?? UsbTransferType.Bulk,
            Data = data,
            TimeoutMs = timeoutMs,
        }, cancellationToken).ConfigureAwait(false);

        ThrowIfFailed(result, $"write to endpoint 0x{endpoint:x2}");
        return result.ActualLength;
    }

    /// <summary>Reads a standard descriptor straight from the device.</summary>
    public Task<byte[]> GetDescriptorAsync(
        byte descriptorType, byte descriptorIndex, ushort languageId = 0, ushort length = 255,
        CancellationToken cancellationToken = default)
        => ControlTransferAsync(0x80, 0x06, (ushort)((descriptorType << 8) | descriptorIndex), languageId,
            null, length, cancellationToken: cancellationToken);

    private UsbEndpointInfo? FindEndpoint(byte address)
        => Info.Interfaces.SelectMany(i => i.Endpoints).FirstOrDefault(e => e.Address == address);

    private static void ThrowIfFailed(UrbResult result, string what)
    {
        if (result.Status == UrbStatus.Ok) return;
        throw new UsbBackendException($"{what} failed: {result.Status}" +
                                      (result.Detail is null ? "" : $" ({result.Detail})"));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = _client.CloseDeviceAsync(SessionId, CancellationToken.None);
    }
}
