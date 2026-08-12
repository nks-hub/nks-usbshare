using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace NksUsbShare.Core.Client;

/// <summary>
/// The importing half of the USB/IP operation phase.
///
/// A kernel controller that takes a ready-made socket expects the import to have been negotiated
/// already, so this performs that negotiation and hands back what the kernel needs to identify
/// the device. All fields are big endian.
/// </summary>
public static class UsbIpHandshake
{
    private const ushort ProtocolVersion = 0x0111;
    private const ushort RequestImport = 0x8003;
    private const ushort ReplyImport = 0x0003;
    private const int DeviceRecordSize = 312;

    public sealed record ImportedDevice(uint DevId, uint Speed, ushort VendorId, ushort ProductId);

    /// <summary>
    /// Asks the peer for a bus id and returns what it answered, or null when it refused.
    /// The socket is left open and positioned at the start of the transfer stream.
    /// </summary>
    public static async Task<ImportedDevice?> ImportAsync(Socket socket, string busId, CancellationToken cancellationToken)
    {
        var request = new byte[8 + 32];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), RequestImport);

        var busIdBytes = Encoding.ASCII.GetBytes(busId);
        busIdBytes.AsSpan(0, Math.Min(busIdBytes.Length, 32)).CopyTo(request.AsSpan(8));

        await socket.SendAsync(request, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var header = new byte[8];
        if (!await ReceiveExactAsync(socket, header, cancellationToken).ConfigureAwait(false)) return null;

        var code = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var status = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        if (code != ReplyImport || status != 0) return null;

        var record = new byte[DeviceRecordSize];
        if (!await ReceiveExactAsync(socket, record, cancellationToken).ConfigureAwait(false)) return null;

        var span = record.AsSpan(288);
        var busNumber = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        var deviceNumber = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        var speed = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));

        return new ImportedDevice(
            DevId: (busNumber << 16) | deviceNumber,
            Speed: speed,
            VendorId: BinaryPrimitives.ReadUInt16BigEndian(span.Slice(12, 2)),
            ProductId: BinaryPrimitives.ReadUInt16BigEndian(span.Slice(14, 2)));
    }

    private static async Task<bool> ReceiveExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
