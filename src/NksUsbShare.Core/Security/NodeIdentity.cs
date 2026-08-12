using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NksUsbShare.Core.Security;

/// <summary>
/// The long-lived cryptographic identity of one node: a self-signed P-384 certificate whose
/// public key fingerprint is what peers pin. Certificates are generated on first run and kept
/// in the state directory; no certificate authority is involved because there is no third party
/// to trust in a peer-to-peer link.
/// </summary>
public sealed class NodeIdentity
{
    private const string FileName = "node-identity.pfx";

    /// <summary>A fixed passphrase would be pointless: the file's protection is the filesystem.</summary>
    private static readonly string ExportPassphrase = "nks-usbshare";

    /// <summary>
    /// Ephemeral keys are not usable here: Windows refuses them as a TLS server credential, and
    /// macOS refuses to load them at all. Without PersistKeySet the key material is still cleaned
    /// up when the certificate is disposed, so nothing is left behind.
    /// </summary>
    private const X509KeyStorageFlags StorageFlags = X509KeyStorageFlags.Exportable;

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 over the SubjectPublicKeyInfo, formatted in readable groups.</summary>
    public string Fingerprint { get; }

    private NodeIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = ComputeFingerprint(certificate);
    }

    /// <summary>Loads the node's identity, creating it on first use.</summary>
    public static NodeIdentity LoadOrCreate(string stateDirectory, string subjectName)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, FileName);

        if (File.Exists(path))
        {
            try
            {
                var loaded = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), ExportPassphrase, StorageFlags);

                // A certificate that has expired cannot complete a handshake, so replace it.
                if (loaded.NotAfter > DateTimeOffset.UtcNow.AddDays(1)) return new NodeIdentity(loaded);
                loaded.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable: regenerate rather than leave the node unable to start.
            }
        }

        var created = Create(subjectName);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12, ExportPassphrase));
        RestrictToOwner(path);
        return new NodeIdentity(created);
    }

    /// <summary>Builds a fresh self-signed certificate valid for ten years.</summary>
    public static X509Certificate2 Create(string subjectName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest($"CN={Sanitise(subjectName)}", key, HashAlgorithmName.SHA384);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        // Peers authenticate by pinned fingerprint rather than by name, but a SAN keeps the
        // certificate well formed for tooling that inspects it.
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName(Sanitise(subjectName));
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        // Round-tripping through PKCS#12 attaches the private key in the form SslStream needs
        // on Windows; without it the handshake fails with a missing credentials error.
        var exported = certificate.Export(X509ContentType.Pkcs12, ExportPassphrase);
        certificate.Dispose();
        return X509CertificateLoader.LoadPkcs12(exported, ExportPassphrase, StorageFlags);
    }

    public static string ComputeFingerprint(X509Certificate2 certificate)
    {
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Format(SHA256.HashData(spki));
    }

    /// <summary>Renders a hash as five groups of four hex characters, which is short enough to compare by eye.</summary>
    private static string Format(ReadOnlySpan<byte> hash)
    {
        var hex = Convert.ToHexString(hash[..10]).ToUpperInvariant();
        return string.Join('-', Enumerable.Range(0, 5).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Compares two fingerprints without leaking timing, and tolerating case and separators.</summary>
    public static bool FingerprintsMatch(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(Normalise(left));
        var b = System.Text.Encoding.UTF8.GetBytes(Normalise(right));
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Normalise(string fingerprint)
        => fingerprint.Replace("-", "").Replace(":", "").Replace(" ", "").ToUpperInvariant();

    private static string Sanitise(string name)
    {
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray());
        return cleaned.Length == 0 ? "nks-usbshare-node" : cleaned;
    }

    /// <summary>Best-effort tightening of file permissions; on Unix this is the difference that matters.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // A filesystem that cannot express modes is not a reason to refuse to start.
        }
    }
}
