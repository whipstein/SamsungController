using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpMenuSection(string Id, string Name, string Category);
public sealed record IpMenuControl(string Id, string Method, string Field, string Name, string Section, IpRemoteParameter Parameter)
{
    public SamsungIpRemoteCommand Command => SamsungIpRemoteCommands.Get(Method);
    public bool ChangesContext => Method is "inputSourceControl" or "pictureModeControl" or "gameModeControl" or "artModeControl" or "pictureCalibrationModeControl";
    public bool IsSelector => Method is "WB20P.IntervalControl" or "colorSpace.ColorControl";
    public bool IsSwitch => Parameter.Kind == IpRemoteParameterKind.Choice && Parameter.Choices.Count == 2
        && Parameter.Choices.Any(choice => choice.EndsWith("On", StringComparison.OrdinalIgnoreCase))
        && Parameter.Choices.Any(choice => choice.EndsWith("Off", StringComparison.OrdinalIgnoreCase));
    public string RangeSource => "Documented limits — not returned by the TV query";
}

public static class IpMenuCatalog
{
    public static IReadOnlyList<IpMenuSection> Sections { get; } = Array.AsReadOnly(new[]
    {
        new IpMenuSection("expert", "Expert settings", "Picture"),
        new IpMenuSection("white2", "2-point white balance", "Picture"),
        new IpMenuSection("white20", "20-point white balance", "Picture"),
        new IpMenuSection("color", "Color", "Picture"),
        new IpMenuSection("sound", "Sound", "Sound"),
        new IpMenuSection("system", "Inputs, panel & power saving", "System")
    });

    public static IReadOnlyList<IpMenuControl> Controls { get; } = Array.AsReadOnly(Build().ToArray());
    public static IpMenuControl Get(string id) => Controls.FirstOrDefault(control => control.Id == id)
        ?? throw new ArgumentException("Unknown direct menu control.");
    public static IEnumerable<IpMenuControl> ForSection(string section) => Controls.Where(control => control.Section == section);
    private static IEnumerable<IpMenuControl> Build()
    {
        foreach (var command in SamsungIpRemoteCommands.All)
        {
            if (!command.CanQuery || command.DeviceList || command.IsReadOnly || command.Method is "powerControl" or "directAccessControl" or "directChannelControl" or "firstScreenAppControl" or "multiviewControl") continue;
            var section = command.Group switch
            {
                "White balance · 2 point" => command.Method == "colorToneControl" ? "expert" : "white2",
                "White balance · 20 point" => "white20",
                "Color space" => "color",
                "Sound" => "sound",
                "Sources" or "Panel" or "Power / Eco" or "Art / Power" => "system",
                _ => "expert"
            };
            foreach (var original in command.Parameters)
            {
                if (original.Kind is not (IpRemoteParameterKind.Integer or IpRemoteParameterKind.Choice)) continue;
                // Samsung's 2023 consumer-TV list, not the older 0..100 transport envelope.
                var parameter = original.Name switch
                {
                    "contrast" or "color" => original with { Minimum = 0, Maximum = 50 },
                    "sharpness" => original with { Minimum = 0, Maximum = 20 },
                    "gammaMode" => original with { Choices = Array.AsReadOnly(new[] { "BT.1886", "ST.2084", "HLG", "2.2", "2.20" }) },
                    _ => original
                };
                var name = command.Method == "WB2PointControl" ? original.Name.Replace('-', ' ') : command.Name;
                yield return new(command.Method + "/" + parameter.Name, command.Method, parameter.Name, name, section, parameter);
            }
        }
    }

    public static JsonNode ParseTarget(IpMenuControl control, string text)
    {
        var parameter = control.Parameter;
        if (parameter.Kind == IpRemoteParameterKind.Integer)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number < parameter.Minimum || number > parameter.Maximum)
                throw new ArgumentException($"{control.Name} must be an integer from {parameter.Minimum} to {parameter.Maximum}. Nothing was sent.");
            return JsonValue.Create(number)!;
        }
        if (!parameter.Choices.Contains(text, StringComparer.Ordinal)) throw new ArgumentException($"Choose a documented {control.Name} option.");
        return JsonValue.Create(text)!;
    }
}

public sealed record IpMenuRead(DateTimeOffset ReadAt, JsonObject? Values, SamsungIpRemoteOutcome Outcome, string Message);
public sealed record IpMenuDraft(string ControlId, JsonNode Target, JsonNode Original, JsonObject Prerequisites, string Input, string PictureMode);
public sealed record IpMenuPreferences(bool ApplyImmediately = false);
public sealed record IpMenuSnapshot
{
    public bool Connected { get; init; }
    public Guid SessionId { get; init; }
    public DateTimeOffset? LastContact { get; init; }
    public JsonObject Tv { get; init; } = new();
    public JsonObject Video { get; init; } = new();
    public IReadOnlyDictionary<string, IpMenuRead> Readings { get; init; } = new Dictionary<string, IpMenuRead>();
    public IReadOnlyDictionary<string, DateTimeOffset> SectionsRead { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, IpMenuDraft> Pending { get; init; } = new Dictionary<string, IpMenuDraft>();
    public IpMenuPreferences Preferences { get; init; } = new();
    public IpMenuUpdate? Update { get; init; }
    public string Status { get; init; } = "Connect to read current TV settings.";
    public string? Input => Tv["inputSource"]?.ToString();
    public string? PictureMode => Tv["pictureMode"]?.ToString();
    public JsonNode? Value(IpMenuControl control) => Readings.GetValueOrDefault(control.Method) is { Outcome: SamsungIpRemoteOutcome.Success, Values: { } values } ? values[control.Field] : null;
}

public sealed record IpMenuUpdateStep(string ControlId, JsonNode Original, JsonNode Target, string Status = "Pending");
public sealed record IpMenuUpdate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Endpoint { get; init; } = "";
    public string Input { get; init; } = "";
    public string PictureMode { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public IReadOnlyList<IpMenuUpdateStep> Steps { get; init; } = [];
    public string Status { get; init; } = "Running";
    public string Message { get; init; } = "";
    public bool NeedsReview => Steps.Any(step => step.Status is "Sending" or "Uncertain");
}
