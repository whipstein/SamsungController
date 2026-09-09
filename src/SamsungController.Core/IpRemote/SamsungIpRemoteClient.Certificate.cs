using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SamsungController.Core.IpRemote;

public sealed partial class SamsungIpRemoteClient
{
    public async Task<SamsungCertificateInspection> InspectCertificateAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default)
    {
        var endpoint = options.Endpoint;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var socket = new TcpClient { NoDelay = true };
            string? fingerprint = null;
            // This isolated socket is ONLY for inspection. It never enters the
            // HTTP pool, loads credentials, writes application data, or saves trust.
            await socket.ConnectAsync(endpoint.DnsSafeHost, endpoint.Port, timeout.Token).ConfigureAwait(false);
            await using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                fingerprint = peer.GetCertHashString(HashAlgorithmName.SHA256);
                return true; // Observe only; the user must confirm before a separate pinned connection.
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.DnsSafeHost,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck }
            }, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new(endpoint.AbsoluteUri, SamsungIpRemoteOutcome.Success,
                "Certificate retrieved. No pairing request, saved token, or TV command was sent. Review before trusting.", fingerprint);
        }
        catch (OperationCanceledException)
        {
            return new(endpoint.AbsoluteUri, cancellationToken.IsCancellationRequested ? SamsungIpRemoteOutcome.Canceled : SamsungIpRemoteOutcome.Timeout,
                cancellationToken.IsCancellationRequested ? "Certificate check canceled. Trust was not changed." : "Certificate check timed out. Check TV power, IP Remote, address, and local-network/VPN access.");
        }
        catch (Exception error) when (error is SocketException or IOException)
        {
            return new(endpoint.AbsoluteUri, SamsungIpRemoteOutcome.TransportError, DescribeTransportFailure(new HttpRequestException("Certificate inspection transport failed.", error)));
        }
        catch (AuthenticationException)
        {
            return new(endpoint.AbsoluteUri, SamsungIpRemoteOutcome.CertificateError, "Could not complete the certificate check. Verify this is the TV's HTTPS IP Remote port. Trust was not changed.");
        }
        finally { _gate.Release(); }
    }
}
