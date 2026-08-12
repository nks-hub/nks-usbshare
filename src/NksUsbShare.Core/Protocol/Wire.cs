using System.Buffers.Binary;
using System.Text;

namespace NksUsbShare.Core.Protocol;

/// <summary>
/// Message kinds carried by the NKS UsbShare wire protocol.
/// </summary>
public enum MessageType : ushort
{
    Hello = 0x0001,
    HelloAck = 0x0002,

    ListDevicesRequest = 0x0010,
    ListDevicesResponse = 0x0011,

    OpenDeviceRequest = 0x0020,
    OpenDeviceResponse = 0x0021,
    CloseDeviceRequest = 0x0022,
    CloseDeviceResponse = 0x0023,

    UrbSubmit = 0x0030,
    UrbComplete = 0x0031,
    UrbCancel = 0x0032,

    ResetDeviceRequest = 0x0040,
    SetConfigurationRequest = 0x0041,
    ClaimInterfaceRequest = 0x0042,
    ReleaseInterfaceRequest = 0x0043,
    SetAltSettingRequest = 0x0044,
    ClearHaltRequest = 0x0045,
    SimpleResponse = 0x004F,

    Error = 0x00F0,
    Ping = 0x00FE,
    Pong = 0x00FF,
}

/// <summary>
/// Failure codes returned in <see cref="MessageType.Error"/> payloads.
/// </summary>
public enum ProtocolError : ushort
{
    None = 0,
    Unauthorized = 1,
    UnsupportedVersion = 2,
    UnknownDevice = 3,
    AccessDenied = 4,
    DeviceBusy = 5,
    DeviceGone = 6,
    BackendFailure = 7,
    MalformedRequest = 8,
    NotSupportedOnPlatform = 9,
    InternalError = 10,
}

/// <summary>
/// Fixed-size prologue that precedes every payload on the wire.
/// Layout (little endian, 16 bytes): magic u32 | version u16 | type u16 | sequence u32 | payloadLength u32.
/// </summary>
public readonly record struct FrameHeader(ushort Version, MessageType Type, uint Sequence, uint PayloadLength)
{
    public const int Size = 16;
    public const uint Magic = 0x4253554E; // "NUSB" read little endian
    public const ushort CurrentVersion = 1;

    /// <summary>Refuse absurd allocations from a hostile or desynchronised peer.</summary>
    public const uint MaxPayloadLength = 8 * 1024 * 1024;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Destination too small.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), (ushort)Type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), PayloadLength);
    }

    public static FrameHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) throw new ArgumentException("Source too small.", nameof(source));
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        if (magic != Magic) throw new ProtocolViolationExceptionEx($"Bad frame magic 0x{magic:X8}.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
        var type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6, 2));
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));
        if (length > MaxPayloadLength) throw new ProtocolViolationExceptionEx($"Payload of {length} bytes exceeds the {MaxPayloadLength} byte cap.");
        return new FrameHeader(version, type, sequence, length);
    }
}

/// <summary>Raised when a peer sends something that cannot be parsed as a valid frame.</summary>
public sealed class ProtocolViolationExceptionEx : Exception
{
    public ProtocolViolationExceptionEx(string message) : base(message) { }
}

/// <summary>Sequential little-endian writer over a growable buffer.</summary>
public sealed class PayloadWriter
{
    private byte[] _buffer;
    private int _length;

    public PayloadWriter(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _length;

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            var grown = new byte[Math.Max(_buffer.Length * 2, _length + count)];
            Array.Copy(_buffer, grown, _length);
            _buffer = grown;
        }
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public PayloadWriter U8(byte value) { Reserve(1)[0] = value; return this; }
    public PayloadWriter I8(sbyte value) { Reserve(1)[0] = unchecked((byte)value); return this; }
    public PayloadWriter Bool(bool value) { Reserve(1)[0] = value ? (byte)1 : (byte)0; return this; }
    public PayloadWriter U16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value); return this; }
    public PayloadWriter U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); return this; }
    public PayloadWriter I32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value); return this; }
    public PayloadWriter U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); return this; }

    public PayloadWriter Bytes(ReadOnlySpan<byte> value)
    {
        U32((uint)value.Length);
        value.CopyTo(Reserve(value.Length));
        return this;
    }

    public PayloadWriter Raw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        return this;
    }

    public PayloadWriter Str(string? value)
    {
        var bytes = value is null ? [] : Encoding.UTF8.GetBytes(value);
        return Bytes(bytes);
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);
}

/// <summary>Sequential little-endian reader that refuses to read past the payload end.</summary>
public ref struct PayloadReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PayloadReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _position + count > _data.Length)
            throw new ProtocolViolationExceptionEx($"Truncated payload: wanted {count} bytes at offset {_position} of {_data.Length}.");
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => unchecked((sbyte)Take(1)[0]);
    public bool Bool() => Take(1)[0] != 0;
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public byte[] Bytes()
    {
        var length = checked((int)U32());
        return Take(length).ToArray();
    }

    public byte[] Raw(int count) => Take(count).ToArray();

    public string Str() => Encoding.UTF8.GetString(Take(checked((int)U32())));
}
