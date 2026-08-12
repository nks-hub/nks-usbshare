using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NksUsbShare.Core.Usb;

/// <summary>Turns protocol values into the words a person reads, shared by every front end.</summary>
public static class UsbDisplay
{
    public static string Speed(UsbSpeed speed) => speed switch
    {
        UsbSpeed.Low => "1.5 Mbit/s",
        UsbSpeed.Full => "12 Mbit/s",
        UsbSpeed.High => "480 Mbit/s",
        UsbSpeed.Super => "5 Gbit/s",
        UsbSpeed.SuperPlus => "10 Gbit/s",
        _ => "unknown",
    };

    /// <summary>
    /// What kind of thing this is, from the device class or, when the device defers with class 0,
    /// from its first interface.
    /// </summary>
    public static string Kind(UsbDeviceInfo device)
    {
        var deviceClass = device.DeviceClass != 0
            ? device.DeviceClass
            : device.Interfaces.Select(i => i.InterfaceClass).FirstOrDefault();

        return deviceClass switch
        {
            0x01 => "Audio",
            0x02 => "Communications",
            0x03 => "Input device",
            0x05 => "Physical",
            0x06 => "Camera",
            0x07 => "Printer",
            0x08 => "Storage",
            0x09 => "Hub",
            0x0A => "Data",
            0x0B => "Smart card",
            0x0D => "Content security",
            0x0E => "Video",
            0x0F => "Health",
            0x10 => "Audio/video",
            0xE0 => "Wireless",
            0xEF => "Composite",
            0xFE => "Application specific",
            0xFF => "Vendor specific",
            _ => "Unclassified",
        };
    }

    /// <summary>Addresses a client could reach this machine on, for the pairing panel.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Some sandboxes refuse to enumerate interfaces; fewer options is not a failure.
        }

        return addresses.Distinct().OrderBy(a => a, StringComparer.Ordinal).ToArray();
    }
}
