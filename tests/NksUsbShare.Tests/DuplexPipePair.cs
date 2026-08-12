using System.Net;
using System.Net.Sockets;

namespace NksUsbShare.Tests;

/// <summary>
/// A connected pair of duplex streams, built on a loopback TCP socket so the tests exercise
/// the same partial-read behaviour the real transport has.
/// </summary>
public static class DuplexPipePair
{
    public static (Stream Client, Stream Server) Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            var client = new TcpClient();
            var connect = client.ConnectAsync(endpoint.Address, endpoint.Port);
            var server = listener.AcceptTcpClient();
            connect.GetAwaiter().GetResult();

            client.NoDelay = true;
            server.NoDelay = true;
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }
}
