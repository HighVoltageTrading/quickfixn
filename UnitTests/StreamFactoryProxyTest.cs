using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Transport;

namespace UnitTests;

[TestFixture]
public class StreamFactoryProxyTest
{
    private static async Task<string> ReadHttpHeaders(NetworkStream stream)
    {
        var data = new MemoryStream();
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, 0, 1);
            if (read <= 0)
                break;

            data.WriteByte(one[0]);
            byte[] buf = data.ToArray();
            int len = buf.Length;
            if (len >= 4 && buf[len - 4] == '\r' && buf[len - 3] == '\n' && buf[len - 2] == '\r' && buf[len - 1] == '\n')
                break;
        }

        return Encoding.ASCII.GetString(data.ToArray());
    }

    private static async Task PumpAsync(Stream input, Stream output, CancellationToken token)
    {
        byte[] buffer = new byte[1024];
        while (!token.IsCancellationRequested)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            await output.FlushAsync(token);
        }
    }

    [Test]
    public async Task DirectConnectionStillWorks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<byte[]> acceptTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using var ns = client.GetStream();
            byte[] buffer = new byte[4];
            int read = await ns.ReadAsync(buffer, 0, buffer.Length);
            return buffer[..read];
        });

        var settings = new SocketSettings { SocketIgnoreProxy = true };
        using Stream stream = StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, port), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance);
        await stream.WriteAsync("PING"u8.ToArray());

        byte[] received = await acceptTask;
        Assert.That(Encoding.ASCII.GetString(received), Is.EqualTo("PING"));
    }

    [Test]
    public async Task ProxiedConnectionSuccessUsingHttpConnect()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;

        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;

        Task<string> targetReadTask = Task.Run(async () =>
        {
            using TcpClient targetClient = await target.AcceptTcpClientAsync();
            using NetworkStream ns = targetClient.GetStream();
            byte[] buffer = new byte[5];
            int read = await ns.ReadAsync(buffer, 0, buffer.Length);
            return Encoding.ASCII.GetString(buffer, 0, read);
        });

        Task proxyTask = Task.Run(async () =>
        {
            using TcpClient proxyClient = await proxy.AcceptTcpClientAsync();
            using NetworkStream proxyStream = proxyClient.GetStream();
            string headers = await ReadHttpHeaders(proxyStream);
            StringAssert.Contains($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1", headers);

            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
            await proxyStream.WriteAsync(ok, 0, ok.Length);

            using var tunnelClient = new TcpClient();
            await tunnelClient.ConnectAsync(IPAddress.Loopback, targetPort);
            using NetworkStream tunnelStream = tunnelClient.GetStream();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task t1 = PumpAsync(proxyStream, tunnelStream, cts.Token);
            Task t2 = PumpAsync(tunnelStream, proxyStream, cts.Token);
            await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(1500, cts.Token));
            cts.Cancel();
        });

        var settings = new SocketSettings
        {
            ProxyEnabled = true,
            ProxyType = "HTTP",
            ProxyHost = "127.0.0.1",
            ProxyPort = proxyPort,
            ProxyReadTimeoutMs = 3000,
            ProxyConnectTimeoutMs = 3000
        };

        using Stream stream = StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, targetPort), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance);
        await stream.WriteAsync("HELLO"u8.ToArray());

        Assert.That(await targetReadTask, Is.EqualTo("HELLO"));
        await proxyTask;
    }

    [Test]
    public void ProxyReturns403_ConnectionFails()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;

        Task.Run(async () =>
        {
            using TcpClient proxyClient = await proxy.AcceptTcpClientAsync();
            using NetworkStream proxyStream = proxyClient.GetStream();
            _ = await ReadHttpHeaders(proxyStream);
            byte[] deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\n");
            await proxyStream.WriteAsync(deny, 0, deny.Length);
        });

        var settings = new SocketSettings
        {
            ProxyEnabled = true,
            ProxyType = "HTTP",
            ProxyHost = "127.0.0.1",
            ProxyPort = proxyPort
        };

        Assert.Throws<IOException>(() =>
            StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, 9999), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance));
    }

    [Test]
    public async Task ProxyAuthorizationHeaderIsSentWhenCredentialsConfigured()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;

        Task<string> proxyHeadersTask = Task.Run(async () =>
        {
            using TcpClient proxyClient = await proxy.AcceptTcpClientAsync();
            using NetworkStream proxyStream = proxyClient.GetStream();
            string headers = await ReadHttpHeaders(proxyStream);
            byte[] deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\n");
            await proxyStream.WriteAsync(deny, 0, deny.Length);
            return headers;
        });

        var settings = new SocketSettings
        {
            ProxyEnabled = true,
            ProxyType = "HTTP",
            ProxyHost = "127.0.0.1",
            ProxyPort = proxyPort,
            ProxyUsername = "myuser",
            ProxyPassword = "mypass"
        };

        Assert.Throws<IOException>(() =>
            StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, 9999), "example.org", settings, NullQuickFixLoggerFactory.Instance));

        string headers = await proxyHeadersTask;
        StringAssert.Contains("Proxy-Authorization: Basic bXl1c2VyOm15cGFzcw==", headers);
    }

    [Test]
    public void InvalidProxyResponseIsHandledCleanly()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;

        Task.Run(async () =>
        {
            using TcpClient proxyClient = await proxy.AcceptTcpClientAsync();
            using NetworkStream proxyStream = proxyClient.GetStream();
            _ = await ReadHttpHeaders(proxyStream);
            byte[] invalid = Encoding.ASCII.GetBytes("NOT_HTTP\r\n\r\n");
            await proxyStream.WriteAsync(invalid, 0, invalid.Length);
        });

        var settings = new SocketSettings
        {
            ProxyEnabled = true,
            ProxyType = "HTTP",
            ProxyHost = "127.0.0.1",
            ProxyPort = proxyPort
        };

        Assert.Throws<IOException>(() =>
            StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, 9999), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance));
    }

    [Test]
    public void ConnectionCanBeRetriedAfterProxySideDisconnect()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;

        int attempts = 0;
        Task.Run(async () =>
        {
            while (attempts < 2)
            {
                using TcpClient proxyClient = await proxy.AcceptTcpClientAsync();
                using NetworkStream proxyStream = proxyClient.GetStream();
                _ = await ReadHttpHeaders(proxyStream);
                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                await proxyStream.WriteAsync(ok, 0, ok.Length);

                if (Interlocked.Increment(ref attempts) == 1)
                {
                    proxyClient.Close();
                }
                else
                {
                    break;
                }
            }
        });

        var settings = new SocketSettings
        {
            ProxyEnabled = true,
            ProxyType = "HTTP",
            ProxyHost = "127.0.0.1",
            ProxyPort = proxyPort
        };

        using Stream first = StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, 5001), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance);
        first.Dispose();

        using Stream second = StreamFactory.CreateClientStream(new IPEndPoint(IPAddress.Loopback, 5001), "127.0.0.1", settings, NullQuickFixLoggerFactory.Instance);
        Assert.That(second.CanWrite, Is.True);
    }
}
