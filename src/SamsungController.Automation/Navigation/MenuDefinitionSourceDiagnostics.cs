using System.Text;
using System.Text.Json;

namespace SamsungController.Automation.Navigation;

public static class MenuDefinitionSourceDiagnostics
{
    public static MenuDefinitionParseException AddJsonLocation(
        string json,
        MenuDefinitionParseException exception)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(exception);
        var sourceMap = JsonSourceMap.Create(json);
        if (!sourceMap.TryFindParseError(exception.Message, out var location))
        {
            location = new SourceLocation(1, 1);
        }

        return new MenuDefinitionParseException(
            $"Invalid menu definition JSON at {location}: {exception.Message}",
            exception);
    }

    public static MenuDefinitionValidationException AddJsonLocations(
        string json,
        MenuDefinitionValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(exception);
        var sourceMap = JsonSourceMap.Create(json);
        var errors = exception.Errors
            .Select(error =>
            {
                if (!sourceMap.TryFindValidationError(error, out var location))
                {
                    location = new SourceLocation(1, 1);
                }

                return error with { Location = $"{location} · {error.Location}" };
            })
            .ToArray();
        return new MenuDefinitionValidationException(errors);
    }

    public static MenuDefinitionParseException FromJsonSyntaxError(JsonException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var line = checked((exception.LineNumber ?? 0) + 1);
        var column = checked((exception.BytePositionInLine ?? 0) + 1);
        return new MenuDefinitionParseException(
            $"Invalid menu definition JSON at line {line}, column {column}: {exception.Message}",
            exception);
    }

    private sealed class JsonSourceMap
    {
        private readonly Dictionary<string, List<SourceLocation>> _properties;
        private readonly Dictionary<string, List<SourceLocation>> _identifiers;

        private JsonSourceMap(
            Dictionary<string, List<SourceLocation>> properties,
            Dictionary<string, List<SourceLocation>> identifiers)
        {
            _properties = properties;
            _identifiers = identifiers;
        }

        public static JsonSourceMap Create(string json)
        {
            var utf8 = Encoding.UTF8.GetBytes(json);
            var lineStarts = FindLineStarts(utf8);
            var properties = new Dictionary<string, List<SourceLocation>>(
                StringComparer.OrdinalIgnoreCase);
            var identifiers = new Dictionary<string, List<SourceLocation>>(
                StringComparer.OrdinalIgnoreCase);
            var reader = new Utf8JsonReader(utf8);
            string? pendingProperty = null;
            SourceLocation pendingLocation = default;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    pendingProperty = reader.GetString() ?? string.Empty;
                    pendingLocation = Locate(reader.TokenStartIndex, lineStarts);
                    Add(properties, pendingProperty, pendingLocation);
                    continue;
                }

                if (pendingProperty is not null
                    && pendingProperty.Equals("id", StringComparison.OrdinalIgnoreCase)
                    && reader.TokenType == JsonTokenType.String)
                {
                    Add(identifiers, reader.GetString() ?? string.Empty, pendingLocation);
                }

                pendingProperty = null;
            }

            return new JsonSourceMap(properties, identifiers);
        }

        public bool TryFindParseError(string message, out SourceLocation location)
        {
            const string unknownPrefix = "Unknown field(s)";
            if (message.StartsWith(unknownPrefix, StringComparison.Ordinal))
            {
                var separator = message.LastIndexOf(": ", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    foreach (var field in message[(separator + 2)..]
                                 .TrimEnd('.')
                                 .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryFindProperty(field, 0, out location))
                        {
                            return true;
                        }
                    }
                }
            }

            const string duplicatePrefix = "Field '";
            const string duplicateMarker = "' appears more than once";
            if (message.StartsWith(duplicatePrefix, StringComparison.Ordinal))
            {
                var marker = message.IndexOf(duplicateMarker, StringComparison.Ordinal);
                if (marker > duplicatePrefix.Length
                    && TryFindProperty(
                        message[duplicatePrefix.Length..marker],
                        1,
                        out location))
                {
                    return true;
                }
            }

            foreach (var (identifier, locations) in _identifiers)
            {
                if (locations.Count > 0
                    && message.Contains($"'{identifier}'", StringComparison.OrdinalIgnoreCase))
                {
                    location = locations[0];
                    return true;
                }
            }

            foreach (var (property, locations) in _properties)
            {
                if (locations.Count == 1
                    && message.Contains($"'{property}'", StringComparison.OrdinalIgnoreCase))
                {
                    location = locations[0];
                    return true;
                }
            }

            location = default;
            return false;
        }

        public bool TryFindValidationError(
            MenuDefinitionValidationError error,
            out SourceLocation location)
        {
            var firstQuote = error.Location.IndexOf('\'');
            var secondQuote = firstQuote < 0
                ? -1
                : error.Location.IndexOf('\'', firstQuote + 1);
            if (firstQuote >= 0
                && secondQuote > firstQuote + 1
                && _identifiers.TryGetValue(
                    error.Location[(firstQuote + 1)..secondQuote],
                    out var identifierLocations)
                && identifierLocations.Count > 0)
            {
                location = identifierLocations[0];
                return true;
            }

            if (error.Location.Equals("definition", StringComparison.OrdinalIgnoreCase)
                && TryFindProperty("id", 0, out location))
            {
                return true;
            }

            var propertyName = error.Location.Split(' ', 2)[0]
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? error.Location;
            return TryFindProperty(propertyName, 0, out location);
        }

        private bool TryFindProperty(
            string name,
            int occurrence,
            out SourceLocation location)
        {
            if (_properties.TryGetValue(name, out var locations)
                && locations.Count > occurrence)
            {
                location = locations[occurrence];
                return true;
            }

            location = default;
            return false;
        }

        private static IReadOnlyList<long> FindLineStarts(ReadOnlySpan<byte> utf8)
        {
            var starts = new List<long> { 0 };
            for (var index = 0; index < utf8.Length; index++)
            {
                if (utf8[index] == (byte)'\n')
                {
                    starts.Add(index + 1L);
                }
            }

            return starts;
        }

        private static SourceLocation Locate(
            long byteOffset,
            IReadOnlyList<long> lineStarts)
        {
            var low = 0;
            var high = lineStarts.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                if (lineStarts[middle] <= byteOffset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            var lineIndex = Math.Max(0, high);
            return new SourceLocation(
                lineIndex + 1,
                checked((int)(byteOffset - lineStarts[lineIndex] + 1)));
        }

        private static void Add(
            IDictionary<string, List<SourceLocation>> values,
            string key,
            SourceLocation location)
        {
            if (!values.TryGetValue(key, out var locations))
            {
                locations = [];
                values[key] = locations;
            }

            locations.Add(location);
        }
    }

    private readonly record struct SourceLocation(int Line, int Column)
    {
        public override string ToString() => $"line {Line}, column {Column}";
    }
}
