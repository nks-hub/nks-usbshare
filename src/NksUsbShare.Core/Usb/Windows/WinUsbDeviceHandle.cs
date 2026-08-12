using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static NksUsbShare.Core.Usb.Windows.WindowsNative;

namespace NksUsbShare.Core.Usb.Windows;

/// <summary>
/// Drives one device through WinUSB. Transfers are issued synchronously on thread pool threads
/// (WinUSB runs synchronously when no OVERLAPPED is supplied) and cancelled out of band with
/// WinUsb_AbortPipe, which is the only mechanism WinUSB offers for aborting an in-flight request.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinUsbDeviceHandle : IUsbDeviceHandle
{
    private readonly SafeFileHandle _deviceHandle;
    private readonly IntPtr _rootInterface;
    private readonly List<IntPtr> _associatedInterfaces = [];

    /// <summary>Endpoint address to the WinUSB interface handle that owns that pipe.</summary>
    private readonly Dictionary<byte, IntPtr> _pipeOwners = [];

    /// <summary>Interface number to its WinUSB handle.</summary>
    private readonly Dictionary<byte, IntPtr> _interfaceHandles = [];

    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _pipeLocks = [];
    private readonly ConcurrentDictionary<ulong, byte> _inFlight = [];
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private int _disposed;

    public UsbDeviceInfo Info { get; }

    private WinUsbDeviceHandle(SafeFileHandle deviceHandle, IntPtr rootInterface, UsbDeviceInfo info)
    {
        _deviceHandle = deviceHandle;
        _rootInterface = rootInterface;
        Info = info;
    }

    public static WinUsbDeviceHandle Open(string devicePath, UsbDeviceInfo info)
    {
        var handle = CreateFile(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new UsbBackendException($"CreateFile on '{devicePath}' failed (Win32 {error}). " +
                "The device is either in use by another process or not bound to winusb.sys.");
        }

        if (!WinUsb_Initialize(handle, out var rootInterface))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new UsbBackendException($"WinUsb_Initialize failed (Win32 {error}). The device is not bound to winusb.sys.");
        }

        var result = new WinUsbDeviceHandle(handle, rootInterface, info);
        try
        {
            result.MapPipes();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Walks the root interface plus every associated interface, recording which handle owns which pipe.
    /// WinUSB requires transfers to be issued on the handle of the interface that exposes the endpoint.
    /// </summary>
    private void MapPipes()
    {
        RegisterInterface(_rootInterface);

        for (byte index = 0; ; index++)
        {
            if (!WinUsb_GetAssociatedInterface(_rootInterface, index, out var associated)) break;
            _associatedInterfaces.Add(associated);
            RegisterInterface(associated);
        }
    }

    private void RegisterInterface(IntPtr interfaceHandle)
    {
        if (!WinUsb_QueryInterfaceSettings(interfaceHandle, 0, out var descriptor)) return;
        _interfaceHandles[descriptor.bInterfaceNumber] = interfaceHandle;

        for (byte pipeIndex = 0; pipeIndex < descriptor.bNumEndpoints; pipeIndex++)
        {
            if (!WinUsb_QueryPipe(interfaceHandle, 0, pipeIndex, out var pipe)) continue;
            _pipeOwners[pipe.PipeId] = interfaceHandle;
        }
    }

    private SemaphoreSlim PipeLock(byte pipeId) => _pipeLocks.GetOrAdd(pipeId, _ => new SemaphoreSlim(1, 1));

    public Task<UrbResult> SubmitAsync(UrbRequest request, CancellationToken cancellationToken)
        => Task.Run(() => Submit(request, cancellationToken), CancellationToken.None);

    private UrbResult Submit(UrbRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.TransferType switch
            {
                UsbTransferType.Control => ControlTransfer(request, cancellationToken),
                UsbTransferType.Bulk or UsbTransferType.Interrupt => PipeTransfer(request, cancellationToken),
                UsbTransferType.Isochronous => new UrbResult
                {
                    UrbId = request.UrbId,
                    Status = UrbStatus.NotSupported,
                    Detail = "Isochronous transfers are not yet wired up on the WinUSB backend.",
                },
                _ => new UrbResult { UrbId = request.UrbId, Status = UrbStatus.NotSupported, Detail = "Unknown transfer type." },
            };
        }
        catch (OperationCanceledException)
        {
            return new UrbResult { UrbId = request.UrbId, Status = UrbStatus.Cancelled };
        }
        catch (Exception ex)
        {
            return new UrbResult { UrbId = request.UrbId, Status = UrbStatus.IoError, Detail = ex.Message };
        }
    }

    private UrbResult ControlTransfer(UrbRequest request, CancellationToken cancellationToken)
    {
        if (request.Setup.Length < 8)
            return new UrbResult { UrbId = request.UrbId, Status = UrbStatus.IoError, Detail = "Control transfer without a SETUP packet." };

        if (TryHandleLocally(request) is { } handled) return handled;

        var setup = new WinUsbSetupPacket
        {
            RequestType = request.Setup[0],
            Request = request.Setup[1],
            Value = (ushort)(request.Setup[2] | (request.Setup[3] << 8)),
            Index = (ushort)(request.Setup[4] | (request.Setup[5] << 8)),
            Length = (ushort)(request.Setup[6] | (request.Setup[7] << 8)),
        };

        var isIn = (setup.RequestType & 0x80) != 0;
        var buffer = isIn ? new byte[setup.Length] : (byte[])request.Data.Clone();
        if (!isIn && buffer.Length < setup.Length) Array.Resize(ref buffer, setup.Length);

        _controlLock.Wait(cancellationToken);
        _inFlight[request.UrbId] = 0;
        try
        {
            SetTimeout(_rootInterface, 0, request.TimeoutMs);
            if (!WinUsb_ControlTransfer(_rootInterface, setup, buffer, setup.Length, out var transferred, IntPtr.Zero))
                return FromWin32(request.UrbId, Marshal.GetLastWin32Error());

            return new UrbResult
            {
                UrbId = request.UrbId,
                Status = UrbStatus.Ok,
                ActualLength = (int)transferred,
                Data = isIn ? buffer.AsSpan(0, (int)transferred).ToArray() : [],
            };
        }
        finally
        {
            _inFlight.TryRemove(request.UrbId, out _);
            _controlLock.Release();
        }
    }

    private UrbResult PipeTransfer(UrbRequest request, CancellationToken cancellationToken)
    {
        var pipeId = request.Endpoint;
        if (!_pipeOwners.TryGetValue(pipeId, out var owner))
            return new UrbResult
            {
                UrbId = request.UrbId,
                Status = UrbStatus.NotSupported,
                Detail = $"Endpoint 0x{pipeId:x2} is not exposed by any claimed interface.",
            };

        var isIn = (pipeId & 0x80) != 0;
        var gate = PipeLock(pipeId);
        gate.Wait(cancellationToken);
        _inFlight[request.UrbId] = pipeId;
        try
        {
            SetTimeout(owner, pipeId, request.TimeoutMs);

            if (isIn)
            {
                var buffer = new byte[Math.Max(request.ExpectedLength, 1)];
                if (!WinUsb_ReadPipe(owner, pipeId, buffer, (uint)request.ExpectedLength, out var read, IntPtr.Zero))
                    return FromWin32(request.UrbId, Marshal.GetLastWin32Error());

                return new UrbResult
                {
                    UrbId = request.UrbId,
                    Status = read < request.ExpectedLength ? UrbStatus.ShortPacket : UrbStatus.Ok,
                    ActualLength = (int)read,
                    Data = buffer.AsSpan(0, (int)read).ToArray(),
                };
            }

            var payload = request.Data.Length == 0 ? new byte[1] : request.Data;
            if (!WinUsb_WritePipe(owner, pipeId, payload, (uint)request.Data.Length, out var written, IntPtr.Zero))
                return FromWin32(request.UrbId, Marshal.GetLastWin32Error());

            return new UrbResult { UrbId = request.UrbId, Status = UrbStatus.Ok, ActualLength = (int)written };
        }
        finally
        {
            _inFlight.TryRemove(request.UrbId, out _);
            gate.Release();
        }
    }

    /// <summary>
    /// Answers the standard requests that winusb.sys will not pass through, because it owns that
    /// part of the device state itself.
    ///
    /// A remote host stack enumerating this device sends SET_CONFIGURATION, SET_INTERFACE and
    /// CLEAR_FEATURE as ordinary control transfers. Forwarding them to WinUSB fails, so they are
    /// translated into the equivalent WinUSB calls, or acknowledged when the device is already in
    /// the requested state. Returns null when the request should go to the device unchanged.
    /// </summary>
    private UrbResult? TryHandleLocally(UrbRequest request)
    {
        var requestType = request.Setup[0];
        var code = request.Setup[1];
        var value = (ushort)(request.Setup[2] | (request.Setup[3] << 8));
        var index = (ushort)(request.Setup[4] | (request.Setup[5] << 8));

        UrbResult Ok() => new() { UrbId = request.UrbId, Status = UrbStatus.Ok, ActualLength = 0 };

        UrbResult Fail(string detail) =>
            new() { UrbId = request.UrbId, Status = UrbStatus.Stalled, Detail = detail };

        switch (requestType, code)
        {
            // SET_CONFIGURATION, addressed to the device. WinUSB configured the device when it
            // was opened, so the only honest answers are success for the configuration it is
            // already in, and a stall for any other.
            case (0x00, 0x09):
                return (byte)value == Info.ConfigurationValue || value == 0
                    ? Ok()
                    : Fail($"WinUSB keeps this device on configuration {Info.ConfigurationValue}.");

            // SET_INTERFACE, addressed to an interface.
            case (0x01, 0x0B):
            {
                if (!_interfaceHandles.TryGetValue((byte)index, out var handle))
                    return Fail($"Interface {index} is not present on this device.");

                return WinUsb_SetCurrentAlternateSetting(handle, (byte)value)
                    ? Ok()
                    : Fail($"WinUsb_SetCurrentAlternateSetting failed (Win32 {Marshal.GetLastWin32Error()}).");
            }

            // CLEAR_FEATURE(ENDPOINT_HALT), addressed to an endpoint.
            case (0x02, 0x01) when value == 0x0000:
            {
                var endpoint = (byte)index;
                if (!_pipeOwners.TryGetValue(endpoint, out var owner))
                    return Fail($"Endpoint 0x{endpoint:x2} is not present on this device.");

                return WinUsb_ResetPipe(owner, endpoint)
                    ? Ok()
                    : Fail($"WinUsb_ResetPipe failed (Win32 {Marshal.GetLastWin32Error()}).");
            }

            default:
                return null;
        }
    }

    private static void SetTimeout(IntPtr interfaceHandle, byte pipeId, uint timeoutMs)
    {
        var value = timeoutMs;
        WinUsb_SetPipePolicy(interfaceHandle, pipeId, PipeTransferTimeout, sizeof(uint), ref value);
    }

    private static UrbResult FromWin32(ulong urbId, int error) => new()
    {
        UrbId = urbId,
        Status = error switch
        {
            ErrorSemTimeout => UrbStatus.Timeout,
            ErrorGenFailure => UrbStatus.Stalled,
            ErrorOperationAborted => UrbStatus.Cancelled,
            ErrorDeviceNotConnected or ErrorFileNotFound or 433 => UrbStatus.DeviceGone,
            _ => UrbStatus.IoError,
        },
        Detail = $"Win32 error {error}",
    };

    public bool TryCancel(ulong urbId)
    {
        if (!_inFlight.TryGetValue(urbId, out var pipeId)) return false;
        var handle = pipeId == 0 ? _rootInterface : _pipeOwners.GetValueOrDefault(pipeId, _rootInterface);
        return WinUsb_AbortPipe(handle, pipeId);
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        // WinUSB cannot reset a device, only its pipes. Flushing and resetting every known pipe is
        // the strongest reset available without going through the hub driver.
        foreach (var (pipeId, owner) in _pipeOwners)
        {
            WinUsb_AbortPipe(owner, pipeId);
            WinUsb_FlushPipe(owner, pipeId);
            WinUsb_ResetPipe(owner, pipeId);
        }
    }, cancellationToken);

    public Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken)
    {
        // winusb.sys owns configuration selection; a device bound to it is always in its single
        // configured state. Reject rather than silently pretend the request took effect.
        if (configurationValue == Info.ConfigurationValue || configurationValue == 0) return Task.CompletedTask;
        throw new UsbBackendException(
            $"WinUSB does not expose SET_CONFIGURATION; the device stays on configuration {Info.ConfigurationValue}.");
    }

    public Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
    {
        // WinUSB claims every interface of the device at WinUsb_Initialize time, so a claim is a
        // presence check rather than a state change.
        if (!_interfaceHandles.ContainsKey(interfaceNumber))
            throw new UsbBackendException($"Interface {interfaceNumber} is not present on this device.");
        return Task.CompletedTask;
    }

    public Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!_interfaceHandles.TryGetValue(interfaceNumber, out var handle))
            throw new UsbBackendException($"Interface {interfaceNumber} is not present on this device.");
        if (!WinUsb_SetCurrentAlternateSetting(handle, alternateSetting))
            throw new UsbBackendException($"WinUsb_SetCurrentAlternateSetting failed (Win32 {Marshal.GetLastWin32Error()}).");
    }, cancellationToken);

    public Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!_pipeOwners.TryGetValue(endpointAddress, out var owner))
            throw new UsbBackendException($"Endpoint 0x{endpointAddress:x2} is not present on this device.");
        if (!WinUsb_ResetPipe(owner, endpointAddress))
            throw new UsbBackendException($"WinUsb_ResetPipe failed (Win32 {Marshal.GetLastWin32Error()}).");
    }, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var associated in _associatedInterfaces) WinUsb_Free(associated);
        if (_rootInterface != IntPtr.Zero) WinUsb_Free(_rootInterface);
        _deviceHandle.Dispose();
        _controlLock.Dispose();
        foreach (var gate in _pipeLocks.Values) gate.Dispose();
    }
}
