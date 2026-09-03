using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class DisplayDefinitionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.DisplayDefinitionTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WritesAndReadsDisplayDefinition()
    {
        var path = Path.Combine(_directory, "living-room.display.json");
        var definition = new DisplayDefinitionDocument
        {
            Id = "living-room",
            Name = "Living room",
            Connection = new DisplayConnectionDefinition
            {
                Host = "192.0.2.10",
                Secure = true,
                AllowUntrustedCertificate = true
            },
            DefaultMenu = "sdr",
            Menus =
            [
                new DisplayMenuDefinitionReference
                {
                    Id = "sdr",
                    DefinitionId = "s95f-1296-sdr",
                    Source = DisplayMenuDefinitionSource.UserData,
                    Path = "../menu-definitions/s95f-1296-sdr.json",
                    ConfigurationId = "default"
                }
            ]
        };
        var store = new DisplayDefinitionStore();

        await store.SaveAsync(path, definition);
        var loaded = await store.LoadAsync(path);

        Assert.Equal(definition.Id, loaded.Id);
        Assert.Equal(definition.Name, loaded.Name);
        Assert.Equal(definition.Connection, loaded.Connection);
        Assert.Equal(definition.DefaultMenu, loaded.DefaultMenu);
        Assert.Equal(Assert.Single(definition.Menus), Assert.Single(loaded.Menus));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"source\": \"userData\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnknownJsonFieldWithSourceLocation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "broken.display.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": 1,
              "id": "broken",
              "name": "Broken",
              "unexpected": true,
              "connection": {},
              "menu": { "definitionId": "test" }
            }
            """);

        var exception = await Assert.ThrowsAsync<DisplayDefinitionValidationException>(
            () => new DisplayDefinitionStore().LoadAsync(path));

        Assert.Contains("line 5", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unexpected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
