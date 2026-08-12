using System.Security.Authentication;
using NksUsbShare.Core.Protocol;
using NksUsbShare.Core.Security;

namespace NksUsbShare.Tests;

public static class SecurityTests
{
    public static void Register()
    {
        const string suite = "Security";

        TestRunner.Add(suite, "tokens are unique, readable and free of ambiguous characters", () =>
        {
            var tokens = Enumerable.Range(0, 200).Select(_ => AccessToken.Generate()).ToList();

            Assert.Equal(200, tokens.Distinct().Count(), "every generated token is distinct");
            Assert.Equal(29, tokens[0].Length, "token length");
            Assert.Equal(4, tokens[0].Count(c => c == '-'), "group separators");

            foreach (var forbidden in "IO01")
                Assert.False(tokens.Any(t => t.Contains(forbidden)),
                    $"no token contains the ambiguous character '{forbidden}'");
        });

        TestRunner.Add(suite, "token verification accepts the right token in any formatting", () =>
        {
            var token = AccessToken.Generate();
            Assert.True(AccessToken.Verify(token, token), "exact match");
            Assert.True(AccessToken.Verify(token.ToLowerInvariant(), token), "case insensitive");
            Assert.True(AccessToken.Verify(token.Replace("-", ""), token), "separators ignored");
        });

        TestRunner.Add(suite, "token verification rejects wrong, empty and null tokens", () =>
        {
            var token = AccessToken.Generate();
            Assert.False(AccessToken.Verify(AccessToken.Generate(), token), "a different token");
            Assert.False(AccessToken.Verify("", token), "empty presented token");
            Assert.False(AccessToken.Verify(null, token), "null presented token");
            Assert.False(AccessToken.Verify(token, ""), "empty expected token");
            Assert.False(AccessToken.Verify(token, null), "null expected token");
            Assert.False(AccessToken.Verify(token[..20], token), "a truncated prefix");
        });

        TestRunner.Add(suite, "identity generates a usable certificate with a stable fingerprint", () =>
        {
            using var certificate = NodeIdentity.Create("test-node");

            Assert.True(certificate.HasPrivateKey, "the certificate carries its private key");
            Assert.True(certificate.NotAfter > DateTime.UtcNow.AddYears(9), "validity is long enough to be practical");

            var first = NodeIdentity.ComputeFingerprint(certificate);
            var second = NodeIdentity.ComputeFingerprint(certificate);
            Assert.Equal(first, second, "the fingerprint is deterministic");
            Assert.Equal(24, first.Length, "fingerprint is five groups of four hex characters");
        });

        TestRunner.Add(suite, "two identities never share a fingerprint", () =>
        {
            using var a = NodeIdentity.Create("node-a");
            using var b = NodeIdentity.Create("node-b");
            Assert.False(NodeIdentity.ComputeFingerprint(a) == NodeIdentity.ComputeFingerprint(b),
                "independently generated keys differ");
        });

        TestRunner.Add(suite, "identity persists across restarts", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "nks-usbshare-test-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var first = NodeIdentity.LoadOrCreate(directory, "persisted-node");
                var second = NodeIdentity.LoadOrCreate(directory, "persisted-node");
                Assert.Equal(first.Fingerprint, second.Fingerprint, "the same identity was loaded back");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        TestRunner.Add(suite, "fingerprint comparison tolerates formatting but not different values", () =>
        {
            Assert.True(NodeIdentity.FingerprintsMatch("ABCD-1234", "abcd1234"), "case and separators ignored");
            Assert.True(NodeIdentity.FingerprintsMatch("AB:CD:12", "ABCD12"), "colon separators ignored");
            Assert.False(NodeIdentity.FingerprintsMatch("ABCD-1234", "ABCD-1235"), "one differing character");
            Assert.False(NodeIdentity.FingerprintsMatch(null, "ABCD"), "null never matches");
        });

        TestRunner.Add(suite, "a TLS session carries protocol frames end to end", async () =>
        {
            using var host = NodeIdentity.Create("host");
            var (clientSocket, serverSocket) = DuplexPipePair.Create();

            var serverTask = SecureChannel.AuthenticateServerAsync(
                serverSocket, host, new HashSet<string>(), CancellationToken.None);
            var clientTask = SecureChannel.AuthenticateClientAsync(
                clientSocket, "host", null, null, CancellationToken.None);

            await using var serverSsl = await serverTask;
            var (clientSsl, observedFingerprint) = await clientTask;

            Assert.True(serverSsl.IsEncrypted, "the server side negotiated encryption");
            Assert.True(clientSsl.IsAuthenticated, "the client side completed the handshake");
            Assert.Equal(NodeIdentity.ComputeFingerprint(host), observedFingerprint,
                "the client observed the host's real fingerprint");

            Console.WriteLine($"          negotiated {serverSsl.SslProtocol}, cipher {serverSsl.NegotiatedCipherSuite}");

            await using var serverConnection = new FramedConnection(serverSsl, ownsStream: false);
            await using var clientConnection = new FramedConnection(clientSsl);

            await clientConnection.SendAsync(MessageType.Hello,
                new HelloMessage("client", "test", "PAIR-TOKEN").Serialize(), CancellationToken.None);

            var frame = await serverConnection.ReceiveAsync(CancellationToken.None);
            var hello = HelloMessage.Deserialize(frame!.Value.Payload);
            Assert.Equal("PAIR-TOKEN", hello.Token, "the token survived the encrypted channel");
        });

        TestRunner.Add(suite, "a pinned client refuses a host whose key changed", async () =>
        {
            using var realHost = NodeIdentity.Create("host");
            using var impostor = NodeIdentity.Create("impostor");
            var (clientSocket, serverSocket) = DuplexPipePair.Create();

            // The impostor answers, but the client pinned the real host's fingerprint.
            var serverTask = SecureChannel.AuthenticateServerAsync(
                serverSocket, impostor, new HashSet<string>(), CancellationToken.None);

            await Assert.Throws<PinnedCertificateMismatchException>(async () =>
            {
                var (stream, _) = await SecureChannel.AuthenticateClientAsync(
                    clientSocket, "host", NodeIdentity.ComputeFingerprint(realHost), null, CancellationToken.None);
                await stream.DisposeAsync();
            }, "connecting to a host with an unexpected key");

            // The server side fails too; drain it so the test leaves nothing running.
            try { (await serverTask).Dispose(); } catch (AuthenticationException) { } catch (IOException) { }
        });

        TestRunner.Add(suite, "a host requiring client certificates rejects an unknown client", async () =>
        {
            using var host = NodeIdentity.Create("host");
            using var stranger = NodeIdentity.Create("stranger");
            using var known = NodeIdentity.Create("known");

            var allowed = new HashSet<string> { NodeIdentity.ComputeFingerprint(known) };
            var (clientSocket, serverSocket) = DuplexPipePair.Create();

            var serverTask = SecureChannel.AuthenticateServerAsync(serverSocket, host, allowed, CancellationToken.None);
            var clientTask = SecureChannel.AuthenticateClientAsync(clientSocket, "host", null, stranger, CancellationToken.None);

            var rejected = false;
            try
            {
                await using var stream = await serverTask;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                rejected = true;
            }

            try { (await clientTask).Stream.Dispose(); }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { rejected = true; }

            Assert.True(rejected, "the handshake failed for a client outside the allow list");
        });
    }
}
