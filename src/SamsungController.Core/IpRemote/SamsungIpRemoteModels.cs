using System.Text.Json.Nodes;

namespace SamsungController.Core.IpRemote;

public sealed record SamsungIpRemoteOptions
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1516;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool AllowUntrustedCertificate { get; init; }
    public string? CertificateSha256 { get; init; }

    public Uri Endpoint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host.Trim().Trim('[', ']')) == UriHostNameType.Unknown)
                throw new ArgumentException("Enter a TV IP address or hostname, without a URL, path, or credentials.");
            if (Port is < 1 or > 65535)
                throw new ArgumentException("The IP Remote port must be between 1 and 65535.");
            if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2)
                || PairingTimeout <= TimeSpan.Zero || PairingTimeout > TimeSpan.FromMinutes(2))
                throw new ArgumentException("Request and pairing timeouts must be greater than zero and at most 120 seconds.");
            if (NormalizedCertificatePin is { } pin && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
                throw new ArgumentException("The certificate pin must be a complete SHA-256 fingerprint (64 hexadecimal digits).");
            return new UriBuilder(Uri.UriSchemeHttps, Host.Trim().Trim('[', ']'), Port, "/").Uri;
        }
    }

    public string? NormalizedCertificatePin => string.IsNullOrWhiteSpace(CertificateSha256)
        ? null : CertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

public enum SamsungIpRemoteOutcome
{
    Success, NotPaired, Unauthorized, Unsupported, RpcError, HttpError,
    ProtocolError, TransportError, CertificateError, Timeout, Canceled, StorageError
}

// No credential is ever returned in this model. Request, response, and Result
// are sanitized before leaving the client, including createAccessToken replies.
public sealed record SamsungIpRemoteExchange(
    DateTimeOffset Timestamp,
    long RequestId,
    string Method,
    string Endpoint,
    SamsungIpRemoteOutcome Outcome,
    string Message,
    string RequestJson,
    string? ResponseJson = null,
    JsonObject? Result = null,
    int? HttpStatus = null,
    int? RpcErrorCode = null,
    long DurationMilliseconds = 0,
    string? ObservedCertificateSha256 = null)
{
    public bool IsSuccess => Outcome == SamsungIpRemoteOutcome.Success;
}

public interface ISamsungIpRemoteClient
{
    Task<bool> HasTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default);
    Task ForgetTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default);
    Task<SamsungIpRemoteExchange> PairAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default);
    Task<SamsungIpRemoteExchange> ReadAsync(SamsungIpRemoteOptions options, string method, CancellationToken cancellationToken = default);
    Task<SamsungIpRemoteExchange> WritePictureControlAsync(SamsungIpRemoteOptions options, string control, int value, CancellationToken cancellationToken = default);
}
