# NKS UsbShare

Share USB devices between computers over an encrypted TCP link — on a LAN, across a VPN, or over
the internet. The device plugged into one machine becomes usable from another.

Written in C# on .NET 9 with **no third-party dependencies**. Everything it uses ships with the
operating system or with .NET itself: WinUSB and SetupAPI on Windows, usbfs on Linux, IOKit on
macOS, WPF for the Windows interface, and the .NET TLS stack for the transport.

## What works today

| | Windows | Linux | macOS |
|---|---|---|---|
| Enumerate local USB devices | verified | verified | verified |
| Share a device (export) | verified | verified | implemented |
| Use a remote device from an application | verified | verified | verified |
| Present a remote device to the whole OS | verified | verified | not possible today |
| Native application window | WPF, verified | planned (GTK) | AppKit, window verified |
| Command line | verified | verified | verified |

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

The import path was then checked the same way. With a virtual controller installed, `nksusb attach`
handed the same flash drive to the virtual machine's own USB stack: Windows enumerated it, bound
its ordinary mass storage driver, and mounted the filesystem as a removable drive. Files written
from the virtual machine landed on the physical device, and a two megabyte round trip came back
byte for byte identical.

```
Port 01: device in use at High Speed(480Mbps)
         unknown vendor : unknown product (ffff:5678)
           -> usbip://127.0.0.1:3240/1-1

Number FriendlyName       BusType   GB   Status
     2 Generic Flash Disk USB     14.9   Online

DriveLetter FileSystemLabel FileSystem   GB
          G 20250512-PL     FAT32      14.9
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

### Desktop application

**Windows** — `dotnet build src/NksUsbShare.Desktop`, then run `NksUsbShare.exe`. The first tab
lists local devices and shares them, the second pairs with other computers and uses their devices,
and the connection details panel carries the pairing token and key fingerprint the other side
needs.

**macOS** — `dotnet publish src/NksUsbShare.Mac -r osx-arm64 --self-contained`. The window is
AppKit, driven straight through the Objective-C runtime, so the application still has no
dependency of its own. Put the binary in a `.app` bundle so LaunchServices treats it as a normal
application.

**Linux** — `dotnet publish src/NksUsbShare.Gtk -r linux-x64 --self-contained`, then run
`nksusb-gtk`. The window is GTK 3 through plain P/Invoke; GTK is already on every Linux desktop,
so nothing new is installed. On a machine with no display it says so and exits rather than
failing obscurely.

All three windows show the same thing in the same shape: identity and pairing details pinned at
the top, then a table of devices with a button per row.

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
class driver. **The applications do this themselves** — sharing a device Windows is holding asks
for administrator approval and then hands it over. Nothing has to be installed for that:
`New-SelfSignedCertificate` provides a signing key, `New-FileCatalog` and
`Set-AuthenticodeSignature` build and sign the driver catalog, and `pnputil` installs it. No WDK,
no third-party signing tool, and **Secure Boot stays on** — `winusb.sys` is already signed by
Microsoft, so only the small package that points at it has to be trusted.

```
nksusb share 7          # claims the device from Windows first if it has to
nksusb giveback         # returns every claimed device to its original driver
```

In the desktop application the button reads *Claim and share*, and *Sharing → Give devices back*
undoes it. The same helper is available on its own as `tools/bind-winusb.ps1` for scripted setups.

While a device is shared it stops working as its normal self: a flash drive disappears from
Explorer, a keyboard stops typing. That is what sharing a USB device means on every product that
does this, and giving it back restores the original driver.

**Using a remote device as though it were plugged in locally** needs a virtual USB host controller.
Only kernel-mode code can create one, so this is the single piece the project does not provide
itself. What it provides instead is everything around it: `nksusb attach` imports the device,
publishes it on a plain USB/IP endpoint bound to **loopback only**, and lets the machine's own
controller attach to that. The network hop is still carried by the encrypted link — the plaintext
protocol never leaves the machine.

```
nksusb attach 10.0.0.7 'USB\VID_FFFF&PID_5678\9207014AB5258217200'
```

Which controller answers depends on the platform:

- **Linux** already has one in the kernel, `vhci_hcd`, driven through the `usbip` tool. Nothing
  has to be written, installed or signed.
- **Windows** has no in-box virtual device. It ships the framework to build one —
  [`UdeCx`](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/writing-a-ude-client-driver),
  the USB Device Emulation class extension — but a KMDF client driver still has to be written and
  signed. Rather than duplicate that,`nksusb attach` works with the controller from
  [usbip-win2](https://github.com/vadimgrn/usbip-win2), which is BSD-2-Clause licensed and ships
  drivers signed by the *Microsoft Windows Hardware Compatibility Publisher*, so they install on
  stock Windows 11 with Secure Boot left on. Install it and `nksusb attach` finds it on its own.
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
src/NksUsbShare.Mac       AppKit application for macOS, on the Objective-C runtime
src/NksUsbShare.Gtk       GTK application for Linux
src/NksUsbShare.Mcp       management server speaking the Model Context Protocol
tests/NksUsbShare.Tests   test suite
tools/bind-winusb.ps1     hands a Windows device to WinUSB so it can be shared, and back again
```

## Tests

```
dotnet run --project tests/NksUsbShare.Tests
```

No test framework is used, for the same reason nothing else is: tests are plain methods, failures
are exceptions, and the exit code is what matters. The suite covers the wire protocol, descriptor
parsing, the TLS and pairing layer, and a full host-to-client round trip over a real socket.
Tests that need hardware skip themselves when there is none.
