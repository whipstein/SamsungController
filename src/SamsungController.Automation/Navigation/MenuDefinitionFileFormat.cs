namespace SamsungController.Automation.Navigation;

public enum MenuDefinitionFileFormat
{
    Yaml,
    Json
}

public static class MenuDefinitionFileFormats
{
    public static MenuDefinitionFileFormat DetectForRead(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        return TryFromExtension(path, out var format)
            ? format
            : content.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal)
                ? MenuDefinitionFileFormat.Json
                : MenuDefinitionFileFormat.Yaml;
    }

    public static MenuDefinitionFileFormat DetectForWrite(string path, string? existingContent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TryFromExtension(path, out var format)
            ? format
            : existingContent is not null
                ? DetectForRead(path, existingContent)
                : MenuDefinitionFileFormat.Yaml;
    }

    public static string Extension(MenuDefinitionFileFormat format) => format switch
    {
        MenuDefinitionFileFormat.Yaml => ".yaml",
        MenuDefinitionFileFormat.Json => ".json",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    public static string Label(MenuDefinitionFileFormat format) => format switch
    {
        MenuDefinitionFileFormat.Yaml => "YAML",
        MenuDefinitionFileFormat.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static bool TryFromExtension(
        string path,
        out MenuDefinitionFileFormat format)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".json":
                format = MenuDefinitionFileFormat.Json;
                return true;
            case ".yaml":
            case ".yml":
                format = MenuDefinitionFileFormat.Yaml;
                return true;
            default:
                format = default;
                return false;
        }
    }
}
