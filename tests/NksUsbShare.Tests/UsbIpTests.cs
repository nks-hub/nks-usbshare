using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NksUsbShare.Core.Client;
using NksUsbShare.Core.Diagnostics;

namespace NksUsbShare.Tests;

/// <summary>
/// Exercises the USB/IP endpoint by connecting to it exactly as a virtual host controller would.
/// The protocol is big endian throughout, and a byte order slip here would break every transfer
/// while still looking plausible, so the encoding is checked field by field.
/// </summary>
public static class UsbIpTests
{
    private const ushort ProtocolVersion = 0x0111;
    private const ushort RequestDeviceList = 0x8005;
    private const ushort RequestImport = 0x8003;
    private const uint CommandSubmit = 1;
    private const uint ReplySubmit = 3;

    public static void Register()
    {
        const string suite = "USB/IP endpoint";

        TestRunner.Add(suite, "a listing reports the published device with its real identity", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();

            using var controller = await fixture.ConnectAsync();
            await controller.SendAsync(OperationRequest(RequestDeviceList, extra: 4));

            var header = await controller.ReadAsync(8);
            Assert.Equal(ProtocolVersion, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)), "protocol version");
            Assert.Equal((ushort)0x0005, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)), "reply code");
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)), "status");

            var count = BinaryPrimitives.ReadUInt32BigEndian(await controller.ReadAsync(4));
            Assert.Equal(1u, count, "one device is published");

            var record = await controller.ReadAsync(312);
            AssertDeviceRecord(record, fixture.BusId);

            // One interface follows: class, subclass, protocol, padding.
            var iface = await controller.ReadAsync(4);
            Assert.Equal((byte)0x08, iface[0], "interface class is mass storage");
            Assert.Equal((byte)0x06, iface[1], "interface subclass");
            Assert.Equal((byte)0x50, iface[2], "interface protocol");
        });

        TestRunner.Add(suite, "an import of a known bus id is accepted", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var controller = await fixture.ConnectAsync();

            await controller.SendAsync(ImportRequest(fixture.BusId));

            var header = await controller.ReadAsync(8);
            Assert.Equal((ushort)0x0003, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)), "reply code");
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)), "status is success");

            AssertDeviceRecord(await controller.ReadAsync(312), fixture.BusId);
        });

        TestRunner.Add(suite, "an import of an unknown bus id is refused", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var controller = await fixture.ConnectAsync();

            await controller.SendAsync(ImportRequest("9-9"));

            var header = await controller.ReadAsync(8);
            Assert.True(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) != 0,
                "a non-zero status tells the controller the bus id is unknown");
        });

        TestRunner.Add(suite, "a control transfer travels to the device and the answer comes back", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var controller = await fixture.ConnectAsync();

            await controller.SendAsync(ImportRequest(fixture.BusId));
            await controller.ReadAsync(8 + 312);

            // GET_DESCRIPTOR(DEVICE), 18 bytes, on endpoint zero, device to host.
            byte[] setup = [0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x12, 0x00];
            await controller.SendAsync(SubmitCommand(sequence: 1, isIn: true, endpoint: 0, transferLength: 18, setup: setup));

            var reply = await controller.ReadAsync(48);
            Assert.Equal(ReplySubmit, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(0, 4)), "reply command");
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4)), "sequence number is echoed");
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20, 4)), "status is success");

            var actualLength = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(24, 4));
            Assert.Equal(18, actualLength, "a whole device descriptor came back");

            var descriptor = await controller.ReadAsync(actualLength);
            Assert.Equal((byte)0x12, descriptor[0], "bLength");
            Assert.Equal((byte)0x01, descriptor[1], "bDescriptorType is DEVICE");

            var vendor = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(8, 2));
            Assert.Equal(FakeUsbBackend.FlashDrive.VendorId, vendor, "vendor id read through the whole chain");
        });

        TestRunner.Add(suite, "an outbound bulk transfer carries its payload", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var controller = await fixture.ConnectAsync();

            await controller.SendAsync(ImportRequest(fixture.BusId));
            await controller.ReadAsync(8 + 312);

            var payload = new byte[2048];
            Random.Shared.NextBytes(payload);

            // Endpoint 2, host to device.
            var command = SubmitCommand(sequence: 7, isIn: false, endpoint: 2, transferLength: payload.Length, setup: new byte[8]);
            await controller.SendAsync([.. command, .. payload]);

            var reply = await controller.ReadAsync(48);
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4)), "sequence number");
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20, 4)), "status is success");
            Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(24, 4)), "the whole payload was written");

            // The fake device echoes the last write back on the IN endpoint.
            await controller.SendAsync(SubmitCommand(sequence: 8, isIn: true, endpoint: 2, transferLength: payload.Length, setup: new byte[8]));

            var echoReply = await controller.ReadAsync(48);
            var echoLength = BinaryPrimitives.ReadInt32BigEndian(echoReply.AsSpan(24, 4));
            Assert.Equal(payload.Length, echoLength, "the echo is the same length");

            var echoed = await controller.ReadAsync(echoLength);
            Assert.SequenceEqual(payload, echoed, "the payload survived the round trip through USB/IP");
        });

        TestRunner.Add(suite, "a stalled transfer is reported as EPIPE", async () =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var controller = await fixture.ConnectAsync();

            await controller.SendAsync(ImportRequest(fixture.BusId));
            await controller.ReadAsync(8 + 312);

            // The fake device stalls anything it does not implement.
            byte[] setup = [0x80, 0x99, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00];
            await controller.SendAsync(SubmitCommand(sequence: 3, isIn: true, endpoint: 0, transferLength: 8, setup: setup));

            var reply = await controller.ReadAsync(48);
            Assert.Equal(-32, BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(20, 4)),
                "a stall is reported as -EPIPE, which is what the controller expects");
        });
    }

    private static void AssertDeviceRecord(byte[] record, string busId)
    {
        Assert.Equal(312, record.Length, "device record size");

        var reportedBusId = Encoding.ASCII.GetString(record, 256, 32).TrimEnd('\0');
        Assert.Equal(busId, reportedBusId, "bus id");

        var span = record.AsSpan(288);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4)), "high speed maps to 3");
        Assert.Equal(FakeUsbBackend.FlashDrive.VendorId, BinaryPrimitives.ReadUInt16BigEndian(span.Slice(12, 2)), "vendor id");
        Assert.Equal(FakeUsbBackend.FlashDrive.ProductId, BinaryPrimitives.ReadUInt16BigEndian(span.Slice(14, 2)), "product id");
        Assert.Equal((byte)1, span[23], "one interface is reported");
    }

    // ---- request builders ----

    private static byte[] OperationRequest(ushort code, int extra)
    {
        var request = new byte[8 + extra];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), code);
        return request;
    }

    private static byte[] ImportRequest(string busId)
    {
        var request = OperationRequest(RequestImport, extra: 4 + 32);
        var bytes = Encoding.ASCII.GetBytes(busId);
        bytes.CopyTo(request.AsSpan(12, Math.Min(bytes.Length, 32)));
        return request;
    }

    private static byte[] SubmitCommand(uint sequence, bool isIn, uint endpoint, int transferLength, byte[] setup)
    {
        var command = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(0, 4), CommandSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(4, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(8, 4), 0x00010001);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(12, 4), isIn ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(16, 4), endpoint);
        BinaryPrimitives.WriteInt32BigEndian(command.AsSpan(24, 4), transferLength);
        setup.CopyTo(command.AsSpan(40, 8));
        return command;
    }

    // ---- fixture ----

    /// <summary>
    /// A whole link stood up in memory: fake hardware, a real host, a real client, and the USB/IP
    /// endpoint under test. Only the device at the far end is a double.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public required UsbIpEndpoint Endpoint { get; init; }
        public required string BusId { get; init; }
        public required Func<Task> Cleanup { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var harness = await EndToEndHarness.StartAsync();
            var client = await harness.ConnectClientAsync();
            var device = await client.OpenDeviceAsync(FakeUsbBackend.FlashDriveId, CancellationToken.None);

            var endpoint = new UsbIpEndpoint(new ActivityLog());
            await endpoint.StartAsync(0, CancellationToken.None);
            var busId = endpoint.Publish(device);

            return new Fixture
            {
                Endpoint = endpoint,
                BusId = busId,
                Cleanup = async () =>
                {
                    device.Dispose();
                    await client.DisposeAsync();
                    await harness.DisposeAsync();
                },
            };
        }

        public async Task<Controller> ConnectAsync()
        {
            var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, Endpoint.Port);
            socket.NoDelay = true;
            return new Controller(socket);
        }

        public async ValueTask DisposeAsync()
        {
            await Endpoint.DisposeAsync();
            await Cleanup();
        }
    }

    /// <summary>Stands in for the operating system's virtual USB host controller.</summary>
    private sealed class Controller(TcpClient socket) : IDisposable
    {
        private readonly NetworkStream _stream = socket.GetStream();

        public async Task SendAsync(byte[] bytes)
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task<byte[]> ReadAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            while (offset < count)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset), timeout.Token);
                if (read == 0) throw new Exception($"the endpoint closed after {offset} of {count} bytes");
                offset += read;
            }

            return buffer;
        }

        public void Dispose() => socket.Dispose();
    }
}
