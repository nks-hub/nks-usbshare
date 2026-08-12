using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NksUsbShare.Core.Usb.MacOs.IoKitInterop;

namespace NksUsbShare.Core.Usb.MacOs;

/// <summary>
/// Drives one device through IOKit's USB plug-in interfaces. Control transfers go to the device
/// interface; bulk and interrupt transfers go to the interface that owns the endpoint.
///
/// IOKit's synchronous calls block, so every transfer runs on a thread pool thread and is
/// cancelled out of band with AbortPipe, which is the only interruption IOKit offers.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacUsbDeviceHandle : IUsbDeviceHandle
{
    private readonly IntPtr _device;
    private readonly List<IntPtr> _interfaces = [];

    /// <summary>Endpoint address to the interface that owns it and its pipe reference.</summary>
    private readonly Dictionary<byte, (IntPtr Interface, byte Pipe)> _pipes = [];

    private readonly Dictionary<byte, IntPtr> _interfacesByNumber = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _inFlight = new();
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly Dictionary<byte, SemaphoreSlim> _pipeLocks = [];
    private int _disposed;

    public UsbDeviceInfo Info { get; private set; }

    private MacUsbDeviceHandle(IntPtr device, UsbDeviceInfo info)
    {
        _device = device;
        Info = info;
    }

    public static MacUsbDeviceHandle Open(uint service, UsbDeviceInfo info)
    {
        var device = CreateInterface(service, DeviceUserClientType, DeviceInterfaceId);

        var status = CallOpen(device, Device.Open);
        if (status != 0)
        {
            Release(device);
            throw new UsbBackendException(
                $"Could not open {info.DisplayName}: {Describe(status)}. " +
                "Another driver may hold it, or the process may need to run with more privilege.");
        }

        var handle = new MacUsbDeviceHandle(device, info);
        try
        {
            handle.OpenInterfaces();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Walks the CFPlugIn dance: create the plug-in, then ask it for the interface we want.</summary>
    private static IntPtr CreateInterface(uint service, CFUUIDBytes userClientType, CFUUIDBytes interfaceId)
    {
        var status = IOCreatePlugInInterfaceForService(service, userClientType, PlugInInterfaceId,
            out var plugin, out _);

        if (status != 0 || plugin == IntPtr.Zero)
            throw new UsbBackendException($"IOCreatePlugInInterfaceForService failed: {Describe(status)}.");

        try
        {
            var result = QueryInterface(plugin, interfaceId, out var target);
            if (result != 0 || target == IntPtr.Zero)
                throw new UsbBackendException($"QueryInterface for the USB interface failed with 0x{result:X8}.");

            return target;
        }
        finally
        {
            Release(plugin);
        }
    }

    /// <summary>
    /// Claims every interface the device exposes and records where each endpoint lives, so a
    /// transfer can later be routed to the right pipe without searching.
    /// </summary>
    private void OpenInterfaces()
    {
        var request = InterfaceRequest.Any;
        var status = CallCreateInterfaceIterator(_device, ref request, out var iterator);
        if (status != 0) throw new UsbBackendException($"CreateInterfaceIterator failed: {Describe(status)}.");

        var interfaces = new List<UsbInterfaceInfo>();

        try
        {
            for (var service = IOIteratorNext(iterator); service != 0; service = IOIteratorNext(iterator))
            {
                try
                {
                    var usbInterface = CreateInterface(service, InterfaceUserClientType, InterfaceInterfaceId);

                    var opened = CallOpen(usbInterface, Interface.Open);
                    if (opened != 0)
                    {
                        // A class driver still owns this interface; the rest may still be usable.
                        Release(usbInterface);
                        continue;
                    }

                    _interfaces.Add(usbInterface);

                    CallGetInterfaceNumber(usbInterface, out var number);
                    _interfacesByNumber[number] = usbInterface;

                    interfaces.Add(new UsbInterfaceInfo(number, 0, 0, 0, 0, MapPipes(usbInterface)));
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        if (_interfaces.Count == 0)
            throw new UsbBackendException(
                $"Every interface of {Info.DisplayName} is held by another driver, so it cannot be shared.");

        // The descriptors read at enumeration time are sparse on macOS, so fill in what the
        // pipes just told us. The importing side needs this to know each endpoint's type.
        Info = Info with { Interfaces = interfaces };
    }

    private List<UsbEndpointInfo> MapPipes(IntPtr usbInterface)
    {
        var endpoints = new List<UsbEndpointInfo>();
        if (CallGetNumEndpoints(usbInterface, out var count) != 0) return endpoints;

        // Pipe references are one based; zero is the default control pipe.
        for (byte pipe = 1; pipe <= count; pipe++)
        {
            if (CallGetPipeProperties(usbInterface, pipe, out var direction, out var number,
                    out var transferType, out var maxPacketSize, out var interval) != 0)
                continue;

            var address = (byte)(number | (direction == 1 ? 0x80 : 0x00));
            _pipes[address] = (usbInterface, pipe);
            _pipeLocks[address] = new SemaphoreSlim(1, 1);

            endpoints.Add(new UsbEndpointInfo(address, (UsbTransferType)(transferType & 0x03), maxPacketSize, interval));
        }

        return endpoints;
    }

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
                _ => new UrbResult
                {
                    UrbId = request.UrbId,
                    Status = UrbStatus.NotSupported,
                    Detail = "Isochronous transfers are not wired up on the IOKit backend.",
                },
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

        var requestType = request.Setup[0];
        var isIn = (requestType & 0x80) != 0;
        var length = (ushort)(request.Setup[6] | (request.Setup[7] << 8));

        var buffer = Marshal.AllocHGlobal(Math.Max((int)length, 1));
        _controlLock.Wait(cancellationToken);
        try
        {
            if (!isIn && request.Data.Length > 0)
                Marshal.Copy(request.Data, 0, buffer, Math.Min(request.Data.Length, length));

            var deviceRequest = new DeviceRequest
            {
                RequestType = requestType,
                Request = request.Setup[1],
                Value = (ushort)(request.Setup[2] | (request.Setup[3] << 8)),
                Index = (ushort)(request.Setup[4] | (request.Setup[5] << 8)),
                Length = length,
                Data = length == 0 ? IntPtr.Zero : buffer,
            };

            var status = CallDeviceRequest(_device, ref deviceRequest);
            if (status != 0) return FromIoKit(request.UrbId, status);

            var actual = (int)Math.Min(deviceRequest.CompletedLength, length);
            var data = Array.Empty<byte>();
            if (isIn && actual > 0)
            {
                data = new byte[actual];
                Marshal.Copy(buffer, data, 0, actual);
            }

            return new UrbResult
            {
                UrbId = request.UrbId,
                Status = UrbStatus.Ok,
                ActualLength = actual,
                Data = data,
            };
        }
        finally
        {
            _controlLock.Release();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private UrbResult PipeTransfer(UrbRequest request, CancellationToken cancellationToken)
    {
        if (!_pipes.TryGetValue(request.Endpoint, out var pipe))
            return new UrbResult
            {
                UrbId = request.UrbId,
                Status = UrbStatus.NotSupported,
                Detail = $"Endpoint 0x{request.Endpoint:x2} is not exposed by any claimed interface.",
            };

        var gate = _pipeLocks[request.Endpoint];
        gate.Wait(cancellationToken);
        _inFlight[request.UrbId] = request.Endpoint;

        try
        {
            if ((request.Endpoint & 0x80) != 0)
            {
                var buffer = new byte[Math.Max(request.ExpectedLength, 1)];
                var size = (uint)request.ExpectedLength;

                var status = CallReadPipe(pipe.Interface, pipe.Pipe, buffer, ref size);
                if (status != 0) return FromIoKit(request.UrbId, status);

                return new UrbResult
                {
                    UrbId = request.UrbId,
                    Status = size < request.ExpectedLength ? UrbStatus.ShortPacket : UrbStatus.Ok,
                    ActualLength = (int)size,
                    Data = buffer.AsSpan(0, (int)size).ToArray(),
                };
            }

            var payload = request.Data.Length == 0 ? new byte[1] : request.Data;
            var written = CallWritePipe(pipe.Interface, pipe.Pipe, payload, (uint)request.Data.Length);
            if (written != 0) return FromIoKit(request.UrbId, written);

            return new UrbResult { UrbId = request.UrbId, Status = UrbStatus.Ok, ActualLength = request.Data.Length };
        }
        finally
        {
            _inFlight.TryRemove(request.UrbId, out _);
            gate.Release();
        }
    }

    private static UrbResult FromIoKit(ulong urbId, int status) => new()
    {
        UrbId = urbId,
        Status = (uint)status switch
        {
            0xE00002ED => UrbStatus.Stalled,
            0xE00002C7 => UrbStatus.Timeout,
            0xE00002EB => UrbStatus.Cancelled,
            0xE00002C1 => UrbStatus.DeviceGone,
            0xE00002BE => UrbStatus.NotSupported,
            _ => UrbStatus.IoError,
        },
        Detail = Describe(status),
    };

    public bool TryCancel(ulong urbId)
    {
        if (!_inFlight.TryGetValue(urbId, out var endpoint)) return false;
        if (!_pipes.TryGetValue(endpoint, out var pipe)) return false;
        return CallPipeOperation(pipe.Interface, Interface.AbortPipe, pipe.Pipe) == 0;
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var status = CallOpen(_device, Device.ResetDevice);
        if (status != 0) throw new UsbBackendException($"ResetDevice failed: {Describe(status)}.");
    }, cancellationToken);

    public Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var status = CallWithByte(_device, Device.SetConfiguration, configurationValue);
        if (status != 0) throw new UsbBackendException($"SetConfiguration failed: {Describe(status)}.");
    }, cancellationToken);

    public Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken)
    {
        // Every interface was claimed when the device was opened, so this is a presence check.
        if (!_interfacesByNumber.ContainsKey(interfaceNumber))
            throw new UsbBackendException($"Interface {interfaceNumber} is not claimed on this device.");
        return Task.CompletedTask;
    }

    public Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (!_interfacesByNumber.TryGetValue(interfaceNumber, out var usbInterface))
                throw new UsbBackendException($"Interface {interfaceNumber} is not claimed on this device.");

            var status = CallSetAlternateInterface(usbInterface, alternateSetting);
            if (status != 0) throw new UsbBackendException($"SetAlternateInterface failed: {Describe(status)}.");
        }, cancellationToken);

    public Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!_pipes.TryGetValue(endpointAddress, out var pipe))
            throw new UsbBackendException($"Endpoint 0x{endpointAddress:x2} is not present on this device.");

        var status = CallPipeOperation(pipe.Interface, Interface.ClearPipeStall, pipe.Pipe);
        if (status != 0) throw new UsbBackendException($"ClearPipeStall failed: {Describe(status)}.");
    }, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var usbInterface in _interfaces)
        {
            CallOpen(usbInterface, Interface.Close);
            Release(usbInterface);
        }
        _interfaces.Clear();

        CallOpen(_device, Device.Close);
        Release(_device);

        _controlLock.Dispose();
        foreach (var gate in _pipeLocks.Values) gate.Dispose();
    }
}
