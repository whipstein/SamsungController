using System.Text.Json;
using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Services;

internal sealed class MenuVerificationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;

    public MenuVerificationStore(string configurationDirectory)
    {
        _directory = Path.Combine(
            Path.GetFullPath(configurationDirectory),
            "menu-verifications");
    }

    public async Task<MenuVerificationManifest?> LoadAsync(
        string definitionId,
        MenuVerificationDisplay display,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(definitionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<MenuVerificationStoreDocument>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        if (document is null
            || !document.DefinitionId.Equals(
                definitionId,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return document.Profiles.FirstOrDefault(profile =>
            HasSameDisplay(profile.Display, display));
    }

    public async Task SaveAsync(
        string definitionId,
        MenuVerificationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = GetPath(definitionId);
        MenuVerificationStoreDocument document;
        if (File.Exists(path))
        {
            await using var input = File.OpenRead(path);
            document = await JsonSerializer.DeserializeAsync<MenuVerificationStoreDocument>(
                    input,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new MenuVerificationStoreDocument(1, definitionId, []);
        }
        else
        {
            document = new MenuVerificationStoreDocument(1, definitionId, []);
        }

        var profiles = document.Profiles
            .Where(profile => !HasSameDisplay(profile.Display, manifest.Display))
            .Append(manifest)
            .OrderBy(profile => FormatDisplayKey(profile.Display), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        document = new MenuVerificationStoreDocument(1, definitionId, profiles);
        Directory.CreateDirectory(_directory);
        var temporaryPath = path + ".tmp";
        await using (var output = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    output,
                    document,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public string GetPath(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        var safeName = string.Concat(definitionId.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? char.ToLowerInvariant(character)
                : '-'));
        safeName = safeName.Trim('-');
        if (safeName.Length == 0)
        {
            safeName = "menu";
        }

        if (safeName.Length > 80)
        {
            safeName = safeName[..80];
        }

        return Path.Combine(_directory, $"{safeName}.verification.json");
    }

    private static bool HasSameDisplay(
        MenuVerificationDisplay left,
        MenuVerificationDisplay right) =>
        FormatDisplayKey(left).Equals(
            FormatDisplayKey(right),
            StringComparison.OrdinalIgnoreCase);

    private static string FormatDisplayKey(MenuVerificationDisplay display) => string.Join(
        "\u001f",
        display.Model,
        display.Firmware,
        display.Signal,
        display.PictureMode,
        display.Input);

    private sealed record MenuVerificationStoreDocument(
        int Version,
        string DefinitionId,
        IReadOnlyList<MenuVerificationManifest> Profiles);
}
