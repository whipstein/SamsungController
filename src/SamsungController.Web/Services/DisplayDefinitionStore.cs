using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamsungController.Web.Services;

internal enum DisplayMenuDefinitionSource
{
    Any,
    UserData,
    Repository,
    Installation,
    CustomFile
}

internal sealed record DisplayConnectionDefinition
{
    public string? Host { get; init; }

    public bool Secure { get; init; } = true;

    public int? Port { get; init; }

    public bool AllowUntrustedCertificate { get; init; } = true;

    public int? KeepAliveIntervalSeconds { get; init; }

    public int? KeepAliveTimeoutSeconds { get; init; }

    public int? PostConnectWarmupMilliseconds { get; init; }

    public int? ReconnectAfterIdleSeconds { get; init; }
}

internal sealed record DisplayMenuDefinitionReference
{
    public string Id { get; init; } = string.Empty;

    public string DefinitionId { get; init; } = string.Empty;

    public DisplayMenuDefinitionSource Source { get; init; }

    public string? Path { get; init; }

    public string? ConfigurationId { get; init; }
}

internal sealed record DisplayDefinitionDocument
{
    public int Version { get; init; } = 1;

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public DisplayConnectionDefinition Connection { get; init; } = new();

    public string? DefaultMenu { get; init; }

    public IReadOnlyList<DisplayMenuDefinitionReference> Menus { get; init; } = [];
}

internal sealed class DisplayDefinitionValidationException(
    IReadOnlyList<string> errors)
    : Exception(CreateMessage(errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string CreateMessage(IReadOnlyList<string> errors) =>
        errors.Count == 0
            ? "Display definition validation failed."
            : "Display definition validation failed:" + Environment.NewLine
              + string.Join(Environment.NewLine, errors.Select(error => $"- {error}"));
}

internal sealed class DisplayDefinitionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<DisplayDefinitionDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Display definition was not found: {fullPath}",
                fullPath);
        }

        try
        {
            await using var stream = File.OpenRead(fullPath);
            var definition = await JsonSerializer.DeserializeAsync<DisplayDefinitionDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DisplayDefinitionValidationException(
                    ["The document must contain a JSON object."]);
            ValidateAndThrow(definition);
            return definition;
        }
        catch (JsonException exception)
        {
            var line = checked((exception.LineNumber ?? 0) + 1);
            var column = checked((exception.BytePositionInLine ?? 0) + 1);
            throw new DisplayDefinitionValidationException(
                [$"Invalid JSON at line {line}, column {column}: {exception.Message}"]);
        }
    }

    public async Task SaveAsync(
        string path,
        DisplayDefinitionDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateAndThrow(definition);
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
                    definition,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    public static void ValidateAndThrow(DisplayDefinitionDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();
        if (definition.Version != 1)
        {
            errors.Add($"version: Expected 1 but received {definition.Version}.");
        }

        ValidateIdentifier("id", definition.Id, errors);
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add("name: A friendly display name is required.");
        }

        if (definition.Connection is null)
        {
            errors.Add("connection: A connection object is required.");
        }
        else if (definition.Connection.Port is <= 0 or > 65_535)
        {
            errors.Add("connection.port: Use a port from 1 through 65535, or omit it for automatic selection.");
        }

        if (definition.Connection is { } connection)
        {
            if (connection.KeepAliveIntervalSeconds is < 5 or > 120)
            {
                errors.Add("connection.keepAliveIntervalSeconds: Use 5 through 120 seconds.");
            }

            if (connection.KeepAliveTimeoutSeconds is < 0 or 1 or > 60
                || connection.KeepAliveTimeoutSeconds > connection.KeepAliveIntervalSeconds)
            {
                errors.Add("connection.keepAliveTimeoutSeconds: Use 0 or 2 through 60 seconds, no greater than the health-check interval.");
            }

            if (connection.PostConnectWarmupMilliseconds is < 0 or > 10_000)
            {
                errors.Add("connection.postConnectWarmupMilliseconds: Use 0 through 10000 milliseconds.");
            }

            if (connection.ReconnectAfterIdleSeconds is < 0 or (> 0 and < 30) or > 3600)
            {
                errors.Add("connection.reconnectAfterIdleSeconds: Use 0 or 30 through 3600 seconds.");
            }
        }

        if (definition.Menus is null || definition.Menus.Count == 0)
        {
            errors.Add("menus: At least one menu-definition reference is required.");
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var menu in definition.Menus)
            {
                if (menu is null)
                {
                    errors.Add("menus: Null menu references are not allowed.");
                    continue;
                }

                ValidateIdentifier("menus.id", menu.Id, errors);
                if (!string.IsNullOrWhiteSpace(menu.Id) && !ids.Add(menu.Id))
                {
                    errors.Add($"menus: Menu reference ID '{menu.Id}' appears more than once.");
                }

                ValidateIdentifier($"menu '{menu.Id}'.definitionId", menu.DefinitionId, errors);
                if (!Enum.IsDefined(menu.Source))
                {
                    errors.Add($"menu '{menu.Id}'.source: Use any, userData, repository, installation, or customFile.");
                }

                if (menu.Source == DisplayMenuDefinitionSource.CustomFile
                    && string.IsNullOrWhiteSpace(menu.Path))
                {
                    errors.Add($"menu '{menu.Id}'.path: A customFile reference requires an absolute or display-relative path.");
                }
            }

            if (!string.IsNullOrWhiteSpace(definition.DefaultMenu)
                && !ids.Contains(definition.DefaultMenu))
            {
                errors.Add($"defaultMenu: Menu reference '{definition.DefaultMenu}' does not exist.");
            }
        }

        if (errors.Count > 0)
        {
            throw new DisplayDefinitionValidationException(errors);
        }
    }

    public static string CreateIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('-');
        if (normalized.Length == 0)
        {
            return "samsung-display";
        }

        return char.IsLetter(normalized[0]) || normalized[0] == '_'
            ? normalized
            : $"display-{normalized}";
    }

    private static void ValidateIdentifier(
        string location,
        string? value,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{location}: A stable identifier is required.");
            return;
        }

        if (!(char.IsLetter(value[0]) || value[0] == '_')
            || !value.All(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            errors.Add(
                $"{location}: Begin with a letter or underscore and use only letters, numbers, periods, hyphens, and underscores.");
        }
    }
}
