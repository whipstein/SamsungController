using System.Text.Json;

namespace SamsungController.Web.Services;

public static class MenuControlTargetProfileSerializer
{
    public const int CurrentVersion = 2;

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
            throw new InvalidOperationException("A calibration file cannot exceed 2 MB.");
        }

        MenuControlTargetProfile document;
        try
        {
            document = JsonSerializer.Deserialize<MenuControlTargetProfile>(json, SerializerOptions)
                ?? throw new InvalidOperationException("The calibration file is empty.");
        }
        catch (JsonException exception)
        {
            var location = exception.LineNumber is { } line
                ? $" at line {line + 1}, column {(exception.BytePositionInLine ?? 0) + 1}"
                : string.Empty;
            throw new InvalidOperationException(
                $"Calibration JSON is invalid{location}: {exception.Message}",
                exception);
        }

        Validate(document);
        return document;
    }

    private static void Validate(MenuControlTargetProfile document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version is not (1 or CurrentVersion))
        {
            throw new InvalidOperationException(
                $"Calibration-file version {document.Version} is not supported; expected version {CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidOperationException("A calibration file must include a name.");
        }

        if (document.Name.Trim().Length > 80)
        {
            throw new InvalidOperationException(
                "A calibration name can contain at most 80 characters.");
        }

        if (string.IsNullOrWhiteSpace(document.DefinitionId))
        {
            throw new InvalidOperationException(
                "A calibration file must identify its menu definition.");
        }

        if (document.ConditionValues is { Count: > 0 } sets)
        {
            if (document.Version < 2 || document.Values is { Count: > 0 })
            {
                throw new InvalidOperationException("Use version 2 with conditionValues, without top-level values, for an all-conditions file.");
            }
            if (sets.Count > 128)
            {
                throw new InvalidOperationException("A calibration file can contain at most 128 input-condition combinations.");
            }
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in sets)
            {
                if (set?.Conditions is null || set.Conditions.Count > 20
                    || set.Conditions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    || set.Conditions.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != set.Conditions.Count)
                {
                    throw new InvalidOperationException("Every conditionValues entry needs a conditions map with at most 20 unique, nonempty state IDs and values.");
                }
                if (!keys.Add(ConditionKey(set.Conditions)))
                {
                    throw new InvalidOperationException("An input-condition combination is duplicated in the calibration file.");
                }
                ValidateValues(set.Values);
            }
            if (sets.Sum(set => set.Values.Count) > 50000)
            {
                throw new InvalidOperationException("An all-conditions calibration file can contain at most 50,000 values.");
            }
            return;
        }

        ValidateValues(document.Values);
    }

    internal static string ConditionKey(IReadOnlyDictionary<string, string> conditions) =>
        JsonSerializer.Serialize(conditions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new[] { pair.Key.ToUpperInvariant(), pair.Value.ToUpperInvariant() }));

    private static void ValidateValues(IReadOnlyList<MenuControlProfileValue>? values)
    {
        if (values is null || values.Count == 0)
        {
            throw new InvalidOperationException(
                "A calibration file must include at least one value.");
        }

        if (values.Count > 5000)
        {
            throw new InvalidOperationException(
                "A calibration file can contain at most 5,000 values.");
        }

        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value is null
                || string.IsNullOrWhiteSpace(value.NodeId)
                || string.IsNullOrWhiteSpace(value.Value))
            {
                throw new InvalidOperationException(
                    "Every calibration value must include a nodeId and value.");
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
                    $"Calibration value '{value.NodeId}' is duplicated.");
            }
        }
    }
}
