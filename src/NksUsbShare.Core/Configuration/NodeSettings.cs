using System.Text.Json;
using System.Text.Json.Serialization;
using NksUsbShare.Core.Security;

namespace NksUsbShare.Core.Configuration;

/// <summary>Default TCP ports. Both are configurable; these are just what the app uses out of the box.</summary>
public static class DefaultPorts
{
    /// <summary>Where the host listens for encrypted device traffic.</summary>
    public const int Link = 57340;

    /// <summary>Where the local web interface is served. Bound to loopback unless told otherwise.</summary>
    public const int WebUi = 57341;

    /// <summary>
    /// Where imported devices are offered to this machine's own virtual USB host controller.
    /// Bound to loopback only; 3240 is the port such controllers look for by convention.
    /// </summary>
    public const int LocalController = 3240;
}

/// <summary>One device the host is willing to hand out.</summary>
public sealed class DeviceShare
{
    /// <summary>Backend device id. Stable while the device stays in the same port.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>vid:pid, used to recognise the device again after a replug into another port.</summary>
    public string HardwareKey { get; set; } = "";

    public string SerialNumber { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Client fingerprints permitted to import this device. Empty means any client that passed
    /// the token check, which is the sane default for a single-user setup.
    /// </summary>
    public List<string> AllowedClients { get; set; } = [];

    public bool AllowsClient(string? fingerprint)
        => AllowedClients.Count == 0
           || (fingerprint is not null && AllowedClients.Any(a => NodeIdentity.FingerprintsMatch(a, fingerprint)));
}

/// <summary>A client the host has seen before.</summary>
public sealed class KnownClient
{
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public bool Blocked { get; set; }
}

/// <summary>A host this node imports from.</summary>
public sealed class RemoteHost
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = DefaultPorts.Link;
    public string Token { get; set; } = "";

    /// <summary>Pinned on first successful connection; a change afterwards aborts the connection.</summary>
    public string? PinnedFingerprint { get; set; }

    public bool AutoConnect { get; set; }
}

/// <summary>Everything one installation persists, as a single JSON file.</summary>
public sealed class NodeSettings
{
    public string HostName { get; set; } = Environment.MachineName;

    public int LinkPort { get; set; } = DefaultPorts.Link;
    public int WebUiPort { get; set; } = DefaultPorts.WebUi;

    /// <summary>Bind address for the link listener. 0.0.0.0 exposes it to the network.</summary>
    public string LinkBindAddress { get; set; } = "0.0.0.0";

    /// <summary>Bind address for the web interface. Loopback keeps the control plane local.</summary>
    public string WebUiBindAddress { get; set; } = "127.0.0.1";

    /// <summary>Presented by clients after the TLS handshake. Regenerated on first run.</summary>
    public string PairingToken { get; set; } = "";

    /// <summary>When true, clients must also present a certificate the host already knows.</summary>
    public bool RequireClientCertificate { get; set; }

    public List<DeviceShare> Shares { get; set; } = [];
    public List<KnownClient> KnownClients { get; set; } = [];
    public List<RemoteHost> RemoteHosts { get; set; } = [];

    [JsonIgnore]
    public string? SourcePath { get; private set; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Per-user state directory, overridable so tests and portable installs stay self-contained.</summary>
    public static string DefaultStateDirectory =>
        Environment.GetEnvironmentVariable("NKS_USBSHARE_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "nks-usbshare");

    public static NodeSettings LoadOrCreate(string? stateDirectory = null)
    {
        var directory = stateDirectory ?? DefaultStateDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        NodeSettings settings;
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<NodeSettings>(File.ReadAllText(path), SerializerOptions)
                           ?? new NodeSettings();
            }
            catch (JsonException)
            {
                // Keep the unreadable file for inspection rather than silently discarding it.
                File.Move(path, path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
                settings = new NodeSettings();
            }
        }
        else
        {
            settings = new NodeSettings();
        }

        settings.SourcePath = path;

        if (string.IsNullOrWhiteSpace(settings.PairingToken))
        {
            settings.PairingToken = AccessToken.Generate();
            settings.Save();
        }

        return settings;
    }

    public void Save()
    {
        if (SourcePath is null) return;

        // Write beside the target and swap, so a crash mid-write cannot truncate the settings.
        var temporary = SourcePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temporary, SourcePath, overwrite: true);
    }

    public DeviceShare? FindShare(string deviceId, string hardwareKey, string serialNumber)
    {
        var byId = Shares.FirstOrDefault(s => s.DeviceId == deviceId);
        if (byId is not null) return byId;

        // The device may have moved to a different port; match on identity instead.
        return Shares.FirstOrDefault(s =>
            s.HardwareKey == hardwareKey
            && (s.SerialNumber.Length == 0 || s.SerialNumber == serialNumber));
    }

    public KnownClient TouchClient(string fingerprint, string name)
    {
        var client = KnownClients.FirstOrDefault(c => NodeIdentity.FingerprintsMatch(c.Fingerprint, fingerprint));
        if (client is null)
        {
            client = new KnownClient { Fingerprint = fingerprint, Name = name };
            KnownClients.Add(client);
        }

        client.LastSeen = DateTimeOffset.UtcNow;
        if (name.Length > 0) client.Name = name;
        return client;
    }
}
