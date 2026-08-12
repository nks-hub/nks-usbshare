using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NksUsbShare.Core.Usb.Linux.LinuxNative;

namespace NksUsbShare.Core.Usb.Linux;

/// <summary>
/// Drives one device through usbfs asynchronous URBs. A single reaper thread polls the file
/// descriptor and completes the pending requests, which is the same model the kernel intends
/// and the only one that supports isochronous transfers and cancellation.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UsbFsDeviceHandle : IUsbDeviceHandle
{
    private readonly int _fd;
    private readonly Thread _reaper;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<IntPtr, PendingUrb> _pending = new();
    private readonly ConcurrentDictionary<ulong, IntPtr> _byUrbId = new();
    private readonly HashSet<byte> _claimedInterfaces = [];
    private readonly Lock _claimLock = new();
    private int _disposed;

    public UsbDeviceInfo Info { get; }

    private sealed record PendingUrb(
        IntPtr Block,
        int DataOffset,
        int DataCapacity,
        int IsoPacketCount,
        bool IsIn,
        ulong UrbId,
        TaskCompletionSource<UrbResult> Completion);

    private UsbFsDeviceHandle(int fd, UsbDeviceInfo info)
    {
        _fd = fd;
        Info = info;
        _reaper = new Thread(ReapLoop) { IsBackground = true, Name = $"usbfs-reaper-{info.DeviceId}" };
        _reaper.Start();
    }

    public static UsbFsDeviceHandle Open(string nodePath, UsbDeviceInfo info)
    {
        var fd = LinuxNative.Open(nodePath, OpenReadWrite | OpenCloExec);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new UsbBackendException($"open('{nodePath}') failed: {Strerror(errno)} (errno {errno}).");
        }

        var handle = new UsbFsDeviceHandle(fd, info);
        try
        {
            handle.DetachKernelDrivers();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the kernel to release every interface still held by an in-tree driver.
    /// Without this the claim below fails with EBUSY for anything a class driver already owns.
    /// </summary>
    private void DetachKernelDrivers()
    {
        foreach (var iface in Info.Interfaces.Select(i => i.Number).Distinct())
        {
            // struct usbdevfs_ioctl { int ifno; int ioctl_code; void *data; }
            var size = 2 * sizeof(int) + IntPtr.Size;
            var block = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(block, 0, iface);
                Marshal.WriteInt32(block, 4, unchecked((int)UsbdevfsDisconnect));
                Marshal.WriteIntPtr(block, IntPtr.Size == 8 ? 8 : 8, IntPtr.Zero);
                Ioctl(_fd, UsbdevfsIoctl, block);
                // A failure here is normal when no driver was bound; the claim below is the real gate.
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    private void ReapLoop()
    {
        var pollFd = new PollFd { Fd = _fd, Events = PollOut };
        var reaped = Marshal.AllocHGlobal(IntPtr.Size);

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                pollFd.Revents = 0;
                var ready = Poll(ref pollFd, 1, 100);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Eintr) continue;
                    break;
                }
                if (ready == 0) continue;

                if ((pollFd.Revents & (PollErr | PollHup)) != 0)
                {
                    FailAllPending(UrbStatus.DeviceGone, "The device disconnected.");
                    break;
                }

                // Drain everything the kernel has ready before polling again.
                while (Ioctl(_fd, UsbdevfsReapUrbNdelay, reaped) == 0)
                    CompleteUrb(Marshal.ReadIntPtr(reaped));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(reaped);
            FailAllPending(UrbStatus.Cancelled, "The device handle closed.");
        }
    }

    private void CompleteUrb(IntPtr urbPointer)
    {
        if (!_pending.TryRemove(urbPointer, out var pending)) return;
        _byUrbId.TryRemove(pending.UrbId, out _);

        try
        {
            var status = Marshal.ReadInt32(urbPointer, UrbStatusOffset);
            var actualLength = Marshal.ReadInt32(urbPointer, UrbActualLengthOffset);
            if (actualLength < 0) actualLength = 0;
            if (actualLength > pending.DataCapacity) actualLength = pending.DataCapacity;

            var isoResults = ReadIsoResults(urbPointer, pending.IsoPacketCount);

            var data = Array.Empty<byte>();
            if (pending.IsIn && actualLength > 0)
            {
                data = new byte[actualLength];
                Marshal.Copy(urbPointer + pending.DataOffset, data, 0, actualLength);
            }

            pending.Completion.TrySetResult(new UrbResult
            {
                UrbId = pending.UrbId,
                Status = MapStatus(status, actualLength, pending.DataCapacity),
                ActualLength = actualLength,
                Data = data,
                IsoResults = isoResults,
                Detail = status == 0 ? null : $"usbfs status {status} ({Strerror(-status)})",
            });
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetResult(new UrbResult
            {
                UrbId = pending.UrbId,
                Status = UrbStatus.IoError,
                Detail = ex.Message,
            });
        }
        finally
        {
            Marshal.FreeHGlobal(pending.Block);
        }
    }

    private static IsoPacketResult[] ReadIsoResults(IntPtr urbPointer, int packetCount)
    {
        if (packetCount <= 0) return [];
        var results = new IsoPacketResult[packetCount];
        var baseOffset = UrbSize;
        for (var i = 0; i < packetCount; i++)
        {
            var offset = baseOffset + i * IsoPacketDescSize;
            var actual = Marshal.ReadInt32(urbPointer, offset + 4);
            var status = Marshal.ReadInt32(urbPointer, offset + 8);
            results[i] = new IsoPacketResult(actual, MapStatus(status, actual, actual));
        }
        return results;
    }

    private static UrbStatus MapStatus(int status, int actualLength, int capacity) => status switch
    {
        0 => actualLength < capacity ? UrbStatus.ShortPacket : UrbStatus.Ok,
        -Epipe => UrbStatus.Stalled,
        -Etimedout => UrbStatus.Timeout,
        -Enoent or -Econnreset => UrbStatus.Cancelled,
        -Enodev or -Eshutdown => UrbStatus.DeviceGone,
        -Eoverflow => UrbStatus.Overflow,
        -Eremoteio => UrbStatus.ShortPacket,
        _ => status < 0 ? UrbStatus.IoError : UrbStatus.Ok,
    };

    private void FailAllPending(UrbStatus status, string detail)
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (!_pending.TryRemove(key, out var pending)) continue;
            _byUrbId.TryRemove(pending.UrbId, out _);
            pending.Completion.TrySetResult(new UrbResult { UrbId = pending.UrbId, Status = status, Detail = detail });
            Marshal.FreeHGlobal(pending.Block);
        }
    }

    public Task<UrbResult> SubmitAsync(UrbRequest request, CancellationToken cancellationToken)
    {
        var isControl = request.TransferType == UsbTransferType.Control;
        var isIn = request.IsIn;
        var isoCount = request.TransferType == UsbTransferType.Isochronous ? request.IsoPacketLengths.Length : 0;

        // Control transfers carry their SETUP packet inside the data buffer.
        var payloadLength = isControl
            ? 8 + (isIn ? request.ExpectedLength : request.Data.Length)
            : (isIn ? request.ExpectedLength : request.Data.Length);
        if (isoCount > 0) payloadLength = request.IsoPacketLengths.Sum();
        if (payloadLength < 0) payloadLength = 0;

        var isoBytes = isoCount * IsoPacketDescSize;
        var dataOffset = UrbSize + isoBytes;
        var block = Marshal.AllocHGlobal(dataOffset + Math.Max(payloadLength, 1));

        try
        {
            // Zero the header so no stale bytes reach the kernel.
            for (var i = 0; i < dataOffset; i++) Marshal.WriteByte(block, i, 0);

            Marshal.WriteByte(block, 0, request.TransferType switch
            {
                UsbTransferType.Control => UrbTypeControl,
                UsbTransferType.Isochronous => UrbTypeIsochronous,
                UsbTransferType.Interrupt => UrbTypeInterrupt,
                _ => UrbTypeBulk,
            });
            Marshal.WriteByte(block, 1, isControl ? (byte)0 : request.Endpoint);
            Marshal.WriteIntPtr(block, UrbBufferOffset, block + dataOffset);
            Marshal.WriteInt32(block, UrbBufferLengthOffset, payloadLength);
            Marshal.WriteInt32(block, UrbNumberOfPacketsOffset, isoCount);

            if (isControl)
            {
                Marshal.Copy(request.Setup, 0, block + dataOffset, 8);
                if (!isIn && request.Data.Length > 0)
                    Marshal.Copy(request.Data, 0, block + dataOffset + 8, request.Data.Length);
            }
            else if (!isIn && request.Data.Length > 0)
            {
                Marshal.Copy(request.Data, 0, block + dataOffset, Math.Min(request.Data.Length, payloadLength));
            }

            for (var i = 0; i < isoCount; i++)
                Marshal.WriteInt32(block, UrbSize + i * IsoPacketDescSize, request.IsoPacketLengths[i]);

            var completion = new TaskCompletionSource<UrbResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingUrb(block, dataOffset, payloadLength, isoCount, isIn, request.UrbId, completion);

            _pending[block] = pending;
            _byUrbId[request.UrbId] = block;

            if (Ioctl(_fd, UsbdevfsSubmitUrb, block) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                _pending.TryRemove(block, out _);
                _byUrbId.TryRemove(request.UrbId, out _);
                Marshal.FreeHGlobal(block);
                return Task.FromResult(new UrbResult
                {
                    UrbId = request.UrbId,
                    Status = errno switch
                    {
                        Enodev => UrbStatus.DeviceGone,
                        Einval => UrbStatus.NotSupported,
                        Ebusy => UrbStatus.IoError,
                        _ => UrbStatus.IoError,
                    },
                    Detail = $"USBDEVFS_SUBMITURB failed: {Strerror(errno)} (errno {errno}).",
                });
            }

            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => TryCancel(request.UrbId));

            return completion.Task;
        }
        catch
        {
            Marshal.FreeHGlobal(block);
            throw;
        }
    }

    public bool TryCancel(ulong urbId)
        => _byUrbId.TryGetValue(urbId, out var block) && Ioctl(_fd, UsbdevfsDiscardUrb, block) == 0;

    private void SimpleIoctl(uint request, uint argument, string what)
    {
        var block = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(block, unchecked((int)argument));
            if (Ioctl(_fd, request, block) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new UsbBackendException($"{what} failed: {Strerror(errno)} (errno {errno}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Ioctl(_fd, UsbdevfsReset, IntPtr.Zero) < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new UsbBackendException($"USBDEVFS_RESET failed: {Strerror(errno)} (errno {errno}).");
        }
    }, cancellationToken);

    public Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken)
        => Task.Run(() => SimpleIoctl(UsbdevfsSetConfiguration, configurationValue, "USBDEVFS_SETCONFIGURATION"), cancellationToken);

    public Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken) => Task.Run(() =>
    {
        SimpleIoctl(UsbdevfsClaimInterface, interfaceNumber, $"USBDEVFS_CLAIMINTERFACE({interfaceNumber})");
        lock (_claimLock) _claimedInterfaces.Add(interfaceNumber);
    }, cancellationToken);

    public Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken) => Task.Run(() =>
    {
        SimpleIoctl(UsbdevfsReleaseInterface, interfaceNumber, $"USBDEVFS_RELEASEINTERFACE({interfaceNumber})");
        lock (_claimLock) _claimedInterfaces.Remove(interfaceNumber);
    }, cancellationToken);

    public Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var block = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(block, 0, interfaceNumber);
            Marshal.WriteInt32(block, 4, alternateSetting);
            if (Ioctl(_fd, UsbdevfsSetInterface, block) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new UsbBackendException($"USBDEVFS_SETINTERFACE failed: {Strerror(errno)} (errno {errno}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }, cancellationToken);

    public Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken)
        => Task.Run(() => SimpleIoctl(UsbdevfsClearHalt, endpointAddress, $"USBDEVFS_CLEAR_HALT(0x{endpointAddress:x2})"), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdown.Cancel();
        _reaper.Join(TimeSpan.FromSeconds(2));

        byte[] claimed;
        lock (_claimLock) claimed = _claimedInterfaces.ToArray();
        foreach (var iface in claimed)
        {
            try { SimpleIoctl(UsbdevfsReleaseInterface, iface, "release"); }
            catch (UsbBackendException) { /* the device may already be gone */ }
        }

        FailAllPending(UrbStatus.Cancelled, "The device handle closed.");
        LinuxNative.Close(_fd);
        _shutdown.Dispose();
    }
}
