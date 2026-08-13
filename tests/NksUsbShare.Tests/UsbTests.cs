using NksUsbShare.Core.Usb;

namespace NksUsbShare.Tests;

public static class UsbTests
{
    /// <summary>
    /// A real configuration descriptor blob: a USB mass storage device with one interface and
    /// two bulk endpoints, taken from the layout the USB specification defines.
    /// </summary>
    private static readonly byte[] MassStorageConfiguration =
    [
        // Configuration descriptor: length 9, type 2, total 32, 1 interface, value 1, ...
        0x09, 0x02, 0x20, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32,
        // Interface descriptor: number 0, alt 0, 2 endpoints, class 08 subclass 06 protocol 50
        0x09, 0x04, 0x00, 0x00, 0x02, 0x08, 0x06, 0x50, 0x00,
        // Endpoint 0x81, bulk, 512 bytes
        0x07, 0x05, 0x81, 0x02, 0x00, 0x02, 0x00,
        // Endpoint 0x02, bulk, 512 bytes
        0x07, 0x05, 0x02, 0x02, 0x00, 0x02, 0x00,
    ];

    public static void Register()
    {
        const string suite = "USB descriptors";

        TestRunner.Add(suite, "configuration descriptor parses into interfaces and endpoints", () =>
        {
            var interfaces = UsbDescriptorParser.ParseConfiguration(MassStorageConfiguration);

            Assert.Equal(1, interfaces.Count, "interface count");
            var iface = interfaces[0];
            Assert.Equal((byte)0x08, iface.InterfaceClass, "mass storage class");
            Assert.Equal((byte)0x50, iface.InterfaceProtocol, "bulk-only protocol");
            Assert.Equal(2, iface.Endpoints.Count, "endpoint count");

            Assert.Equal((byte)0x81, iface.Endpoints[0].Address, "IN endpoint address");
            Assert.True(iface.Endpoints[0].IsIn, "0x81 is an IN endpoint");
            Assert.Equal((ushort)512, iface.Endpoints[0].MaxPacketSize, "IN max packet size");
            Assert.Equal(UsbTransferType.Bulk, iface.Endpoints[0].TransferType, "IN transfer type");

            Assert.False(iface.Endpoints[1].IsIn, "0x02 is an OUT endpoint");
            Assert.Equal((byte)2, iface.Endpoints[1].Number, "OUT endpoint number");
        });

        TestRunner.Add(suite, "total length is read from the configuration header", () =>
            Assert.Equal(0x20, UsbDescriptorParser.ReadConfigurationTotalLength(MassStorageConfiguration), "wTotalLength"));

        TestRunner.Add(suite, "unknown class descriptors are skipped, not misparsed", () =>
        {
            // Interface, then a 9 byte HID descriptor (type 0x21), then an endpoint.
            byte[] withHid =
            [
                0x09, 0x02, 0x22, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32,
                0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x02, 0x00,
                0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x3F, 0x00,
                0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x0A,
            ];

            var interfaces = UsbDescriptorParser.ParseConfiguration(withHid);
            Assert.Equal(1, interfaces.Count, "interface count");
            Assert.Equal(1, interfaces[0].Endpoints.Count, "the endpoint after the HID descriptor was found");
            Assert.Equal(UsbTransferType.Interrupt, interfaces[0].Endpoints[0].TransferType, "interrupt endpoint");
            Assert.Equal((byte)0x0A, interfaces[0].Endpoints[0].Interval, "polling interval");
        });

        TestRunner.Add(suite, "several alternate settings each become their own record", () =>
        {
            byte[] withAlt =
            [
                0x09, 0x02, 0x25, 0x00, 0x02, 0x01, 0x00, 0x80, 0x32,
                0x09, 0x04, 0x01, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
                0x09, 0x04, 0x01, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00,
                0x07, 0x05, 0x82, 0x01, 0xC0, 0x00, 0x01,
            ];

            var interfaces = UsbDescriptorParser.ParseConfiguration(withAlt);
            Assert.Equal(2, interfaces.Count, "both alternate settings are present");
            Assert.Equal((byte)0, interfaces[0].AlternateSetting, "first alt setting");
            Assert.Equal(0, interfaces[0].Endpoints.Count, "alt 0 has no endpoints");
            Assert.Equal((byte)1, interfaces[1].AlternateSetting, "second alt setting");
            Assert.Equal(UsbTransferType.Isochronous, interfaces[1].Endpoints[0].TransferType, "isochronous endpoint");
        });

        TestRunner.Add(suite, "a truncated descriptor blob stops rather than looping", () =>
        {
            // Claims 32 bytes total but the buffer ends mid-endpoint.
            var truncated = MassStorageConfiguration.AsSpan(0, 22).ToArray();
            var interfaces = UsbDescriptorParser.ParseConfiguration(truncated);
            Assert.Equal(1, interfaces.Count, "the interface still parsed");
            Assert.Equal(0, interfaces[0].Endpoints.Count, "the partial endpoint was discarded");
        });

        TestRunner.Add(suite, "a zero-length descriptor cannot cause an infinite loop", () =>
        {
            byte[] malicious = [0x09, 0x02, 0x20, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32, 0x00, 0x04, 0x00];
            var interfaces = UsbDescriptorParser.ParseConfiguration(malicious);
            Assert.Equal(0, interfaces.Count, "parsing stopped at the malformed descriptor");
        });

        TestRunner.Add(suite, "string descriptors decode from UTF-16LE", () =>
        {
            // Length 12, type 3, "SanD" would be 4 chars; use "USB" for clarity.
            byte[] descriptor = [0x08, 0x03, (byte)'U', 0, (byte)'S', 0, (byte)'B', 0];
            Assert.Equal("USB", UsbDescriptorParser.DecodeStringDescriptor(descriptor), "decoded string");
        });

        TestRunner.Add(suite, "an empty string descriptor decodes to an empty string", () =>
            Assert.Equal("", UsbDescriptorParser.DecodeStringDescriptor([0x02, 0x03]), "empty descriptor"));
    }

}
