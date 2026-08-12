using System.Runtime.InteropServices;

namespace NksUsbShare.Core.Usb;

/// <summary>
/// An opened USB device the exporter drives on behalf of a remote importer.
/// Every method maps onto a real OS call; nothing here is simulated.
/// </summary>
public interface IUsbDeviceHandle : IDisposable
{
    UsbDeviceInfo Info { get; }

    Task<UrbResult> SubmitAsync(UrbRequest request, CancellationToken cancellationToken);

    /// <summary>Best-effort abort of an in-flight URB. Returns false when the backend cannot cancel.</summary>
    bool TryCancel(ulong urbId);

    Task ResetAsync(CancellationToken cancellationToken);
    Task SetConfigurationAsync(byte configurationValue, CancellationToken cancellationToken);
    Task ClaimInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken);
    Task ReleaseInterfaceAsync(byte interfaceNumber, CancellationToken cancellationToken);
    Task SetAltSettingAsync(byte interfaceNumber, byte alternateSetting, CancellationToken cancellationToken);
    Task ClearHaltAsync(byte endpointAddress, CancellationToken cancellationToken);
}

/// <summary>Platform specific USB access. One implementation per OS, chosen by <see cref="UsbBackend.Create"/>.</summary>
public interface IUsbBackend
{
    string Name { get; }

    /// <summary>Everything the OS currently reports on the local buses.</summary>
    Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken);

    /// <summary>Opens and exclusively claims a device so its transfers can be forwarded.</summary>
    Task<IUsbDeviceHandle> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

public static class UsbBackend
{
    /// <summary>Returns the backend for the running OS, or throws when the platform has no implementation.</summary>
    public static IUsbBackend Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new Windows.WindowsUsbBackend();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new Linux.LinuxUsbBackend();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacOs.MacUsbBackend();
        throw new PlatformNotSupportedException(
            $"No USB backend for {RuntimeInformation.OSDescription}. Supported: Windows, Linux, macOS.");
    }

    public static string DescribeCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows (WinUSB)";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux (usbfs)";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS (IOKit)";
        return RuntimeInformation.OSDescription;
    }
}
