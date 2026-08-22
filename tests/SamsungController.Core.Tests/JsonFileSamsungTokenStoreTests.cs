using SamsungController.Core.Devices;

namespace SamsungController.Core.Tests;

public sealed class JsonFileSamsungTokenStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavesLoadsAndRemovesTokensByNormalizedHost()
    {
        var path = Path.Combine(_directory, "tokens.json");
        var store = new JsonFileSamsungTokenStore(path);

        await store.SaveAsync("TV.EXAMPLE ", "secret-token");

        Assert.Equal("secret-token", await store.LoadAsync("tv.example"));
        await store.RemoveAsync("Tv.Example");
        Assert.Null(await store.LoadAsync("tv.example"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
