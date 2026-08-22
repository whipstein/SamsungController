using SamsungController.Core.Connection;
using SamsungController.Core.Devices;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli;

internal interface IInteractiveSamsungClient
{
    SamsungConnectionState State { get; }

    long ConnectionGeneration { get; }

    Task SendKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken);

    Task SendQueryAsync(SamsungQuery query, CancellationToken cancellationToken);

    Task SendRawAsync(string rawJson, CancellationToken cancellationToken);
}

internal sealed class InteractiveSamsungClient(SamsungTvClient client) : IInteractiveSamsungClient
{
    private readonly SamsungTvClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public SamsungConnectionState State => _client.State;

    public long ConnectionGeneration => _client.ConnectionGeneration;

    public Task SendKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken) =>
        _client.SendKeyAsync(key, action, cancellationToken);

    public Task SendQueryAsync(SamsungQuery query, CancellationToken cancellationToken) =>
        _client.SendQueryAsync(query, cancellationToken);

    public Task SendRawAsync(string rawJson, CancellationToken cancellationToken) =>
        _client.SendRawAsync(rawJson, cancellationToken);
}
