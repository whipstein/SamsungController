using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamsungController.Web.Services;

internal sealed record SamsungWebSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string? Host { get; init; }

    public string Name { get; init; } = "Samsung TV";

    public string ApplicationName { get; init; } = "SamsungController";

    public bool Secure { get; init; } = true;

    public int? Port { get; init; }

    public bool AllowUntrustedCertificate { get; init; } = true;

    public string? MacroFilePath { get; init; }

    public string? MenuDefinitionPath { get; init; }

    public IReadOnlyList<QuickAccessAction>? QuickAccess { get; init; }

    public static async Task<SamsungWebSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new SamsungWebSettings();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SamsungWebSettings>(
                   stream,
                   SerializerOptions,
                   cancellationToken)
               .ConfigureAwait(false)
            ?? new SamsungWebSettings();
    }

    public async Task SaveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
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

        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}
