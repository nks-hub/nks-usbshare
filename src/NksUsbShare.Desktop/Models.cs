namespace NksUsbShare.Desktop;

/// <summary>A row in the local device list.</summary>
public sealed class LocalDeviceRow
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Manufacturer { get; init; }
    public required string HardwareKey { get; init; }
    public required string Kind { get; init; }
    public required string Speed { get; init; }
    public required string SerialNumber { get; init; }
    public required string Status { get; init; }

    /// <summary>Shown as a tooltip on the status cell; explains an "Unavailable" state.</summary>
    public string? StatusDetail { get; init; }

    public bool IsShared { get; init; }
    public bool CanShare { get; init; }
}

/// <summary>A row in the paired computers list.</summary>
public sealed class HostRow
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Status { get; init; }
    public required string Fingerprint { get; init; }
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>A row in the remote device list.</summary>
public sealed class RemoteDeviceRow
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Manufacturer { get; init; }
    public required string HardwareKey { get; init; }
    public required string Kind { get; init; }
    public required string Speed { get; init; }
    public required string Status { get; init; }
    public required string HostAddress { get; init; }
    public required int HostPort { get; init; }
    public string? ImportKey { get; init; }
    public bool IsImported => ImportKey is not null;
}

/// <summary>A row in the connected clients list.</summary>
public sealed class ClientRow
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Platform { get; init; }
    public required string DeviceCount { get; init; }
    public required string Since { get; init; }
}
