using SamsungController.Core.Devices;

namespace SamsungController.Core.Tests.Fakes;

internal sealed class InMemoryTokenStore : ISamsungTokenStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> LoadAsync(string host, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.GetValueOrDefault(host));

    public Task SaveAsync(
        string host,
        string token,
        CancellationToken cancellationToken = default)
    {
        _tokens[host] = token;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string host, CancellationToken cancellationToken = default)
    {
        _tokens.Remove(host);
        return Task.CompletedTask;
    }
}
