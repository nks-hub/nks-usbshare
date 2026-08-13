<#
    Binds a USB device to the in-box winusb.sys so it can be shared over the network, and
    restores its original driver afterwards.

    Everything here uses tooling that ships with Windows: New-SelfSignedCertificate for the
    signing key, New-FileCatalog and Set-AuthenticodeSignature for the driver catalog, and
    pnputil for the install. No WDK, no third-party signing tool, and Secure Boot stays on,
    because winusb.sys itself is already signed by Microsoft — only the small driver package
    that points at it has to be trusted.

    Requires administrator rights.

    Examples:
        .\bind-winusb.ps1 -HardwareId 'USB\VID_FFFF&PID_5678&REV_0200'
        .\bind-winusb.ps1 -Restore
        .\bind-winusb.ps1 -Status
#>

[CmdletBinding(DefaultParameterSetName = 'Bind')]
param(
    [Parameter(ParameterSetName = 'Bind', Mandatory = $true)]
    [string]$HardwareId,

    [Parameter(ParameterSetName = 'Bind')]
    [string]$Description = 'NKS UsbShare device',

    [Parameter(ParameterSetName = 'Restore', Mandatory = $true)]
    [switch]$Restore,

    [Parameter(ParameterSetName = 'Status', Mandatory = $true)]
    [switch]$Status
)

$ErrorActionPreference = 'Stop'

$stateDirectory = Join-Path $env:ProgramData 'nks-usbshare'
$stateFile = Join-Path $stateDirectory 'bound-devices.json'
$certificateSubject = 'CN=NKS UsbShare Driver Signing'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator rights are required to change which driver owns a device.'
    }
}

function Get-State {
    if (-not (Test-Path $stateFile)) { return @() }

    $text = (Get-Content $stateFile -Raw).Trim()
    if ($text.Length -eq 0) { return @() }

    try { return @($text | ConvertFrom-Json) } catch { return @() }
}

function Save-State($entries) {
    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null

    # Windows PowerShell's ConvertTo-Json wraps an array in {"value":[...],"Count":n}, which does
    # not read back as a list. Emitting each entry and joining them keeps it a real JSON array.
    $items = @($entries) | ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress }
    $json = '[' + ($items -join ',') + ']'
    Set-Content -Path $stateFile -Value $json -Encoding UTF8
}

# The record of what was claimed can be lost, so the driver store is also searched by provider
# name. That way a device can always be given back.
function Find-OrphanPackages {
    $blocks = (pnputil /enum-drivers 2>&1 | Out-String) -split "`r?`n`r?`n"
    $found = @()

    foreach ($block in $blocks) {
        if ($block -notmatch 'NKS UsbShare') { continue }
        if ($block -match '(oem\d+\.inf)') { $found += $matches[1] }
    }

    return $found
}

# Reuses the machine's signing certificate, creating and trusting it on first use.
function Get-SigningCertificate {
    $existing = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq $certificateSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Select-Object -First 1
    if ($existing) { return $existing }

    Write-Host 'Creating a driver signing certificate for this machine...'
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certificateSubject `
        -KeyUsage DigitalSignature `
        -KeyLength 3072 `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

    # Windows only installs a driver package whose catalog chains to a trusted root and whose
    # signer is a trusted publisher. Both stores are machine-wide, hence the elevation.
    $temporary = Join-Path $env:TEMP 'nksusb-signing.cer'
    Export-Certificate -Cert $certificate -FilePath $temporary | Out-Null
    Import-Certificate -FilePath $temporary -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $temporary -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    Remove-Item $temporary -Force -ErrorAction SilentlyContinue

    return $certificate
}

# Turns a hub port off and on again, which is a replug without anyone reaching for the cable.
# Windows often records a new driver for a device but leaves the old stack standing until the
# device disappears and comes back; a phone keeps the interface nodes its vendor driver created
# and nothing can open it. pnputil's restart, a hub restart and disable/enable all refuse while
# the machine has a pending reboot, but a port cycle is a hardware event and goes through anyway.
function Invoke-PortCycle {
    param([string]$instanceId)

    $parent = (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    $port = (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_Address' -ErrorAction SilentlyContinue).Data
    if (-not $parent -or -not $port) { return 'the hub or port could not be worked out' }

    # The hub is reached through its device interface path, which is the instance id lowercased
    # with backslashes turned into hashes, plus the USB hub interface class.
    $hubPath = '\\?\' + $parent.ToLowerInvariant().Replace('\', '#') + '#{f18a0e88-c30c-11d0-8815-00a0c906bed8}'

    # The whole call lives in C#. Done from PowerShell the access mask 0xC0000000 is read as a
    # negative number and will not convert to the unsigned parameter.
    if (-not ('Nks.PortCycle' -as [type])) {
        Add-Type -Namespace Nks -Name PortCycle -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct CyclePort { public uint ConnectionIndex; public uint StatusReturned; }

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security,
    uint disposition, uint flags, IntPtr template);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool DeviceIoControl(IntPtr device, uint code, ref CyclePort input, int inputSize,
    ref CyclePort output, int outputSize, out int returned, IntPtr overlapped);

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool CloseHandle(IntPtr handle);

public static string Cycle(string hubPath, uint port)
{
    const uint readWrite = 0xC0000000, shareReadWrite = 3, openExisting = 3;
    const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;

    IntPtr hub = CreateFileW(hubPath, readWrite, shareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
    if (hub == new IntPtr(-1))
        return "the hub could not be opened (Win32 " + Marshal.GetLastWin32Error() + ")";

    try
    {
        var input = new CyclePort { ConnectionIndex = port };
        var output = new CyclePort();
        int returned;
        int size = Marshal.SizeOf(typeof(CyclePort));

        if (!DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, ref input, size, ref output, size, out returned, IntPtr.Zero))
            return "the port could not be cycled (Win32 " + Marshal.GetLastWin32Error() + ")";

        return "port " + port + " cycled";
    }
    finally { CloseHandle(hub); }
}
'@
    }

    return [Nks.PortCycle]::Cycle($hubPath, [uint32]$port)
}

# Installs a driver package on every device matching a hardware id, ignoring which driver Windows
# would otherwise consider better. This is the same call the well-known driver-swapping tools make;
# it lives in newdev.dll, which ships with Windows.
function Invoke-ForceInstall {
    param([string]$infPath, [string]$hardwareId)

    if (-not ('Nks.NewDev' -as [type])) {
        Add-Type -Namespace Nks -Name NewDev -MemberDefinition @'
[DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool UpdateDriverForPlugAndPlayDevicesW(
    IntPtr parent, string hardwareId, string infPath, uint flags, out bool rebootNeeded);
'@
    }

    $reboot = $false
    $installFlagForce = 1
    $ok = [Nks.NewDev]::UpdateDriverForPlugAndPlayDevicesW(
        [IntPtr]::Zero, $hardwareId, $infPath, $installFlagForce, [ref]$reboot)

    if (-not $ok) {
        $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        # 0xE000020B / ERROR_NO_SUCH_DEVINST means nothing matched, which the caller reports on
        # its own by checking the driver afterwards.
        return "the forced install did not take (Win32 $code)"
    }

    # Windows PowerShell 5.1 runs this script, so no ternary operator.
    if ($reboot) { return 'forced; Windows wants a reboot to finish' }
    return 'forced'
}

function New-DriverPackage($hardwareId, $description) {
    $work = Join-Path $env:TEMP ('nksusb-inf-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    $interfaceGuid = '{' + [guid]::NewGuid().ToString().ToUpper() + '}'
    $date = (Get-Date).ToString('MM/dd/yyyy')
    $escaped = $hardwareId

    $inf = @"
; Generated by NKS UsbShare. Binds one device to the in-box WinUSB driver so its transfers
; can be forwarded over the network. Remove this package to give the device back.
[Version]
Signature   = "`$Windows NT`$"
Class       = USBDevice
ClassGuid   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}
Provider    = %ProviderName%
CatalogFile = nksusb.cat
DriverVer   = $date,1.0.0.0
PnpLockdown = 1

[Manufacturer]
%ProviderName% = Standard,NTamd64,NTarm64

[Standard.NTamd64]
%DeviceName% = USB_Install, $escaped

[Standard.NTarm64]
%DeviceName% = USB_Install, $escaped

[USB_Install]
Include = winusb.inf
Needs   = WINUSB.NT

[USB_Install.Services]
Include = winusb.inf
Needs   = WINUSB.NT.Services

[USB_Install.HW]
AddReg = Dev_AddReg

[Dev_AddReg]
HKR,,DeviceInterfaceGUIDs,0x10000,"$interfaceGuid"

[Strings]
ProviderName = "NKS UsbShare"
DeviceName   = "$description"
"@

    $infPath = Join-Path $work 'nksusb.inf'
    Set-Content -Path $infPath -Value $inf -Encoding Unicode

    # The steps below take most of the half minute this whole operation costs, and whatever runs
    # the script shows these lines as they arrive. Without them it is a silent wait that reads as
    # a hang.
    Write-Host 'Building the driver catalog...'
    $catalogPath = Join-Path $work 'nksusb.cat'
    New-FileCatalog -Path $work -CatalogFilePath $catalogPath -CatalogVersion 2 | Out-Null

    $certificate = Get-SigningCertificate

    Write-Host 'Signing the catalog...'
    $signature = Set-AuthenticodeSignature -FilePath $catalogPath -Certificate $certificate -HashAlgorithm SHA256
    if ($signature.Status -ne 'Valid') {
        throw "The driver catalog could not be signed: $($signature.Status) - $($signature.StatusMessage)"
    }

    return [pscustomobject]@{ Directory = $work; Inf = $infPath; InterfaceGuid = $interfaceGuid }
}

function Invoke-Bind {
    Assert-Elevated

    # A USB instance id begins with its hardware id, so the device can be asked for directly.
    # The fallback below walks every device and reads its hardware ids, which is correct but takes
    # the best part of half a minute on a machine with a lot of them.
    $device = Get-PnpDevice -PresentOnly -InstanceId "$HardwareId\*" -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not $device) {
        Write-Host 'Looking through every attached device...'
        $device = Get-PnpDevice -PresentOnly | Where-Object {
            (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data -contains $HardwareId
        } | Select-Object -First 1
    }

    if (-not $device) { throw "No present device matches hardware id '$HardwareId'." }

    Write-Host "Found $($device.FriendlyName). Preparing a driver package..."

    $currentService = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service').Data
    if ($currentService -eq 'WinUSB') {
        Write-Host "$($device.FriendlyName) is already bound to WinUSB."
        return
    }

    $originalInf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath').Data
    Write-Host "Device:          $($device.FriendlyName)"
    Write-Host "Instance:        $($device.InstanceId)"
    Write-Host "Current driver:  $currentService ($originalInf)"

    $package = New-DriverPackage -hardwareId $HardwareId -description $Description
    Write-Host "Driver package:  $($package.Inf)"

    $output = pnputil /add-driver $package.Inf /install 2>&1 | Out-String
    Write-Host $output

    # The published name is what identifies the package for removal later; take it from this
    # command's own output rather than by searching the store, which can match the wrong entry.
    $published = $null
    if ($output -match '(oem\d+\.inf)') { $published = $matches[1] }
    if (-not $published) { throw "The driver package was not accepted. pnputil said:`n$output" }

    Start-Sleep -Seconds 2
    $newService = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data

    # Installing the package does not always move a device that is busy serving its class driver.
    # Restarting the node makes Windows pick the driver again, which is what a replug would do by
    # hand, and it keeps the device in the tree.
    if ($newService -ne 'WinUSB') {
        Write-Host "Still on '$newService' - restarting the device so Windows picks the new driver..."
        & pnputil /restart-device $device.InstanceId 2>&1 | Out-String | Write-Host
        Start-Sleep -Seconds 3
        $newService = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
    }

    # A vendor driver that is WHQL signed outranks this package whatever its INF says, because
    # Windows weighs the signature before it weighs how specifically the hardware id matched. A
    # phone with its manufacturer's driver installed therefore stays where it is. Forcing the
    # choice is a documented Windows call rather than a trick: UpdateDriverForPlugAndPlayDevices
    # with INSTALLFLAG_FORCE installs the named package on the device and skips the ranking.
    if ($newService -ne 'WinUSB') {
        Write-Host "Still on '$newService' - forcing the driver onto the device..."
        $forced = Invoke-ForceInstall -infPath $package.Inf -hardwareId $HardwareId
        Write-Host "  $forced"
        Start-Sleep -Seconds 3
        $newService = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
    }

    # Windows can record the new driver and still leave the old stack standing: a composite device
    # keeps the interface nodes its vendor driver created, and while those hold the interfaces
    # nothing can open the device. The port cycle below is the replug that clears them.
    $leftovers = @((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Children' -ErrorAction SilentlyContinue).Data)
    if ($newService -ne 'WinUSB' -or $leftovers.Count -gt 0) {
        Write-Host 'Cycling the hub port so the device comes back on its new driver...'
        Write-Host "  $(Invoke-PortCycle -instanceId $device.InstanceId)"
        Start-Sleep -Seconds 6
        $newService = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
    }

    $entries = @(Get-State | Where-Object { $_.HardwareId -ne $HardwareId })
    $entries += [pscustomobject]@{
        HardwareId     = $HardwareId
        InstanceId     = $device.InstanceId
        FriendlyName   = $device.FriendlyName
        OriginalDriver = $currentService
        OriginalInf    = $originalInf
        PublishedName  = $published
        InterfaceGuid  = $package.InterfaceGuid
        BoundAt        = (Get-Date).ToString('o')
    }
    Save-State $entries

    Remove-Item $package.Directory -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host "Published as:    $published"
    Write-Host "Driver now:      $newService"

    # Steps along the way are allowed to fail: pnputil refuses to restart a device while the
    # machine has work pending and answers 3010 for "done, but a reboot would finish it". Those
    # leave a non-zero code behind that would otherwise be read as the whole operation failing.
    # What counts is the driver the device ended up on.
    $global:LASTEXITCODE = 0

    if ($newService -eq 'WinUSB') {
        Write-Host 'The device is ready to be shared.' -ForegroundColor Green
    } else {
        Write-Host "The package installed but the device still reports '$newService'. Unplug and replug it." -ForegroundColor Yellow
        exit 1
    }
}

function Invoke-Restore {
    Assert-Elevated

    $entries = @(Get-State)
    $packages = @($entries | ForEach-Object { $_.PublishedName } | Where-Object { $_ })

    # Anything left in the driver store under our provider name is ours too, even if the record
    # of it was lost.
    foreach ($orphan in Find-OrphanPackages) {
        if ($packages -notcontains $orphan) { $packages += $orphan }
    }

    if ($packages.Count -eq 0) {
        Write-Host 'No devices are claimed.'
        Save-State @()
        return
    }

    # The hub each device hangs off has to be noted BEFORE the package goes: uninstalling a driver
    # package takes the device node out of the tree, and once it is gone there is nothing left to
    # ask who its parent was.
    $parents = @{}
    foreach ($entry in $entries) {
        if (-not $entry.InstanceId) { continue }
        $parent = (Get-PnpDeviceProperty -InstanceId $entry.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
        if ($parent) { $parents[$entry.InstanceId] = $parent }
    }

    foreach ($package in $packages) {
        Write-Host "Removing driver package $package..."
        # /uninstall also reverts every device the package is currently bound to.
        $output = pnputil /delete-driver $package /uninstall /force 2>&1 | Out-String
        Write-Host $output.Trim()
    }

    & pnputil /scan-devices 2>&1 | Out-Null
    Start-Sleep -Seconds 4

    foreach ($entry in $entries) {
        if (-not $entry.InstanceId) { continue }

        # Uninstalling the package removes the device node. A USB hub only reports a port when
        # something changes on it, so nothing brings the device back on its own and it stays
        # invisible until it is physically unplugged. Restarting the hub is the replug, done in
        # software: it makes the hub enumerate its ports again and the device returns on its
        # original driver.
        if (-not (Test-DevicePresent $entry.InstanceId)) {
            $parent = $parents[$entry.InstanceId]
            if ($parent) {
                Write-Host "  $($entry.FriendlyName): gone from the device tree, restarting its hub..."
                & pnputil /restart-device "$parent" 2>&1 | Out-Null
                Start-Sleep -Seconds 5
            }
        }

        $service = (Get-PnpDeviceProperty -InstanceId $entry.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        if ($service) {
            Write-Host "  $($entry.FriendlyName): driver now $service (was $($entry.OriginalDriver))"
        } else {
            Write-Host "  $($entry.FriendlyName): did not come back; unplug it and plug it in again." -ForegroundColor Yellow
        }
    }

    Save-State @()
    Write-Host 'Done.' -ForegroundColor Green
}

function Test-DevicePresent {
    param([string]$instanceId)
    return [bool](Get-PnpDevice -InstanceId $instanceId -ErrorAction SilentlyContinue)
}

function Invoke-Status {
    $entries = @(Get-State)
    if ($entries.Count -eq 0) {
        Write-Host 'No devices are bound to WinUSB by this tool.'
        return
    }

    foreach ($entry in $entries) {
        $service = (Get-PnpDeviceProperty -InstanceId $entry.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        Write-Host "$($entry.FriendlyName)"
        Write-Host "  hardware id  $($entry.HardwareId)"
        Write-Host "  package      $($entry.PublishedName)"
        Write-Host "  was          $($entry.OriginalDriver)"
        Write-Host "  now          $service"
        Write-Host "  bound at     $($entry.BoundAt)"
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Bind'    { Invoke-Bind }
    'Restore' { Invoke-Restore }
    'Status'  { Invoke-Status }
}
