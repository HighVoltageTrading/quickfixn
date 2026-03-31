using System.Security.Authentication;
using NUnit.Framework;
using QuickFix;

namespace UnitTests
{
    [TestFixture]
    public class SocketSettingsTest
    {
        [Test]
        public void DefaultValues()
        {
            SocketSettings socketSettings = new SocketSettings();

            Assert.That(socketSettings.SocketIgnoreProxy, Is.False);
            Assert.That(socketSettings.ProxyEnabled, Is.False);
            Assert.That(socketSettings.ProxyType, Is.EqualTo("HTTP"));
            Assert.That(socketSettings.ProxyHost, Is.Null);
            Assert.That(socketSettings.ProxyPort, Is.Null);
            Assert.That(socketSettings.ProxyUsername, Is.Null);
            Assert.That(socketSettings.ProxyPassword, Is.Null);
            Assert.That(socketSettings.ProxyConnectTimeoutMs, Is.Null);
            Assert.That(socketSettings.ProxyReadTimeoutMs, Is.Null);
            Assert.That(socketSettings.ProxyBypassForHosts, Is.Empty);
            Assert.That(socketSettings.SocketNodelay, Is.True);
            Assert.That(socketSettings.SocketReceiveBufferSize, Is.Null);
            Assert.That(socketSettings.SocketSendBufferSize, Is.Null);
            Assert.That(socketSettings.SocketReceiveTimeout, Is.Null);
            Assert.That(socketSettings.SocketSendTimeout, Is.Null);
            Assert.That(socketSettings.ServerCommonName, Is.Null);
            Assert.That(socketSettings.ValidateCertificates, Is.True);
            Assert.That(socketSettings.CertificatePath, Is.Null);
            Assert.That(socketSettings.CertificatePassword, Is.Null);
            Assert.That(socketSettings.SslProtocol, Is.EqualTo(SslProtocols.None));
            Assert.That(socketSettings.CheckCertificateRevocation, Is.True);
            Assert.That(socketSettings.UseSSL, Is.False);
            Assert.That(socketSettings.CACertificatePath, Is.Null);
            Assert.That(socketSettings.RequireClientCertificate, Is.True);
        }

        private SettingsDictionary BaseTestDict()
        {
            SettingsDictionary dict = new SettingsDictionary();
            dict.SetBool(SessionSettings.SOCKET_IGNORE_PROXY, false);
            dict.SetBool(SessionSettings.PROXY_ENABLED, true);
            dict.SetString(SessionSettings.PROXY_TYPE, "HTTP");
            dict.SetString(SessionSettings.PROXY_HOST, "127.0.0.1");
            dict.SetLong(SessionSettings.PROXY_PORT, 3128);
            dict.SetString(SessionSettings.PROXY_USERNAME, "user");
            dict.SetString(SessionSettings.PROXY_PASSWORD, "pass");
            dict.SetLong(SessionSettings.PROXY_CONNECT_TIMEOUT_MS, 1200);
            dict.SetLong(SessionSettings.PROXY_READ_TIMEOUT_MS, 3400);
            dict.SetString(SessionSettings.PROXY_BYPASS_FOR_HOSTS, "localhost,example.org");
            dict.SetBool(SessionSettings.SOCKET_NODELAY, false);
            dict.SetLong(SessionSettings.SOCKET_RECEIVE_BUFFER_SIZE, 1);
            dict.SetLong(SessionSettings.SOCKET_SEND_BUFFER_SIZE, 2);
            dict.SetLong(SessionSettings.SOCKET_RECEIVE_TIMEOUT, 3);
            dict.SetLong(SessionSettings.SOCKET_SEND_TIMEOUT, 4);
            dict.SetString(SessionSettings.SSL_SERVERNAME, "SSL_SERVERNAME");
            dict.SetBool(SessionSettings.SSL_VALIDATE_CERTIFICATES, false);
            dict.SetString(SessionSettings.SSL_CERTIFICATE, "SSL_CERTIFICATE");
            dict.SetString(SessionSettings.SSL_CA_CERTIFICATE, "SSL_CA_CERTIFICATE");
            dict.SetString(SessionSettings.SSL_CERTIFICATE_PASSWORD, "SSL_CERTIFICATE_PASSWORD");
            dict.SetString(SessionSettings.SSL_PROTOCOLS, "Tls12");
            dict.SetBool(SessionSettings.SSL_CHECK_CERTIFICATE_REVOCATION, false);
            dict.SetBool(SessionSettings.SSL_ENABLE, true);
            dict.SetBool(SessionSettings.SSL_REQUIRE_CLIENT_CERTIFICATE, false);
            return dict;
        }

        [Test]
        public void Configure()
        {
            SocketSettings socketSettings = new SocketSettings();
            socketSettings.Configure(BaseTestDict());

            Assert.That(socketSettings.SocketIgnoreProxy, Is.False);
            Assert.That(socketSettings.ProxyEnabled, Is.True);
            Assert.That(socketSettings.ProxyType, Is.EqualTo("HTTP"));
            Assert.That(socketSettings.ProxyHost, Is.EqualTo("127.0.0.1"));
            Assert.That(socketSettings.ProxyPort, Is.EqualTo(3128));
            Assert.That(socketSettings.ProxyUsername, Is.EqualTo("user"));
            Assert.That(socketSettings.ProxyPassword, Is.EqualTo("pass"));
            Assert.That(socketSettings.ProxyConnectTimeoutMs, Is.EqualTo(1200));
            Assert.That(socketSettings.ProxyReadTimeoutMs, Is.EqualTo(3400));
            Assert.That(socketSettings.ProxyBypassForHosts.SetEquals(["localhost", "example.org"]), Is.True);
            Assert.That(socketSettings.SocketNodelay, Is.False);
            Assert.That(socketSettings.SocketReceiveBufferSize, Is.EqualTo(1));
            Assert.That(socketSettings.SocketSendBufferSize, Is.EqualTo(2));
            Assert.That(socketSettings.SocketReceiveTimeout, Is.EqualTo(3));
            Assert.That(socketSettings.SocketSendTimeout, Is.EqualTo(4));
            Assert.That(socketSettings.ServerCommonName, Is.EqualTo("SSL_SERVERNAME"));
            Assert.That(socketSettings.ValidateCertificates, Is.False);
            Assert.That(socketSettings.CertificatePath, Is.EqualTo("SSL_CERTIFICATE"));
            Assert.That(socketSettings.CACertificatePath, Is.EqualTo("SSL_CA_CERTIFICATE"));
            Assert.That(socketSettings.CertificatePassword, Is.EqualTo("SSL_CERTIFICATE_PASSWORD"));
            Assert.That(socketSettings.SslProtocol, Is.EqualTo(SslProtocols.Tls12));
            Assert.That(socketSettings.CheckCertificateRevocation, Is.False);
            Assert.That(socketSettings.UseSSL, Is.True);
            Assert.That(socketSettings.RequireClientCertificate, Is.False);
        }

        private SocketSettings CreateWithSslConfig(bool sslValidateCertificates, bool sslCheckCertificateRevocation)
        {
            var dict = BaseTestDict();
            dict.SetBool(SessionSettings.SSL_VALIDATE_CERTIFICATES, sslValidateCertificates);
            dict.SetBool(SessionSettings.SSL_CHECK_CERTIFICATE_REVOCATION, sslCheckCertificateRevocation);
            SocketSettings socketSettings = new SocketSettings();
            socketSettings.Configure(dict);
            return socketSettings;
        }

        [Test]
        public void Configure_SslOverride()
        {
            // SSLValidateCertificates=true does not affect SSLCheckCertificateRevocation
            var socketSettings = CreateWithSslConfig(true, false);
            Assert.That(socketSettings.ValidateCertificates, Is.True);
            Assert.That(socketSettings.CheckCertificateRevocation, Is.False);

            socketSettings = CreateWithSslConfig(true, true);
            Assert.That(socketSettings.ValidateCertificates, Is.True);
            Assert.That(socketSettings.CheckCertificateRevocation, Is.True);

            // SSLValidateCertificates=false means SSLCheckCertificateRevocation must be false
            socketSettings = CreateWithSslConfig(false, true);
            Assert.That(socketSettings.ValidateCertificates, Is.False);
            Assert.That(socketSettings.CheckCertificateRevocation, Is.False);

            socketSettings = CreateWithSslConfig(false, false);
            Assert.That(socketSettings.ValidateCertificates, Is.False);
            Assert.That(socketSettings.CheckCertificateRevocation, Is.False);
        }
    }
}
