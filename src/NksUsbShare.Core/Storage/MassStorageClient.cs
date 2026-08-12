using System.Buffers.Binary;
using System.Text;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Storage;

/// <summary>
/// Talks to a remote USB storage device the way the USB Mass Storage class defines: SCSI commands
/// wrapped in Bulk-Only Transport, carried over the two bulk endpoints.
///
/// This matters most where the operating system cannot be given the device at all. macOS has no
/// way to present a virtual USB device, so a remote flash drive can never appear in Finder — but
/// its contents are still perfectly reachable, because reading a block is just a SCSI command sent
/// down a bulk pipe.
/// </summary>
public sealed class MassStorageClient
{
    private const uint CommandSignature = 0x43425355; // "USBC"
    private const uint StatusSignature = 0x53425355;  // "USBS"
    private const int CommandBlockSize = 31;
    private const int StatusBlockSize = 13;

    private readonly RemoteUsbDevice _device;
    private readonly byte _bulkIn;
    private readonly byte _bulkOut;
    private uint _tag;

    public byte InterfaceNumber { get; }

    private MassStorageClient(RemoteUsbDevice device, byte interfaceNumber, byte bulkIn, byte bulkOut)
    {
        _device = device;
        InterfaceNumber = interfaceNumber;
        _bulkIn = bulkIn;
        _bulkOut = bulkOut;
    }

    /// <summary>
    /// Finds the mass storage interface and its bulk pipes, or explains why the device is not one.
    /// Class 08, subclass 06, protocol 50 is SCSI over Bulk-Only Transport, which every flash
    /// drive and card reader speaks.
    /// </summary>
    public static MassStorageClient Attach(RemoteUsbDevice device)
    {
        var candidate = device.Info.Interfaces.FirstOrDefault(i =>
            i.InterfaceClass == 0x08 && i.InterfaceProtocol == 0x50);

        if (candidate is null)
            throw new UsbBackendException(
                $"{device.Info.DisplayName} is not a bulk-only storage device " +
                $"(no interface with class 08 protocol 50).");

        var bulkIn = candidate.Endpoints.FirstOrDefault(e => e.IsIn && e.TransferType == UsbTransferType.Bulk);
        var bulkOut = candidate.Endpoints.FirstOrDefault(e => !e.IsIn && e.TransferType == UsbTransferType.Bulk);

        if (bulkIn is null || bulkOut is null)
            throw new UsbBackendException("The storage interface does not expose both bulk endpoints.");

        return new MassStorageClient(device, candidate.Number, bulkIn.Address, bulkOut.Address);
    }

    public Task ClaimAsync(CancellationToken cancellationToken)
        => _device.ClaimInterfaceAsync(InterfaceNumber, cancellationToken);

    public sealed record DeviceIdentity(string Vendor, string Product, string Revision);

    /// <summary>SCSI INQUIRY: who the device says it is.</summary>
    public async Task<DeviceIdentity> InquiryAsync(CancellationToken cancellationToken)
    {
        var data = await TransferAsync([0x12, 0, 0, 0, 36, 0], 36, incoming: true, cancellationToken)
            .ConfigureAwait(false);

        if (data.Length < 36) throw new UsbBackendException($"INQUIRY returned only {data.Length} bytes.");

        // These fields are space padded by the specification, but plenty of devices pad with
        // nulls instead, which would otherwise show up in the middle of the text.
        static string Field(byte[] source, int offset, int length)
            => Encoding.ASCII.GetString(source, offset, length).Replace('\0', ' ').Trim();

        return new DeviceIdentity(Field(data, 8, 8), Field(data, 16, 16), Field(data, 32, 4));
    }

    public sealed record Capacity(uint BlockCount, uint BlockSize)
    {
        public long TotalBytes => (long)BlockCount * BlockSize;
    }

    /// <summary>SCSI READ CAPACITY(10): how big the medium is.</summary>
    public async Task<Capacity> ReadCapacityAsync(CancellationToken cancellationToken)
    {
        var data = await TransferAsync([0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0], 8, incoming: true, cancellationToken)
            .ConfigureAwait(false);

        if (data.Length < 8) throw new UsbBackendException($"READ CAPACITY returned only {data.Length} bytes.");

        // The reply gives the address of the last block, so the count is one more.
        var lastBlock = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        return new Capacity(lastBlock + 1, blockSize);
    }

    /// <summary>SCSI READ(10): the actual bytes off the medium.</summary>
    public async Task<byte[]> ReadBlocksAsync(uint firstBlock, ushort blockCount, uint blockSize, CancellationToken cancellationToken)
    {
        var command = new byte[10];
        command[0] = 0x28;
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), firstBlock);
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(7, 2), blockCount);

        return await TransferAsync(command, (int)(blockCount * blockSize), incoming: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One Bulk-Only Transport exchange: a command wrapper out, the data, then a status wrapper in.
    /// </summary>
    private async Task<byte[]> TransferAsync(byte[] command, int dataLength, bool incoming, CancellationToken cancellationToken)
    {
        var tag = ++_tag;

        var wrapper = new byte[CommandBlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(wrapper.AsSpan(0, 4), CommandSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapper.AsSpan(4, 4), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapper.AsSpan(8, 4), (uint)dataLength);
        wrapper[12] = incoming ? (byte)0x80 : (byte)0x00;
        wrapper[13] = 0; // logical unit zero
        wrapper[14] = (byte)command.Length;
        command.CopyTo(wrapper.AsSpan(15));

        await _device.WriteAsync(_bulkOut, wrapper, cancellationToken: cancellationToken).ConfigureAwait(false);

        var data = Array.Empty<byte>();
        if (dataLength > 0 && incoming)
            data = await _device.ReadAsync(_bulkIn, dataLength, cancellationToken: cancellationToken).ConfigureAwait(false);

        var status = await _device.ReadAsync(_bulkIn, StatusBlockSize, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (status.Length < StatusBlockSize)
            throw new UsbBackendException($"The device returned a {status.Length} byte status block.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0, 4)) != StatusSignature)
            throw new UsbBackendException("The device returned a malformed status block.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4, 4)) != tag)
            throw new UsbBackendException("The device answered a different command than the one sent.");

        var code = status[12];
        if (code != 0)
            throw new UsbBackendException(code == 1
                ? $"The device failed the command (SCSI status 1)."
                : $"The transport failed (status {code}).");

        return data;
    }

    /// <summary>
    /// Reads the volume label out of a FAT boot sector, which is a cheap way to show that the
    /// bytes coming back really are the filesystem and not noise.
    /// </summary>
    public static string? ReadFatLabel(ReadOnlySpan<byte> bootSector)
    {
        if (bootSector.Length < 512) return null;
        if (bootSector[510] != 0x55 || bootSector[511] != 0xAA) return null;

        // FAT32 keeps the label at offset 71, FAT12 and FAT16 at 43.
        var fat32 = Encoding.ASCII.GetString(bootSector.Slice(71, 11)).Trim();
        var fat16 = Encoding.ASCII.GetString(bootSector.Slice(43, 11)).Trim();

        var label = fat32.Length > 0 && fat32 != "NO NAME" ? fat32 : fat16;
        return label.Length > 0 && label.All(c => c is >= ' ' and < (char)127) ? label : null;
    }

    /// <summary>Reads the partition table out of a master boot record, when there is one.</summary>
    public static IReadOnlyList<(byte Type, uint FirstBlock, uint BlockCount)> ReadPartitions(ReadOnlySpan<byte> sector)
    {
        var partitions = new List<(byte, uint, uint)>();
        if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA) return partitions;

        for (var entry = 0; entry < 4; entry++)
        {
            var offset = 446 + entry * 16;
            var type = sector[offset + 4];
            if (type == 0) continue;

            partitions.Add((
                type,
                BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(offset + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(offset + 12, 4))));
        }

        return partitions;
    }
}
