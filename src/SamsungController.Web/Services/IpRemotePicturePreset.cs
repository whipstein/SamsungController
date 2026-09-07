using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

// Portable desired values, not proof of verification or a current TV reading.
// Deliberately has no endpoint, token, certificate, or capability fields.
public sealed record IpRemotePicturePreset
{
    public string Format { get; init; } = "SamsungController.IPRemote.PicturePreset.v1";
    public string Name { get; init; } = "Picture settings";
    public IpRemotePresetContext Context { get; init; } = new("", "", "", "", "", "", "");
    public Dictionary<string, int> Values { get; init; } = new(StringComparer.Ordinal);
}

public sealed record IpRemotePresetContext(string Model, string Firmware, string InputSource, string PictureMode,
    string Signal, string ReportedInput, string ReportedPictureMode)
{
    public static IpRemotePresetContext FromReading(IpRemoteWorkspaceReading reading) => new(reading.Profile.Model,
        reading.Profile.Firmware, reading.Profile.InputSource, reading.Profile.PictureMode, reading.Profile.Signal,
        reading.ReportedInput, reading.ReportedPictureMode);
}

public static class IpRemotePicturePresets
{
    public const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static string Export(IpRemoteWorkspaceReading reading, IReadOnlyDictionary<string, int> values, string name)
    {
        var preset = new IpRemotePicturePreset
        {
            Name = name.Trim(),
            Context = IpRemotePresetContext.FromReading(reading),
            Values = values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };
        Validate(preset);
        if (values.Keys.Any(control => reading.Value(control) is null))
            throw new InvalidOperationException("Cannot export values for a control missing from the TV reading.");
        return JsonSerializer.Serialize(preset, Options);
    }

    public static IpRemotePicturePreset Import(string json, IpRemoteWorkspaceReading reading)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new InvalidOperationException("An IP Remote preset must be at most 64 KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String || format.GetString() != "SamsungController.IPRemote.PicturePreset.v1")
            throw new InvalidOperationException("The file must explicitly identify itself as a SamsungController IP Remote picture preset.");
        var preset = JsonSerializer.Deserialize<IpRemotePicturePreset>(json, Options) ?? throw new JsonException("The preset is empty.");
        Validate(preset);
        if (preset.Context != IpRemotePresetContext.FromReading(reading))
            throw new InvalidOperationException("This preset's model, firmware, annotated conditions, or reported input/picture mode do not match the current reading. Select the matching display context and refresh before loading it.");
        if (preset.Values.Keys.Any(control => reading.Value(control) is null))
            throw new InvalidOperationException("The preset includes a control that the TV did not report. No values were staged.");
        return preset;
    }

    private static void Validate(IpRemotePicturePreset preset)
    {
        if (preset.Format != "SamsungController.IPRemote.PicturePreset.v1") throw new InvalidOperationException("This is not an IP Remote picture preset. Menu/calibration files use a different format.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80) throw new InvalidOperationException("Preset names must contain 1–80 characters.");
        if (preset.Context is null || new[] { preset.Context.Model, preset.Context.Firmware, preset.Context.InputSource,
            preset.Context.PictureMode, preset.Context.Signal, preset.Context.ReportedInput, preset.Context.ReportedPictureMode }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512))
            throw new InvalidOperationException("The preset needs complete model, firmware, input, picture-mode, and signal context.");
        if (preset.Values is null || preset.Values.Count is < 1 or > 3
            || preset.Values.Any(item => !SamsungIpRemotePictureControl.IsSupported(item.Key) || item.Value is < 0 or > 100))
            throw new InvalidOperationException("Presets may contain only Contrast, Color, and Sharpness integer values within the protocol envelope 0–100.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JsonException($"Duplicate preset property '{property.Name}'.");
            RejectDuplicateProperties(property.Value);
        }
    }
}
