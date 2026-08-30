using System.Text.Json;

namespace SamsungController.Web.Services;

public static class MenuControlTargetProfileSerializer
{
    public const int CurrentVersion = 1;

    private const int MaximumDocumentLength = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize(MenuControlTargetProfile document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    public static MenuControlTargetProfile Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumDocumentLength)
        {
            throw new InvalidOperationException("A target-calibration file cannot exceed 2 MB.");
        }

        MenuControlTargetProfile document;
        try
        {
            document = JsonSerializer.Deserialize<MenuControlTargetProfile>(json, SerializerOptions)
                ?? throw new InvalidOperationException("The target-calibration file is empty.");
        }
        catch (JsonException exception)
        {
            var location = exception.LineNumber is { } line
                ? $" at line {line + 1}, column {(exception.BytePositionInLine ?? 0) + 1}"
                : string.Empty;
            throw new InvalidOperationException(
                $"Target-calibration JSON is invalid{location}: {exception.Message}",
                exception);
        }

        Validate(document);
        return document;
    }

    private static void Validate(MenuControlTargetProfile document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Target-calibration version {document.Version} is not supported; expected version {CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidOperationException("A target-calibration file must include a name.");
        }

        if (document.Name.Trim().Length > 80)
        {
            throw new InvalidOperationException(
                "A target-calibration name can contain at most 80 characters.");
        }

        if (string.IsNullOrWhiteSpace(document.DefinitionId))
        {
            throw new InvalidOperationException(
                "A target-calibration file must identify its menu definition.");
        }

        if (document.Values is null || document.Values.Count == 0)
        {
            throw new InvalidOperationException(
                "A target-calibration file must include at least one desired value.");
        }

        if (document.Values.Count > 5000)
        {
            throw new InvalidOperationException(
                "A target-calibration file can contain at most 5,000 values.");
        }

        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in document.Values)
        {
            if (value is null
                || string.IsNullOrWhiteSpace(value.NodeId)
                || string.IsNullOrWhiteSpace(value.Value))
            {
                throw new InvalidOperationException(
                    "Every target-calibration value must include a nodeId and value.");
            }

            var hasSelectorId = !string.IsNullOrWhiteSpace(value.SelectorNodeId);
            var hasSelectorValue = !string.IsNullOrWhiteSpace(value.SelectorValue);
            if (hasSelectorId != hasSelectorValue)
            {
                throw new InvalidOperationException(
                    $"Indexed target '{value.NodeId}' must include both selectorNodeId and selectorValue.");
            }

            var key = $"{value.SelectorNodeId}\u001f{value.SelectorValue}\u001f{value.NodeId}";
            if (!uniqueKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Target-calibration value '{value.NodeId}' is duplicated.");
            }
        }
    }
}
