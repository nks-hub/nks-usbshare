using NksUsbShare.Core.Usb;

namespace NksUsbShare.Core.Protocol;

/// <summary>First message on every connection. The token is what the host authenticates against.</summary>
public sealed record HelloMessage(string ClientName, string ClientPlatform, string Token)
{
    public byte[] Serialize() => new PayloadWriter().Str(ClientName).Str(ClientPlatform).Str(Token).ToArray();

    public static HelloMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new HelloMessage(r.Str(), r.Str(), r.Str());
    }
}

public sealed record HelloAckMessage(string HostName, string HostPlatform, string BackendName, ushort ProtocolVersion)
{
    public byte[] Serialize() =>
        new PayloadWriter().Str(HostName).Str(HostPlatform).Str(BackendName).U16(ProtocolVersion).ToArray();

    public static HelloAckMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new HelloAckMessage(r.Str(), r.Str(), r.Str(), r.U16());
    }
}

public sealed record DeviceListMessage(IReadOnlyList<UsbDeviceInfo> Devices)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter(1024);
        w.U16((ushort)Devices.Count);
        foreach (var d in Devices) d.Write(w);
        return w.ToArray();
    }

    public static DeviceListMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var count = r.U16();
        var devices = new List<UsbDeviceInfo>(count);
        for (var i = 0; i < count; i++) devices.Add(UsbDeviceInfo.Read(ref r));
        return new DeviceListMessage(devices);
    }
}

public sealed record OpenDeviceRequestMessage(string DeviceId)
{
    public byte[] Serialize() => new PayloadWriter().Str(DeviceId).ToArray();

    public static OpenDeviceRequestMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new OpenDeviceRequestMessage(r.Str());
    }
}

public sealed record OpenDeviceResponseMessage(uint SessionId, UsbDeviceInfo Device)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter(512);
        w.U32(SessionId);
        Device.Write(w);
        return w.ToArray();
    }

    public static OpenDeviceResponseMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var sessionId = r.U32();
        return new OpenDeviceResponseMessage(sessionId, UsbDeviceInfo.Read(ref r));
    }
}

public sealed record SessionMessage(uint SessionId)
{
    public byte[] Serialize() => new PayloadWriter().U32(SessionId).ToArray();

    public static SessionMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SessionMessage(r.U32());
    }
}

/// <summary>Carries a URB in either direction, tagged with the session it belongs to.</summary>
public sealed record UrbSubmitMessage(uint SessionId, UrbRequest Request)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter(256 + Request.Data.Length);
        w.U32(SessionId);
        Request.Write(w);
        return w.ToArray();
    }

    public static UrbSubmitMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var sessionId = r.U32();
        return new UrbSubmitMessage(sessionId, UrbRequest.Read(ref r));
    }
}

public sealed record UrbCompleteMessage(uint SessionId, UrbResult Result)
{
    public byte[] Serialize()
    {
        var w = new PayloadWriter(256 + Result.Data.Length);
        w.U32(SessionId);
        Result.Write(w);
        return w.ToArray();
    }

    public static UrbCompleteMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var sessionId = r.U32();
        return new UrbCompleteMessage(sessionId, UrbResult.Read(ref r));
    }
}

public sealed record UrbCancelMessage(uint SessionId, ulong UrbId)
{
    public byte[] Serialize() => new PayloadWriter().U32(SessionId).U64(UrbId).ToArray();

    public static UrbCancelMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new UrbCancelMessage(r.U32(), r.U64());
    }
}

/// <summary>
/// Device control operations that share one shape: a session plus at most two byte arguments.
/// Which arguments are meaningful depends on the frame's <see cref="MessageType"/>.
/// </summary>
public sealed record DeviceControlMessage(uint SessionId, byte Arg0, byte Arg1)
{
    public byte[] Serialize() => new PayloadWriter().U32(SessionId).U8(Arg0).U8(Arg1).ToArray();

    public static DeviceControlMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new DeviceControlMessage(r.U32(), r.U8(), r.U8());
    }
}

public sealed record SimpleResponseMessage(bool Success, string Message)
{
    public byte[] Serialize() => new PayloadWriter().Bool(Success).Str(Message).ToArray();

    public static SimpleResponseMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new SimpleResponseMessage(r.Bool(), r.Str());
    }
}

public sealed record ErrorMessage(ProtocolError Code, string Message)
{
    public byte[] Serialize() => new PayloadWriter().U16((ushort)Code).Str(Message).ToArray();

    public static ErrorMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return new ErrorMessage((ProtocolError)r.U16(), r.Str());
    }
}
