using NksUsbShare.Core.Usb;

namespace NksUsbShare.Tests;

/// <summary>
/// A test double standing in for a real USB device so the host, the protocol and the client can
/// be exercised without hardware. It is deliberately confined to the test project: the product
/// itself never fabricates a device.
/// </summary>
public sealed class FakeUsbBackend : IUsbBackend
{
    public const string FlashDriveId = "fake:flash-drive";
    public const string BlockedDeviceId = "fake:blocked";

    public string Name => "Fake backend (tests only)";

    public int OpenCount;
    public int DisposeCount;

    public static readonly UsbDeviceInfo FlashDrive = new()
    {
        DeviceId = FlashDriveId,
        VendorId = 0x0781,
        ProductId = 0x5567,
        DeviceRelease = 0x0100,
        DeviceClass = 0x00,
        ConfigurationValue = 1,
        Speed = UsbSpeed.High,
        Manufacturer = "Test Vendor",
        Product = "Test Flash Drive",
        SerialNumber = "TESTSERIAL01",
        BusPath = "fake bus 1",
        Interfaces =
        [
            new UsbInterfaceInfo(0, 0, 0x08, 0x06, 0x50,
            [
                new UsbEndpointInfo(0x81, UsbTransferType.Bulk, 512, 0),
                new UsbEndpointInfo(0x02, UsbTransferType.Bulk, 512, 0),
            ]),
        ],
    };

    private static readonly UsbDeviceInfo Blocked = new()
    {
        DeviceId = BlockedDeviceId,
        VendorId = 0x046d,
        ProductId = 0xc52b,
        Product = "Test Keyboard",
        SerialNumber = "",
        ShareBlockedReason = "Owned by a class driver in this test.",
    };

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<UsbDeviceInfo>>([FlashDrive, Blocked]);

    public Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (deviceId != FlashDriveId)
            throw new UsbBackendException($"Device '{deviceId}' cannot be opened.");

        Interlocked.Increment(ref OpenCount);
        return Task.FromResult<IUsbDeviceHandle>(new FakeHandle(this));
    }

    private sealed class FakeHandle(FakeUsbBackend owner) : IUsbDeviceHandle
    {
        /// <summary>Whatever was last written to the OUT endpoint, echoed back on the IN endpoint.</summary>
        private byte[] _echoBuffer = [];

        public UsbDeviceInfo Info => FlashDrive;
        public byte Configuration { get; private set; } = 1;
        public readonly HashSet<byte> ClaimedInterfaces = [];

        public Task<UrbResult> SubmitAsync(UrbRequest request, CancellationToken cancellationToken)
        {
            switch (request.TransferType)
            {
                case UsbTransferType.Control when request.Setup[1] == 0x06:
                {
                    // GET_DESCRIPTOR: answer with a real 18 byte device descriptor.
                    var descriptorType = (byte)(request.Setup[3]);
                    if (descriptorType != 0x01) break;

                    byte[] descriptor =
                    [
                        0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40,
                        (byte)FlashDrive.VendorId, (byte)(FlashDrive.VendorId >> 8),
                        (byte)FlashDrive.ProductId, (byte)(FlashDrive.ProductId >> 8),
                        0x00, 0x01, 0x01, 0x02, 0x03, 0x01,
                    ];

                    var length = Math.Min(descriptor.Length, request.ExpectedLength);
                    return Task.FromResult(new UrbResult
                    {
                        UrbId = request.UrbId,
                        Status = UrbStatus.Ok,
                        ActualLength = length,
                        Data = descriptor[..length],
                    });
                }

                case UsbTransferType.Bulk when (request.Endpoint & 0x80) == 0:
                    _echoBuffer = request.Data;
                    return Task.FromResult(new UrbResult
                    {
                        UrbId = request.UrbId,
                        Status = UrbStatus.Ok,
                        ActualLength = request.Data.Length,
                    });

                case UsbTransferType.Bulk:
                {
                    var length = Math.Min(_echoBuffer.Length, request.ExpectedLength);
                    return Task.FromResult(new UrbResult
                    {
                        UrbId = request.UrbId,
                        Status = length < request.ExpectedLength ? UrbStatus.ShortPacket : UrbStatus.Ok,
                        ActualLength = length,
                        Data = _echoBuffer[..length],
                    });
                }
            }

            return Task.FromResult(new UrbResult
            {
                UrbId = request.UrbId,
                Status = UrbStatus.Stalled,
                Detail = "The fake device does not implement this request.",
            });
        }

        public bool TryCancel(ulong urbId) => false;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken)
        {
            Configuration = configurationValue;
            return Task.CompletedTask;
        }

        public Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
        {
            if (interfaceNumber != 0) throw new UsbBackendException($"No interface {interfaceNumber} on this device.");
            ClaimedInterfaces.Add(interfaceNumber);
            return Task.CompletedTask;
        }

        public Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
        {
            ClaimedInterfaces.Remove(interfaceNumber);
            return Task.CompletedTask;
        }

        public Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => Interlocked.Increment(ref owner.DisposeCount);
    }
}
