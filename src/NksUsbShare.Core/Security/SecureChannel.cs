using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NksUsbShare.Core.Security;

/// <summary>Raised when a peer's certificate does not match the fingerprint that was pinned for it.</summary>
public sealed class PinnedCertificateMismatchException(string expected, string actual)
    : Exception($"Certificate fingerprint mismatch. Expected {expected}, the peer presented {actual}. " +
                "Either the host was reinstalled or something is intercepting the connection.")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

/// <summary>
/// Wraps a raw stream in TLS. Both sides authenticate by pinned public-key fingerprint rather
/// than by certificate chain, because the endpoints are self-signed peers and a hostname means
/// nothing when a link may run over a VPN, a NAT traversal or a bare IP address.
/// </summary>
public static class SecureChannel
{
    private const SslProtocols Protocols = SslProtocols.Tls13 | SslProtocols.Tls12;

    /// <summary>Accepts an inbound connection. When <paramref name="allowedClientFingerprints"/> is
    /// non-empty the client must also present a certificate from that list.</summary>
    public static async Task<SslStream> AuthenticateServerAsync(
        Stream inner,
        X509Certificate2 serverCertificate,
        IReadOnlySet<string> allowedClientFingerprints,
        CancellationToken cancellationToken)
    {
        var requireClientCertificate = allowedClientFingerprints.Count > 0;

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            // Whatever it presented is recorded by the caller; only a restricted host insists that
            // the certificate be one it already knows.
            if (!requireClientCertificate) return true;
            if (certificate is null) return false;

            var presented = NodeIdentity.ComputeFingerprint(ToCertificate2(certificate));
            return allowedClientFingerprints.Any(allowed => NodeIdentity.FingerprintsMatch(allowed, presented));
        });

        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                // Always ask, even when any client is welcome: a client only sends its certificate
                // when the server requests one, and without it the host cannot name the computer it
                // is talking to, so blocking and per-device access lists would have nothing to match.
                ClientCertificateRequired = true,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens an outbound connection. <paramref name="expectedFingerprint"/> may be null on the very
    /// first contact, which is trust on first use: the caller is expected to record what it saw
    /// and pin it from then on.
    /// </summary>
    public static async Task<(SslStream Stream, string PeerFingerprint)> AuthenticateClientAsync(
        Stream inner,
        string targetName,
        string? expectedFingerprint,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        string? observed = null;
        PinnedCertificateMismatchException? mismatch = null;

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            observed = NodeIdentity.ComputeFingerprint(ToCertificate2(certificate));

            if (expectedFingerprint is null) return true;
            if (NodeIdentity.FingerprintsMatch(expectedFingerprint, observed)) return true;

            // Remember the reason so the caller gets a useful message instead of a generic
            // authentication failure from the platform.
            mismatch = new PinnedCertificateMismatchException(expectedFingerprint, observed);
            return false;
        });

        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetName,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            if (clientCertificate is not null)
                options.ClientCertificates = new X509CertificateCollection { clientCertificate };

            await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            return (ssl, observed ?? "");
        }
        catch (Exception) when (mismatch is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw mismatch;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static X509Certificate2 ToCertificate2(X509Certificate certificate)
        => certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
}
