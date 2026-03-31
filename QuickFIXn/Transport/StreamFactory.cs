using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using QuickFix.Logger;

namespace QuickFix.Transport
{
    /// <summary>
    /// StreamFactory is responsible for initiating <see cref="T:System.IO.Stream"/> for communication.
    /// If any SSL setup is required it is performed here
    /// </summary>
    internal static class StreamFactory
    {
        private sealed class StreamFactoryLogCategory { }

        private const int DEFAULT_PROXY_CONNECT_TIMEOUT_MS = 10000;
        private const int DEFAULT_PROXY_READ_TIMEOUT_MS = 10000;

        private static Socket CreateSocketWithOptions(SocketSettings settings)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = settings.SocketNodelay
            };

            if (settings.SocketReceiveBufferSize.HasValue)
                socket.ReceiveBufferSize = settings.SocketReceiveBufferSize.Value;

            if (settings.SocketSendBufferSize.HasValue)
                socket.SendBufferSize = settings.SocketSendBufferSize.Value;

            return socket;
        }

        private static void ConnectWithTimeout(Socket socket, string host, int port, int timeoutMs)
        {
            var connectTask = socket.ConnectAsync(host, port);
            if (!connectTask.Wait(timeoutMs))
            {
                socket.Dispose();
                throw new TimeoutException($"Timed out connecting to {host}:{port} after {timeoutMs}ms");
            }
        }

        private static Socket? CreateTunnelThruSystemProxy(IPEndPoint endpoint, string destHostName)
        {
            string destUriWithPort = $"{endpoint.Address}:{endpoint.Port}";
            UriBuilder uriBuilder = new UriBuilder(destUriWithPort);
            Uri destUri = uriBuilder.Uri;
            IWebProxy webProxy = WebRequest.DefaultWebProxy ?? WebRequest.GetSystemWebProxy();

            try
            {
                if (webProxy.IsBypassed(destUri))
                    return null;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }

            Uri? proxyUri = webProxy.GetProxy(destUri);
            if (proxyUri is null)
                return null;

            IPAddress[] proxyEntry = Dns.GetHostAddresses(proxyUri.Host);
            IPAddress address = proxyEntry.First(a => a.AddressFamily == AddressFamily.InterNetwork);
            IPEndPoint proxyEndPoint = new IPEndPoint(address, proxyUri.Port);
            Socket socketThruProxy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socketThruProxy.Connect(proxyEndPoint);

            string proxyMsg = $"CONNECT {destHostName}:{endpoint.Port} HTTP/1.1\r\nHost: {destHostName}:{endpoint.Port}\r\n\r\n";
            byte[] buffer = Encoding.ASCII.GetBytes(proxyMsg);
            byte[] responseBuffer = new byte[500];
            socketThruProxy.Send(buffer, buffer.Length, 0);
            socketThruProxy.Receive(responseBuffer, 500, 0);
            string data = Encoding.ASCII.GetString(responseBuffer);
            int index = data.IndexOf("200", StringComparison.Ordinal);

            if (index < 0)
                throw new ApplicationException(
                    $"Connection failed to {destUriWithPort} through proxy server {proxyUri}.");

            return socketThruProxy;
        }

        private static bool ShouldBypassConfiguredProxy(SocketSettings settings, string destinationHost)
            => settings.ProxyBypassForHosts.Contains(destinationHost);

        private static Socket CreateTunnelThruConfiguredProxy(
            string destinationHost,
            int destinationPort,
            SocketSettings settings,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(settings.ProxyHost) || !settings.ProxyPort.HasValue)
                throw new ConfigError($"{SessionSettings.PROXY_ENABLED}=Y requires {SessionSettings.PROXY_HOST} and {SessionSettings.PROXY_PORT}");

            string proxyType = settings.ProxyType ?? "HTTP";
            if (!proxyType.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
                throw new ConfigError($"Unsupported proxy type '{proxyType}'. Only HTTP is supported.");

            int connectTimeoutMs = settings.ProxyConnectTimeoutMs ?? DEFAULT_PROXY_CONNECT_TIMEOUT_MS;
            int readTimeoutMs = settings.ProxyReadTimeoutMs ?? DEFAULT_PROXY_READ_TIMEOUT_MS;

            logger.Log(LogLevel.Information, "Proxy connect started to {ProxyHost}:{ProxyPort}", settings.ProxyHost, settings.ProxyPort.Value);
            logger.Log(LogLevel.Information, "Proxy CONNECT target {TargetHost}:{TargetPort}", destinationHost, destinationPort);
            logger.Log(LogLevel.Information, "Proxy authentication {AuthMode}",
                string.IsNullOrEmpty(settings.ProxyUsername) ? "not used" : "used");

            Socket socket = CreateSocketWithOptions(settings);
            ConnectWithTimeout(socket, settings.ProxyHost, settings.ProxyPort.Value, connectTimeoutMs);

            socket.ReceiveTimeout = readTimeoutMs;
            socket.SendTimeout = settings.SocketSendTimeout ?? readTimeoutMs;

            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n" };

            writer.WriteLine($"CONNECT {destinationHost}:{destinationPort} HTTP/1.1");
            writer.WriteLine($"Host: {destinationHost}:{destinationPort}");
            writer.WriteLine("Proxy-Connection: Keep-Alive");

            if (!string.IsNullOrEmpty(settings.ProxyUsername))
            {
                string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ProxyUsername}:{settings.ProxyPassword ?? string.Empty}"));
                writer.WriteLine($"Proxy-Authorization: Basic {credentials}");
            }

            writer.WriteLine();
            writer.Flush();

            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? statusLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(statusLine))
                throw new IOException("Proxy returned an empty response to CONNECT");

            string[] statusParts = statusLine.Split(' ');
            if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int statusCode))
                throw new IOException($"Invalid proxy response: '{statusLine}'");

            string? line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
            }

            if (statusCode != 200)
            {
                logger.Log(LogLevel.Error, "Proxy CONNECT failed with status code {StatusCode}", statusCode);
                throw new IOException($"Proxy CONNECT failed with status code {statusCode}");
            }

            logger.Log(LogLevel.Information, "Proxy CONNECT succeeded");
            return socket;
        }

        /// <summary>
        /// Connect to the specified endpoint and return a stream that can be used to communicate with it. (for initiator)
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <param name="destinationHostName">The configured target host name.</param>
        /// <param name="settings">The socket settings.</param>
        /// <param name="loggerFactory"></param>
        /// <returns>an opened and initiated stream which can be read and written to</returns>
        internal static Stream CreateClientStream(IPEndPoint endpoint, string destinationHostName, SocketSettings settings, IQuickFixLoggerFactory loggerFactory)
        {
            Socket? socket = null;
            ILogger log = loggerFactory.CreateNonSessionLogger<StreamFactoryLogCategory>();

            if (settings.ProxyEnabled && !ShouldBypassConfiguredProxy(settings, destinationHostName))
            {
                socket = CreateTunnelThruConfiguredProxy(destinationHostName, endpoint.Port, settings, log);
            }
            else if (!settings.SocketIgnoreProxy)
            {
                socket = CreateTunnelThruSystemProxy(endpoint, destinationHostName);
            }

            if (socket is null)
            {
                socket = CreateSocketWithOptions(settings);
                socket.Connect(endpoint);
            }

            if (settings.SocketReceiveTimeout.HasValue)
                socket.ReceiveTimeout = settings.SocketReceiveTimeout.Value;

            if (settings.SocketSendTimeout.HasValue)
                socket.SendTimeout = settings.SocketSendTimeout.Value;

            Stream stream = new NetworkStream(socket, true);

            if (settings.UseSSL)
                stream = new SslStreamFactory(settings, loggerFactory).CreateClientStreamAndAuthenticate(stream);

            return stream;
        }

        /// <summary>
        /// Initiate communication to the remote client and return a stream that can be used to communicate with it. (for acceptor)
        /// </summary>
        /// <param name="tcpClient">The TCP client.</param>
        /// <param name="settings">The socket settings.</param>
        /// <param name="loggerFactory"></param>
        /// <returns>an opened and initiated stream which can be read and written to</returns>
        /// <exception cref="System.ArgumentException">tcp client must be connected in order to get stream;tcpClient</exception>
        internal static Stream CreateServerStream(TcpClient tcpClient, SocketSettings settings, IQuickFixLoggerFactory loggerFactory)
        {
            if (tcpClient.Connected == false)
                throw new ArgumentException("tcp client must be connected in order to get stream", nameof(tcpClient));

            Stream stream = tcpClient.GetStream();
            if (settings.UseSSL)
            {
                stream = new SslStreamFactory(settings, loggerFactory).CreateServerStreamAndAuthenticate(stream);
            }

            return stream;
        }
    }
}
