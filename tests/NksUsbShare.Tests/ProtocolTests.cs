using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Tests;

public static class ProtocolTests
{
    public static void Register()
    {
        const string suite = "Protocol";

        TestRunner.Add(suite, "frame header round-trips through its 16 byte layout", () =>
        {
            var header = new FrameHeader(FrameHeader.CurrentVersion, MessageType.UrbSubmit, 0xDEADBEEF, 4096);
            var buffer = new byte[FrameHeader.Size];
            header.WriteTo(buffer);

            Assert.Equal(FrameHeader.Size, buffer.Length, "header size");
            // Magic is "NUSB" stored little endian.
            Assert.SequenceEqual("NUSB"u8, buffer.AsSpan(0, 4), "magic bytes");

            var parsed = FrameHeader.ReadFrom(buffer);
            Assert.Equal(header, parsed, "round-tripped header");
        });

        TestRunner.Add(suite, "frame header rejects foreign magic", () =>
            Assert.Throws<ProtocolViolationExceptionEx>(
                () => FrameHeader.ReadFrom(new byte[FrameHeader.Size]),
                "all-zero header"));

        TestRunner.Add(suite, "frame header rejects an oversized payload claim", () =>
        {
            var buffer = new byte[FrameHeader.Size];
            new FrameHeader(1, MessageType.Ping, 1, 0).WriteTo(buffer);
            // Overwrite the length field with something past the cap.
            BitConverter.GetBytes(FrameHeader.MaxPayloadLength + 1).CopyTo(buffer, 12);

            Assert.Throws<ProtocolViolationExceptionEx>(() => FrameHeader.ReadFrom(buffer), "oversized payload");
        });

        TestRunner.Add(suite, "payload reader refuses to read past the end", () =>
            Assert.Throws<ProtocolViolationExceptionEx>(() =>
            {
                var reader = new PayloadReader(new byte[3]);
                reader.U32();
            }, "reading 4 bytes from a 3 byte payload"));

        TestRunner.Add(suite, "writer and reader agree on every primitive", () =>
        {
            var payload = new PayloadWriter()
                .U8(0xAB).I8(-42).Bool(true).U16(0x1234).U32(0xCAFEBABE).I32(-7).U64(0x0102030405060708)
                .Str("ěščřžýáíé 🔌").Bytes([1, 2, 3])
                .ToArray();

            var reader = new PayloadReader(payload);
            Assert.Equal((byte)0xAB, reader.U8(), "u8");
            Assert.Equal((sbyte)-42, reader.I8(), "i8");
            Assert.Equal(true, reader.Bool(), "bool");
            Assert.Equal((ushort)0x1234, reader.U16(), "u16");
            Assert.Equal(0xCAFEBABE, reader.U32(), "u32");
            Assert.Equal(-7, reader.I32(), "i32");
            Assert.Equal(0x0102030405060708UL, reader.U64(), "u64");
            Assert.Equal("ěščřžýáíé 🔌", reader.Str(), "utf-8 string");
            Assert.SequenceEqual([1, 2, 3], reader.Bytes(), "byte array");
            Assert.Equal(0, reader.Remaining, "fully consumed");
        });

        TestRunner.Add(suite, "hello round-trips", () =>
        {
            var original = new HelloMessage("workstation", "Windows", "s3cr3t-token");
            var parsed = HelloMessage.Deserialize(original.Serialize());
            Assert.Equal(original, parsed, "hello");
        });

        TestRunner.Add(suite, "device list round-trips with interfaces and endpoints", () =>
        {
            var device = new UsbDeviceInfo
            {
                DeviceId = @"USB\VID_0781&PID_5567\4C530001",
                VendorId = 0x0781,
                ProductId = 0x5567,
                DeviceRelease = 0x0100,
                DeviceClass = 0x00,
                ConfigurationValue = 1,
                Speed = UsbSpeed.High,
                Manufacturer = "SanDisk",
                Product = "Cruzer Blade",
                SerialNumber = "4C530001",
                BusPath = "port 3",
                RequiresDriverDetach = true,
                ShareBlockedReason = "bound to USBSTOR",
                Interfaces =
                [
                    new UsbInterfaceInfo(0, 0, 0x08, 0x06, 0x50,
                    [
                        new UsbEndpointInfo(0x81, UsbTransferType.Bulk, 512, 0),
                        new UsbEndpointInfo(0x02, UsbTransferType.Bulk, 512, 0),
                    ]),
                ],
            };

            var parsed = DeviceListMessage.Deserialize(new DeviceListMessage([device]).Serialize());
            Assert.Equal(1, parsed.Devices.Count, "device count");

            var actual = parsed.Devices[0];
            Assert.Equal(device.DeviceId, actual.DeviceId, "device id");
            Assert.Equal(device.VendorId, actual.VendorId, "vendor id");
            Assert.Equal(device.Speed, actual.Speed, "speed");
            Assert.Equal(device.ShareBlockedReason, actual.ShareBlockedReason, "blocked reason");
            Assert.Equal(2, actual.Interfaces[0].Endpoints.Count, "endpoint count");
            Assert.Equal((ushort)512, actual.Interfaces[0].Endpoints[0].MaxPacketSize, "max packet size");
            Assert.Equal(UsbTransferType.Bulk, actual.Interfaces[0].Endpoints[1].TransferType, "transfer type");
        });

        TestRunner.Add(suite, "URB request round-trips including SETUP and payload", () =>
        {
            var request = new UrbRequest
            {
                UrbId = 0x1122334455667788,
                Endpoint = 0x02,
                TransferType = UsbTransferType.Bulk,
                Setup = [0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x12, 0x00],
                Data = [0xDE, 0xAD, 0xBE, 0xEF],
                ExpectedLength = 512,
                TimeoutMs = 1234,
                IsoPacketLengths = [64, 64, 64],
            };

            var writer = new PayloadWriter();
            request.Write(writer);
            var reader = new PayloadReader(writer.AsSpan());
            var parsed = UrbRequest.Read(ref reader);

            Assert.Equal(request.UrbId, parsed.UrbId, "urb id");
            Assert.Equal(request.Endpoint, parsed.Endpoint, "endpoint");
            Assert.SequenceEqual(request.Setup, parsed.Setup, "setup packet");
            Assert.SequenceEqual(request.Data, parsed.Data, "payload");
            Assert.Equal(request.ExpectedLength, parsed.ExpectedLength, "expected length");
            Assert.Equal(3, parsed.IsoPacketLengths.Length, "iso packet count");
            Assert.Equal(64, parsed.IsoPacketLengths[2], "iso packet length");
        });

        TestRunner.Add(suite, "URB result round-trips including iso packet results", () =>
        {
            var result = new UrbResult
            {
                UrbId = 42,
                Status = UrbStatus.ShortPacket,
                ActualLength = 3,
                Data = [1, 2, 3],
                IsoResults = [new IsoPacketResult(64, UrbStatus.Ok), new IsoPacketResult(0, UrbStatus.Timeout)],
                Detail = "usbfs status -121",
            };

            var writer = new PayloadWriter();
            result.Write(writer);
            var reader = new PayloadReader(writer.AsSpan());
            var parsed = UrbResult.Read(ref reader);

            Assert.Equal(result.Status, parsed.Status, "status");
            Assert.SequenceEqual(result.Data, parsed.Data, "data");
            Assert.Equal(2, parsed.IsoResults.Length, "iso result count");
            Assert.Equal(UrbStatus.Timeout, parsed.IsoResults[1].Status, "second iso status");
            Assert.Equal(result.Detail, parsed.Detail, "detail");
        });

        TestRunner.Add(suite, "control transfer direction comes from the SETUP packet, not the endpoint", () =>
        {
            var deviceToHost = new UrbRequest
            {
                TransferType = UsbTransferType.Control,
                Setup = [0x80, 0x06, 0, 1, 0, 0, 0x12, 0],
            };
            var hostToDevice = new UrbRequest
            {
                TransferType = UsbTransferType.Control,
                Setup = [0x00, 0x09, 1, 0, 0, 0, 0, 0],
            };

            Assert.True(deviceToHost.IsIn, "0x80 request type is device-to-host");
            Assert.False(hostToDevice.IsIn, "0x00 request type is host-to-device");
        });

        TestRunner.Add(suite, "framed connection carries messages over a duplex stream", async () =>
        {
            // A pipe pair stands in for the TLS stream so the framing is exercised end to end.
            var (clientStream, serverStream) = DuplexPipePair.Create();
            await using var client = new FramedConnection(clientStream);
            await using var server = new FramedConnection(serverStream);

            var hello = new HelloMessage("tester", "unit", "token").Serialize();
            await client.SendAsync(MessageType.Hello, hello, CancellationToken.None);

            var frame = await server.ReceiveAsync(CancellationToken.None);
            Assert.NotNull(frame is null ? null : "frame", "a frame arrived");
            Assert.Equal(MessageType.Hello, frame!.Value.Type, "message type");

            var parsed = HelloMessage.Deserialize(frame.Value.Payload);
            Assert.Equal("tester", parsed.ClientName, "client name");
            Assert.Equal("token", parsed.Token, "token");
        });

        TestRunner.Add(suite, "framed connection reassembles a payload split across reads", async () =>
        {
            var (clientStream, serverStream) = DuplexPipePair.Create();
            await using var client = new FramedConnection(clientStream);
            await using var server = new FramedConnection(serverStream);

            // 300 KB forces many underlying reads.
            var big = new byte[300 * 1024];
            Random.Shared.NextBytes(big);

            var send = client.SendAsync(MessageType.UrbComplete,
                new UrbCompleteMessage(7, new UrbResult { UrbId = 9, Status = UrbStatus.Ok, ActualLength = big.Length, Data = big }).Serialize(),
                CancellationToken.None);

            var frame = await server.ReceiveAsync(CancellationToken.None);
            await send;

            var parsed = UrbCompleteMessage.Deserialize(frame!.Value.Payload);
            Assert.Equal(7u, parsed.SessionId, "session id");
            Assert.SequenceEqual(big, parsed.Result.Data, "large payload survived reassembly");
        });

        TestRunner.Add(suite, "framed connection reports a clean end of stream as null", async () =>
        {
            var (clientStream, serverStream) = DuplexPipePair.Create();
            var client = new FramedConnection(clientStream);
            await using var server = new FramedConnection(serverStream);

            await client.DisposeAsync();
            var frame = await server.ReceiveAsync(CancellationToken.None);
            Assert.True(frame is null, "closed peer yields null rather than throwing");
        });
    }
}
