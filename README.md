# NKS UsbShare

Share USB devices between computers over an encrypted TCP link — on a LAN, across a VPN, or over
the internet. The device plugged into one machine becomes usable from another.

Written in C# on .NET 9 with **no third-party dependencies**. Everything it uses ships with the
operating system or with .NET itself: WinUSB and SetupAPI on Windows, usbfs on Linux, IOKit on
macOS, WPF for the Windows interface, and the .NET TLS stack for the transport.

## What works today

| | Windows | Linux | macOS |
|---|---|---|---|
| Enumerate local USB devices | verified | implemented | verified |
| Share a device (export) | verified | implemented | not implemented |
| Use a remote device from an application | verified | verified | verified |
| Present a remote device to the whole OS | needs a kernel driver | via in-kernel `vhci_hcd` | needs a kernel driver |
| Native application window | WPF | planned (GTK) | planned (AppKit) |
| Command line | verified | implemented | implemented |

"Verified" means it was exercised against real hardware; "implemented" means the code is complete
but has not yet been run on that platform.

The Windows export path was checked end to end: a USB flash drive plugged into one machine was
rebound to WinUSB with `tools/bind-winusb.ps1`, shared, and then read from a Windows 11 virtual
machine on another host across a VPN. The virtual machine pulled the device and configuration
descriptors straight off the hardware — mass storage, class 08/06/50, two 512 byte bulk endpoints
— proving real transfers cross the encrypted link rather than cached metadata.

```
nksusb probe 10.0.0.7 'USB\VID_FFFF&PID_5678\9207014AB5258217200'

  Disk 2.0  (ffff:5678)
  device descriptor (18 bytes)
    1201000200000040FFFF7856000201020301
    usb 2.00   vendor ffff   product 5678   class 00   max packet 64
  configuration descriptor, 32 bytes total
    interface 0 alt 0  class 08/06/50
      endpoint 0x01  Bulk        512 bytes
      endpoint 0x82  Bulk        512 bytes
```

## How it fits together

```
   machine A (host)                              machine B (client)
   ┌───────────────────────┐                     ┌───────────────────────┐
   │ USB device            │                     │ application           │
   │   ↓ WinUSB / usbfs    │                     │   ↑ RemoteUsbDevice   │
   │ NodeService           │  TLS 1.3, port      │ NodeService           │
   │   HostServer  ────────┼─────  57340  ───────┼──→ UsbShareClient     │
   └───────────────────────┘                     └───────────────────────┘
```

Transfers cross the link as URBs: the client submits a request describing an endpoint, a transfer
type and a payload; the host issues it through the operating system and sends back the result.
Control, bulk and interrupt transfers are carried; isochronous is carried by the protocol and
implemented on the Linux backend.

### Security

- **TLS 1.3** on every connection, before a single protocol byte is exchanged.
- **Public-key pinning.** Each node generates a self-signed P-384 certificate on first run. A
  client pins the host's key fingerprint the first time it connects and refuses to continue if it
  ever changes. There is no certificate authority, because there is no third party in the link.
- **Pairing tokens.** 125 bits of entropy, compared in constant time, shown in groups of five
  characters with the ambiguous ones removed so they can be read aloud.
- **Per-device access control.** A share can be restricted to named client fingerprints; the
  default is any client that knows the pairing token.
- **Optional mutual TLS**, where clients must also present a key the host already knows.

## Using it

### Desktop application (Windows)

```
dotnet build src/NksUsbShare.Desktop
```

Run `NksUsbShare.exe`. The first tab lists local devices and shares them, the second pairs with
other computers and uses their devices, and the connection details panel carries the pairing token
and key fingerprint the other side needs.

### Command line (all platforms)

```
dotnet build src/NksUsbShare.Cli

nksusb list                          # local devices and what is shared
nksusb share 3                       # share the third device in the list
nksusb serve                         # run the node until interrupted

nksusb pair 192.0.2.10 ABCDE-FGHIJ-KLMNO-PQRST-UVWXY
nksusb remote 192.0.2.10             # what that host offers
nksusb import 192.0.2.10 <device-id> # hold a device until interrupted
```

Settings live in `%APPDATA%\nks-usbshare` or `~/.config/nks-usbshare`, overridable with
`NKS_USBSHARE_HOME`.

## The driver question

Two things in the table above are blocked on the same fact: **only kernel-mode code can take a USB
device away from its class driver, or present a virtual one.** This is not an implementation gap
that more user-mode code can close.

**Sharing on Windows** requires the device to be bound to the in-box `winusb.sys` instead of its
class driver. `tools/bind-winusb.ps1` does this, and it needs nothing that is not already on the
machine: `New-SelfSignedCertificate` for a signing key, `New-FileCatalog` and
`Set-AuthenticodeSignature` for the driver catalog, and `pnputil` for the install. No WDK, no
third-party signing tool, and **Secure Boot stays on** — `winusb.sys` is already signed by
Microsoft, so only the small package that points at it has to be trusted.

```
tools\bind-winusb.ps1 -HardwareId 'USB\VID_FFFF&PID_5678&REV_0200'   # hand it over
tools\bind-winusb.ps1 -Status                                        # what is bound
tools\bind-winusb.ps1 -Restore                                       # give it back
```

It needs administrator rights, and the device stops working as its normal self for as long as it
is shared — a flash drive disappears from Explorer, a keyboard stops typing. That is what sharing
a USB device means on every product that does this. `-Restore` removes the package and puts the
original class driver back.

**Using a remote device as though it were plugged in locally** needs a virtual USB host controller:

- **Linux** already has one in the kernel, `vhci_hcd`. Nothing has to be written or signed.
- **Windows** has no in-box virtual device, but it does ship the framework to build one:
  [`UdeCx`](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/writing-a-ude-client-driver),
  the USB Device Emulation class extension, present since Windows 10 1903. A KMDF client driver
  registers an emulated host controller, supplies the descriptors, and completes URBs — the same
  transfer forwarding this project already does for export, running in the opposite direction.
  The driver has to be signed to load: [OSSign](https://ossign.org/) signs open-source drivers at
  no cost through Microsoft Trusted Signing, and Microsoft Trusted Signing itself is roughly $10 a
  month for closed projects. Either produces a driver that installs on stock Windows 11 with Secure
  Boot left on. Test-signing works for development but forces Secure Boot off, so it is not a
  distribution route.
- **macOS** cannot do this at all today. DriverKit deliberately drives real hardware only —
  `USBDriverKit` matches on the vendor and product id of a device that is physically present, and
  Apple applies the same policy elsewhere, explicitly refusing `AudioDriverKit` for virtual audio
  devices. The one class Apple does permit to be invented is HID, through `HIDDriverKit`'s virtual
  device entitlement. Everything else means a kernel extension, which on Apple Silicon requires the
  user to drop Startup Security to "Reduced Security" from Recovery. Every commercial product in
  this space still ships a kext on macOS for the same reason.

Until that exists on Windows and macOS, a remote device on those platforms is reachable through
`RemoteUsbDevice` — real control, bulk and interrupt transfers from your own code or the command
line — but the operating system's own driver stack does not see it.

A driver is only unavoidable for *arbitrary* devices. Storage, printers and scanners each have a
network protocol both operating systems already speak — iSCSI, IPP, and eSCL — so those three
classes could be carried with no kernel component anywhere.

## Repository layout

```
src/NksUsbShare.Core      protocol, security, USB backends, host and client engines
src/NksUsbShare.Cli       cross-platform command line
src/NksUsbShare.Desktop   WPF application for Windows
src/NksUsbShare.Mcp       management server speaking the Model Context Protocol
tests/NksUsbShare.Tests   test suite
```

## Tests

```
dotnet run --project tests/NksUsbShare.Tests
```

No test framework is used, for the same reason nothing else is: tests are plain methods, failures
are exceptions, and the exit code is what matters. The suite covers the wire protocol, descriptor
parsing, the TLS and pairing layer, and a full host-to-client round trip over a real socket.
Tests that need hardware skip themselves when there is none.
