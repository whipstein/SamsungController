using System.Globalization;
using System.Text.Json.Nodes;

namespace SamsungController.Core.IpRemote;

public enum IpRemoteParameterKind { Integer, Choice, Text, DeviceId, Channel, Url }

public sealed record IpRemoteParameter(string Name, IpRemoteParameterKind Kind, IReadOnlyList<string> Choices,
    int Minimum = 0, int Maximum = 100, bool Optional = false)
{
    internal void Validate(JsonNode? node)
    {
        if (node is not JsonValue scalar) throw new ArgumentException($"{Name} needs a scalar value.");
        if (Kind == IpRemoteParameterKind.Integer)
        {
            if (!scalar.TryGetValue<int>(out var value) || value < Minimum || value > Maximum)
                throw new ArgumentException($"{Name} must be an integer from {Minimum} to {Maximum}. No command was sent.");
            return;
        }
        if (Kind == IpRemoteParameterKind.DeviceId && scalar.TryGetValue<long>(out var id) && id >= 0) return;
        if (!scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > (Kind == IpRemoteParameterKind.Url ? 2048 : 256))
            throw new ArgumentException($"{Name} needs a nonempty, bounded text value.");
        if (Kind == IpRemoteParameterKind.Choice && !Choices.Contains(text, StringComparer.Ordinal))
            throw new ArgumentException($"Choose a documented {Name} value. Arbitrary values are not permitted.");
        if (Kind == IpRemoteParameterKind.Channel && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var channel) || channel is < 0 or > 999))
            throw new ArgumentException("channelNum must be decimal text from 0 to 999.");
        if (Kind == IpRemoteParameterKind.Url && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0))
            throw new ArgumentException("Use an absolute HTTP/HTTPS URL without credentials.");
    }
}

/// <summary>A closed catalog of documented protocol shapes, not a display compatibility claim.</summary>
public sealed record SamsungIpRemoteCommand(string Method, string Name, string Group, IReadOnlyList<IpRemoteParameter> Parameters,
    bool CanQuery = false, string? ReadbackField = null, string? ReadbackMethod = null, string? ManagedPictureControl = null,
    bool SkipReadback = false, string Notes = "Display support and available values must be tested.")
{
    public bool DeviceList => Method is "USBSourceControl" or "RVUSourceControl" or "externalSpeakerControl";
    public bool AllowsArrayResult => DeviceList || Method is "firstScreenAppControl" or "multiviewControl";
    public bool HasDedicatedReadback => ReadbackMethod is not null and not ("getTVStates" or "getVideoStates");
    public IReadOnlyList<IpRemoteCommandRequirement> Requirements => SamsungIpRemoteCommands.RequirementsFor(Method);
    public bool IsReadOnly => Parameters.Count == 0;
    public JsonObject Validate(JsonObject parameters, bool query)
    {
        var copy = (JsonObject)parameters.DeepClone();
        if (query)
        {
            if (!CanQuery || copy.Count != 0) throw new ArgumentException("Only documented read/list operations may run without preparation, and they accept no custom parameters.");
            return copy;
        }
        if (IsReadOnly) throw new ArgumentException("Use Read/list for this method.");
        if (copy.Count == 0) throw new ArgumentException("Enter at least one setting to change. Use Read/list to query without changing anything.");
        if (copy.Any(item => !Parameters.Any(field => field.Name == item.Key)))
            throw new ArgumentException("Unknown command parameter. Tokens and arbitrary method parameters cannot be supplied.");
        foreach (var field in Parameters)
        {
            if (!copy.ContainsKey(field.Name))
            { if (field.Optional) continue; throw new ArgumentException($"{field.Name} is required."); }
            field.Validate(copy[field.Name]);
        }
        if (copy.ContainsKey("url") && copy["applicationName"]?.GetValue<string>() != "webBrowser")
            throw new ArgumentException("A URL is permitted only for webBrowser.");
        return copy;
    }
}

public static partial class SamsungIpRemoteCommands
{
    private static IpRemoteParameter Choice(string name, string choices) => new(name, IpRemoteParameterKind.Choice, Array.AsReadOnly(choices.Split('|')));
    private static IpRemoteParameter Number(string name, int minimum = 0, int maximum = 100) => new(name, IpRemoteParameterKind.Integer, [], minimum, maximum);
    private static IReadOnlyList<IpRemoteParameter> Fields(params IpRemoteParameter[] fields) => Array.AsReadOnly(fields);
    private static IReadOnlyList<IpRemoteParameter> DeviceFields() => Fields(new("deviceId", IpRemoteParameterKind.DeviceId, []), new("deviceName", IpRemoteParameterKind.Text, []));
    private static SamsungIpRemoteCommand Picture(string control, string name, int minimum = 0, int maximum = 100, bool managed = false) =>
        new(control + "Control", name, "Picture", Fields(Number(control, minimum, maximum)), CanQuery: true, ReadbackField: control,
            ReadbackMethod: "getVideoStates", ManagedPictureControl: managed ? control : null,
            Notes: managed ? "Use the verified picture workflow, including its range limits and recovery."
                : "Experimental protocol field. Verify its actual on-screen meaning and range; do not assume modern menu labels match.");

    public static IReadOnlyList<SamsungIpRemoteCommand> All { get; } = Array.AsReadOnly(new[]
    {
        new SamsungIpRemoteCommand("getTVStates", "TV state", "Status", [], CanQuery: true),
        new SamsungIpRemoteCommand("getVideoStates", "Video state", "Status", [], CanQuery: true),
        Picture("contrast", "Contrast", managed: true), Picture("color", "Color", managed: true), Picture("sharpness", "Sharpness", managed: true),
        Picture("brightness", "Shadow Detail (brightness protocol field)", -5, 5), Picture("tint", "Tint (signed)", -15, 15),
        new SamsungIpRemoteCommand("pictureModeControl", "Picture mode", "Picture", Fields(Choice("pictureMode", "Dynamic|Standard|Movie|Natural|HDR+|FilmmakerMode")), ReadbackField: "pictureMode", ReadbackMethod: "getTVStates", Notes: "Changes the picture context and may recall different saved settings. Refresh and update profile annotations afterward."),
        new SamsungIpRemoteCommand("pictureSizeControl", "Picture size", "Picture", Fields(Choice("pictureSize", "16:9|4:3")), ReadbackField: "pictureSize", ReadbackMethod: "getTVStates"),
        new SamsungIpRemoteCommand("directVolumeControl", "Volume", "Sound", Fields(Number("volume")), ReadbackField: "volume", ReadbackMethod: "getTVStates", Notes: "Start with a small change. External audio equipment may not follow the reported volume."),
        new SamsungIpRemoteCommand("volumeUpDnControl", "Volume up/down", "Sound", Fields(Choice("control", "volumeUp|volumeDn"))),
        new SamsungIpRemoteCommand("muteControl", "Mute", "Sound", Fields(Choice("mute", "muteOff|muteOn")), ReadbackField: "mute", ReadbackMethod: "getTVStates"),
        new SamsungIpRemoteCommand("soundModeControl", "Sound mode", "Sound", Fields(Choice("soundMode", "Standard|Amplify|Optimized|ExternalStandard")), ReadbackField: "soundMode", ReadbackMethod: "getTVStates"),
        new SamsungIpRemoteCommand("speakerSelectControl", "Speaker output", "Sound", Fields(Choice("speakerSelect", "Internal|External|AudioOut/Optical")), ReadbackField: "speakerSelect", ReadbackMethod: "getTVStates"),
        new SamsungIpRemoteCommand("externalSpeakerControl", "External speaker discovery/selection", "Sound", DeviceFields(), CanQuery: true),
        new SamsungIpRemoteCommand("inputSourceControl", "Input source", "Sources", Fields(Choice("inputSource", "TV|HDMI1|HDMI2|HDMI3|HDMI4|AV1|COMPONENT1|USB|RVU")), ReadbackField: "inputSource", ReadbackMethod: "getTVStates", Notes: "Changes the input and may recall other settings. Update profile annotations and refresh before picture adjustments."),
        new SamsungIpRemoteCommand("USBSourceControl", "USB discovery/selection", "Sources", DeviceFields(), CanQuery: true),
        new SamsungIpRemoteCommand("RVUSourceControl", "RVU discovery/selection", "Sources", DeviceFields(), CanQuery: true),
        new SamsungIpRemoteCommand("directChannelControl", "Channel selection", "Channels", Fields(Choice("atvDtv", "atv|dtv"), Choice("airCable", "air|cable"), new("channelNum", IpRemoteParameterKind.Channel, [])), ReadbackField: "channelNum", ReadbackMethod: "getTVStates"),
        new SamsungIpRemoteCommand("channelUpDnControl", "Channel up/down", "Channels", Fields(Choice("control", "channelUp|channelDn"))),
        new SamsungIpRemoteCommand("remoteKeyControl", "IP remote key", "Remote", Fields(Choice("remoteKey", "cursorUp|cursorDn|cursorLeft|cursorRight|menu|firstScreen|enter|return|exit|fastforward|rewind|play|stop|pause|power|number0|number1|number2|number3|number4|number5|number6|number7|number8|number9|caption|dash|red|green|yellow|blue|ambient")), SkipReadback: true, Notes: "One explicit IP key only. These actions can alter menus or power; the older WebSocket menu-position indicator is not updated."),
        new SamsungIpRemoteCommand("directAccessControl", "Open application", "Apps", Fields(Choice("applicationName", "webBrowser|netflix|amazon|pandora|vudu|VUDU|youTube|hulu"), new("url", IpRemoteParameterKind.Url, [], Optional: true)), SkipReadback: true),
        new SamsungIpRemoteCommand("artModeControl", "Art mode", "Art / Power", Fields(Choice("artMode", "artModeOn|artModeOff")), ReadbackField: "artMode", ReadbackMethod: "getTVStates", Notes: "Model-specific (for example, Frame displays). An unsupported reply is not permission to try other methods."),
        new SamsungIpRemoteCommand("powerControl", "Power / reboot", "Art / Power", Fields(Choice("power", "powerOff|powerOn|reboot")), SkipReadback: true, Notes: "Can turn off or restart the display. HTTPS powerOn requires a reachable endpoint; this does not send Wake-on-LAN. No automatic read, retry, or rollback follows.")
    }.Concat(Advanced()).Select(WithQuerySupport).ToArray());
    public static SamsungIpRemoteCommand Get(string method) => All.FirstOrDefault(item => item.Method == method)
        ?? throw new ArgumentException("That method is not in the documented IP Remote command catalog.");
}
