namespace SamsungController.Core.Connection;

public interface ISamsungTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    long Generation { get; }

    Task ConnectAsync(
        Uri endpoint,
        TimeSpan timeout,
        bool allowUntrustedCertificate,
        CancellationToken cancellationToken = default);

    Task SendAsync(string rawJson, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReceiveAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
