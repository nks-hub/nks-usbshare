using NksUsbShare.Core.Protocol;

namespace NksUsbShare.Core.Usb;

public enum UsbTransferType : byte
{
    Control = 0,
    Isochronous = 1,
    Bulk = 2,
    Interrupt = 3,
}

public enum UsbSpeed : byte
{
    Unknown = 0,
    Low = 1,      // 1.5 Mbit/s
    Full = 2,     // 12 Mbit/s
    High = 3,     // 480 Mbit/s
    Super = 4,    // 5 Gbit/s
    SuperPlus = 5 // 10 Gbit/s and above
}

/// <summary>Outcome of a single URB, mirroring the granularity every host stack exposes.</summary>
public enum UrbStatus : int
{
    Ok = 0,
    Stalled = 1,
    Timeout = 2,
    Cancelled = 3,
    DeviceGone = 4,
    Overflow = 5,
    ShortPacket = 6,
    IoError = 7,
    NotSupported = 8,
}

public sealed record UsbEndpointInfo(
    byte Address,
    UsbTransferType TransferType,
    ushort MaxPacketSize,
    byte Interval)
{
    public bool IsIn => (Address & 0x80) != 0;
    public byte Number => (byte)(Address & 0x0F);

    public void Write(PayloadWriter w)
    {
        w.U8(Address).U8((byte)TransferType).U16(MaxPacketSize).U8(Interval);
    }

    public static UsbEndpointInfo Read(ref PayloadReader r)
        => new(r.U8(), (UsbTransferType)r.U8(), r.U16(), r.U8());
}

public sealed record UsbInterfaceInfo(
    byte Number,
    byte AlternateSetting,
    byte InterfaceClass,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    IReadOnlyList<UsbEndpointInfo> Endpoints)
{
    public void Write(PayloadWriter w)
    {
        w.U8(Number).U8(AlternateSetting).U8(InterfaceClass).U8(InterfaceSubClass).U8(InterfaceProtocol);
        w.U16((ushort)Endpoints.Count);
        foreach (var ep in Endpoints) ep.Write(w);
    }

    public static UsbInterfaceInfo Read(ref PayloadReader r)
    {
        var number = r.U8();
        var alt = r.U8();
        var cls = r.U8();
        var sub = r.U8();
        var proto = r.U8();
        var count = r.U16();
        var endpoints = new List<UsbEndpointInfo>(count);
        for (var i = 0; i < count; i++) endpoints.Add(UsbEndpointInfo.Read(ref r));
        return new UsbInterfaceInfo(number, alt, cls, sub, proto, endpoints);
    }
}

/// <summary>
/// A USB device as presented to the other end of the link. <see cref="DeviceId"/> is a stable,
/// backend-assigned handle (bus path on Linux/macOS, device instance id on Windows).
/// </summary>
public sealed record UsbDeviceInfo
{
    public required string DeviceId { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public ushort DeviceRelease { get; init; }
    public byte DeviceClass { get; init; }
    public byte DeviceSubClass { get; init; }
    public byte DeviceProtocol { get; init; }
    public byte ConfigurationValue { get; init; }
    public UsbSpeed Speed { get; init; } = UsbSpeed.Unknown;
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string BusPath { get; init; } = "";
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; init; } = [];

    /// <summary>True when the host's OS driver stack still owns the device and it cannot be claimed yet.</summary>
    public bool RequiresDriverDetach { get; init; }

    /// <summary>Human readable reason the device cannot currently be shared, or null when it can.</summary>
    public string? ShareBlockedReason { get; init; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Product) ? Product :
        !string.IsNullOrWhiteSpace(Manufacturer) ? Manufacturer :
        $"USB {VendorId:x4}:{ProductId:x4}";

    public string HardwareKey => $"{VendorId:x4}:{ProductId:x4}";

    public void Write(PayloadWriter w)
    {
        w.Str(DeviceId).U16(VendorId).U16(ProductId).U16(DeviceRelease);
        w.U8(DeviceClass).U8(DeviceSubClass).U8(DeviceProtocol).U8(ConfigurationValue).U8((byte)Speed);
        w.Str(Manufacturer).Str(Product).Str(SerialNumber).Str(BusPath);
        w.Bool(RequiresDriverDetach).Str(ShareBlockedReason ?? "");
        w.U16((ushort)Interfaces.Count);
        foreach (var i in Interfaces) i.Write(w);
    }

    public static UsbDeviceInfo Read(ref PayloadReader r)
    {
        var deviceId = r.Str();
        var vid = r.U16();
        var pid = r.U16();
        var release = r.U16();
        var cls = r.U8();
        var sub = r.U8();
        var proto = r.U8();
        var config = r.U8();
        var speed = (UsbSpeed)r.U8();
        var manufacturer = r.Str();
        var product = r.Str();
        var serial = r.Str();
        var busPath = r.Str();
        var needsDetach = r.Bool();
        var blocked = r.Str();
        var count = r.U16();
        var interfaces = new List<UsbInterfaceInfo>(count);
        for (var i = 0; i < count; i++) interfaces.Add(UsbInterfaceInfo.Read(ref r));

        return new UsbDeviceInfo
        {
            DeviceId = deviceId,
            VendorId = vid,
            ProductId = pid,
            DeviceRelease = release,
            DeviceClass = cls,
            DeviceSubClass = sub,
            DeviceProtocol = proto,
            ConfigurationValue = config,
            Speed = speed,
            Manufacturer = manufacturer,
            Product = product,
            SerialNumber = serial,
            BusPath = busPath,
            RequiresDriverDetach = needsDetach,
            ShareBlockedReason = string.IsNullOrEmpty(blocked) ? null : blocked,
            Interfaces = interfaces,
        };
    }
}

/// <summary>A transfer request travelling from importer to exporter.</summary>
public sealed class UrbRequest
{
    public ulong UrbId { get; init; }
    public byte Endpoint { get; init; }
    public UsbTransferType TransferType { get; init; }

    /// <summary>The 8 byte SETUP packet; control transfers only.</summary>
    public byte[] Setup { get; init; } = new byte[8];

    /// <summary>Payload for OUT transfers. Empty for IN.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>How many bytes the importer expects back on an IN transfer.</summary>
    public int ExpectedLength { get; init; }

    public uint TimeoutMs { get; init; } = 5000;

    /// <summary>Per-packet lengths for isochronous transfers; empty otherwise.</summary>
    public int[] IsoPacketLengths { get; init; } = [];

    public bool IsIn => TransferType == UsbTransferType.Control
        ? (Setup.Length > 0 && (Setup[0] & 0x80) != 0)
        : (Endpoint & 0x80) != 0;

    public void Write(PayloadWriter w)
    {
        w.U64(UrbId).U8(Endpoint).U8((byte)TransferType);
        w.Raw(Setup.Length == 8 ? Setup : new byte[8]);
        w.I32(ExpectedLength).U32(TimeoutMs);
        w.U16((ushort)IsoPacketLengths.Length);
        foreach (var length in IsoPacketLengths) w.I32(length);
        w.Bytes(Data);
    }

    public static UrbRequest Read(ref PayloadReader r)
    {
        var urbId = r.U64();
        var endpoint = r.U8();
        var type = (UsbTransferType)r.U8();
        var setup = r.Raw(8);
        var expected = r.I32();
        var timeout = r.U32();
        var isoCount = r.U16();
        var iso = new int[isoCount];
        for (var i = 0; i < isoCount; i++) iso[i] = r.I32();
        var data = r.Bytes();

        return new UrbRequest
        {
            UrbId = urbId,
            Endpoint = endpoint,
            TransferType = type,
            Setup = setup,
            ExpectedLength = expected,
            TimeoutMs = timeout,
            IsoPacketLengths = iso,
            Data = data,
        };
    }
}

public sealed record IsoPacketResult(int ActualLength, UrbStatus Status);

/// <summary>The exporter's answer to a <see cref="UrbRequest"/>.</summary>
public sealed class UrbResult
{
    public ulong UrbId { get; init; }
    public UrbStatus Status { get; init; }
    public int ActualLength { get; init; }
    public byte[] Data { get; init; } = [];
    public IsoPacketResult[] IsoResults { get; init; } = [];

    /// <summary>Backend specific detail retained for diagnostics; never load bearing for control flow.</summary>
    public string? Detail { get; init; }

    public void Write(PayloadWriter w)
    {
        w.U64(UrbId).I32((int)Status).I32(ActualLength);
        w.U16((ushort)IsoResults.Length);
        foreach (var iso in IsoResults) w.I32(iso.ActualLength).I32((int)iso.Status);
        w.Bytes(Data);
        w.Str(Detail ?? "");
    }

    public static UrbResult Read(ref PayloadReader r)
    {
        var urbId = r.U64();
        var status = (UrbStatus)r.I32();
        var actual = r.I32();
        var isoCount = r.U16();
        var iso = new IsoPacketResult[isoCount];
        for (var i = 0; i < isoCount; i++) iso[i] = new IsoPacketResult(r.I32(), (UrbStatus)r.I32());
        var data = r.Bytes();
        var detail = r.Str();

        return new UrbResult
        {
            UrbId = urbId,
            Status = status,
            ActualLength = actual,
            IsoResults = iso,
            Data = data,
            Detail = string.IsNullOrEmpty(detail) ? null : detail,
        };
    }
}

/// <summary>Thrown by backends when the underlying OS call fails.</summary>
public sealed class UsbBackendException : Exception
{
    public UsbBackendException(string message, Exception? inner = null) : base(message, inner) { }
}
