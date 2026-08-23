using System.Text.Json;

namespace SamsungController.Cli;

internal sealed record SamsungCliSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public string? Host { get; init; }

    public string Name { get; init; } = "Samsung TV";

    public string ApplicationName { get; init; } = "SamsungController";

    public bool Secure { get; init; } = true;

    public int? Port { get; init; }

    public string? MacroFilePath { get; init; }

    public string? MenuDefinitionPath { get; init; }

    public static async Task<SamsungCliSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new SamsungCliSettings();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SamsungCliSettings>(
                   stream,
                   SerializerOptions,
                   cancellationToken)
               .ConfigureAwait(false)
            ?? new SamsungCliSettings();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    this,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
