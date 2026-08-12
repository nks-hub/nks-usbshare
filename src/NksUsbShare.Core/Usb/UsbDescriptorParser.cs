namespace NksUsbShare.Core.Usb;

/// <summary>
/// Walks a raw USB configuration descriptor blob into interface and endpoint records.
/// The layout is defined by the USB specification and is identical on every platform,
/// so all backends share this parser.
/// </summary>
public static class UsbDescriptorParser
{
    private const byte InterfaceDescriptorType = 0x04;
    private const byte EndpointDescriptorType = 0x05;

    /// <summary>
    /// Parses the descriptor chain that follows a configuration descriptor.
    /// Unknown descriptor types (HID, class specific, audio) are skipped, which is what the
    /// spec requires: every descriptor is length prefixed precisely so parsers can walk past them.
    /// </summary>
    public static IReadOnlyList<UsbInterfaceInfo> ParseConfiguration(ReadOnlySpan<byte> configuration)
    {
        var interfaces = new List<UsbInterfaceInfo>();

        byte? currentNumber = null;
        byte currentAlt = 0, currentClass = 0, currentSubClass = 0, currentProtocol = 0;
        var currentEndpoints = new List<UsbEndpointInfo>();

        void Flush()
        {
            if (currentNumber is not { } number) return;
            interfaces.Add(new UsbInterfaceInfo(
                number, currentAlt, currentClass, currentSubClass, currentProtocol, currentEndpoints.ToArray()));
            currentEndpoints.Clear();
        }

        var offset = 0;
        while (offset + 2 <= configuration.Length)
        {
            var length = configuration[offset];
            var type = configuration[offset + 1];

            // A zero length would spin forever, and a run past the end means a truncated blob.
            if (length < 2 || offset + length > configuration.Length) break;

            switch (type)
            {
                case InterfaceDescriptorType when length >= 9:
                    Flush();
                    currentNumber = configuration[offset + 2];
                    currentAlt = configuration[offset + 3];
                    currentClass = configuration[offset + 5];
                    currentSubClass = configuration[offset + 6];
                    currentProtocol = configuration[offset + 7];
                    break;

                case EndpointDescriptorType when length >= 7 && currentNumber is not null:
                    currentEndpoints.Add(new UsbEndpointInfo(
                        Address: configuration[offset + 2],
                        TransferType: (UsbTransferType)(configuration[offset + 3] & 0x03),
                        MaxPacketSize: (ushort)(configuration[offset + 4] | (configuration[offset + 5] << 8)),
                        Interval: configuration[offset + 6]));
                    break;
            }

            offset += length;
        }

        Flush();
        return interfaces;
    }

    /// <summary>Total length field of a configuration descriptor header, or 0 when the header is unusable.</summary>
    public static int ReadConfigurationTotalLength(ReadOnlySpan<byte> configurationHeader)
        => configurationHeader.Length < 4 ? 0 : configurationHeader[2] | (configurationHeader[3] << 8);

    /// <summary>Decodes a USB string descriptor (UTF-16LE payload after a two byte header).</summary>
    public static string DecodeStringDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 2) return "";
        var length = Math.Min((int)descriptor[0], descriptor.Length);
        return length <= 2 ? "" : System.Text.Encoding.Unicode.GetString(descriptor[2..length]).TrimEnd('\0');
    }
}
