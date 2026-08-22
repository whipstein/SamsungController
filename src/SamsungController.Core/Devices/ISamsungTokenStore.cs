namespace SamsungController.Core.Devices;

public interface ISamsungTokenStore
{
    Task<string?> LoadAsync(string host, CancellationToken cancellationToken = default);

    Task SaveAsync(string host, string token, CancellationToken cancellationToken = default);

    Task RemoveAsync(string host, CancellationToken cancellationToken = default);
}
