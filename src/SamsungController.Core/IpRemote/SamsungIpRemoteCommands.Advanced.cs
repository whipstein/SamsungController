namespace SamsungController.Core.IpRemote;

public sealed record IpRemoteCommandRequirement(string Method, string Field, IReadOnlyList<string> AllowedValues);

public static partial class SamsungIpRemoteCommands
{
    // Wire spelling: TheFab21/ha-samsungtv-smart, 8c7000522b4045b42ff26d129d8d5fe9daf280cb,
    // notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md. Ranges/older choices: Samsung 2020 IP list.
    // These sources define protocol candidates, NOT compatibility with the connected display.
    private static SamsungIpRemoteCommand Expert(string field, string name, string group, IpRemoteParameter parameter, string notes = "") =>
        new(field + "Control", name, group, Fields(parameter), CanQuery: true, ReadbackField: field, ReadbackMethod: field + "Control",
            Notes: "Experimental; display/input/mode support must be tested. " + notes);
    private static SamsungIpRemoteCommand EnumSetting(string field, string name, string group, string choices, string notes = "") =>
        Expert(field, name, group, Choice(field, choices), notes);
    private static SamsungIpRemoteCommand Level(string field, string name, string group, int min, int max, string notes = "") =>
        Expert(field, name, group, Number(field, min, max), notes);

    private static IEnumerable<SamsungIpRemoteCommand> Advanced()
    {
        yield return new("getDeviceInformation", "Display identity / firmware", "Status", [], CanQuery: true,
            Notes: "Reads modelID, FWVersion and possibly serialNumber. Missing fields are not filled in or applied to your profile.");
        yield return Level("backlight", "Brightness / Backlight", "Picture", 0, 50,
            "Samsung's 2020 list calls this modern Brightness. Separate from Shadow Detail and Art brightness; verify the on-screen mapping.");
        yield return EnumSetting("pictureCalibrationMode", "Picture calibration mode (deprecated)", "Picture", "Off|On", "2020 list: 2019-only, deprecated. This is not Smart Calibration or a reset.");
        yield return EnumSetting("digitalCleanView", "Noise reduction / Digital Clean View", "Motion / Processing", "Off|Auto|Low|Standard", "Low is deprecated. Standard is a reported getter value; setter availability varies.");
        yield return EnumSetting("autoMotionPlus", "Picture Clarity / Auto Motion Plus", "Motion / Processing", "Off|Auto|Custom");
        yield return Level("AMP.blurReduction", "Blur reduction", "Motion / Processing", 0, 10, "Requires Custom motion mode and a supported panel.");
        yield return Level("AMP.judderReduction", "Judder reduction", "Motion / Processing", 0, 10, "Requires Custom motion mode.");
        yield return EnumSetting("AMP.LEDClearMotion", "LED Clear Motion", "Motion / Processing", "Off|On", "Requires Custom motion mode; panel dependent.");
        yield return EnumSetting("localDimming", "Local dimming", "Motion / Processing", "Low|Standard|High", "Panel dependent; not an OLED brightness control.");
        yield return EnumSetting("filmMode", "Film mode", "Motion / Processing", "Off|Auto1|Auto2", "2020 TVs may display Auto for both Auto1 and Auto2. Content dependent.");
        yield return EnumSetting("contrastEnhancer", "Contrast enhancer", "Motion / Processing", "Off|Low|High");
        yield return EnumSetting("colorTone", "Color tone", "White balance · 2 point", "Cool|Standard|Warm1|Warm2");
        yield return new("WB2PointControl", "2-point white balance gains / offsets", "White balance · 2 point",
            Fields(new[] { "R-Gain", "G-Gain", "B-Gain", "R-Offset", "G-Offset", "B-Offset" }
                .Select(field => Number(field, -50, 50) with { Optional = true }).ToArray()),
            CanQuery: true, ReadbackMethod: "WB2PointControl",
            Notes: "Experimental. Enter one or more channels; blank channels are omitted, not zeroed. Six flat, hyphenated integer parameters, not a nested WB2Point object.");
        yield return EnumSetting("WB20PointMode", "20-point white balance enabled", "White balance · 20 point", "Off|On");
        yield return EnumSetting("WB20P.Interval", "20-point interval", "White balance · 20 point",
            string.Join('|', Enumerable.Range(1, 20).Select(index => $"{index * 5}%")), "Requires 20-point mode On. Changes which interval the RGB commands address.");
        foreach (var color in new[] { "Red", "Green", "Blue" })
            yield return Level("WB20P." + color, "20-point " + color, "White balance · 20 point", -50, 50,
                "Only changes the currently selected interval. The mode and interval are read and checked before/after sending; no interval is selected automatically.");
        yield return EnumSetting("gammaMode", "Gamma mode", "Gamma / HDR", "BT.1886|ST.2084|HLG|2.2|2.20", "Availability depends on signal. Samsung's 2023 list spells 2.2; the 2020 list spells 2.20.");
        yield return Level("gamma.BT1886", "BT.1886 adjustment", "Gamma / HDR", -3, 3, "Requires BT.1886 gamma.");
        yield return Level("gamma.ST2084", "ST.2084 adjustment", "Gamma / HDR", -3, 3, "Requires ST.2084 gamma and an appropriate signal.");
        yield return Level("gamma.HLG", "HLG adjustment", "Gamma / HDR", -3, 3, "Requires HLG gamma and an appropriate signal.");
        yield return EnumSetting("RGBOnlyMode", "RGB only mode", "Color space", "Off|Red|Green|Blue");
        yield return EnumSetting("colorSpace", "Color space", "Color space", "Auto|Native|Custom");
        yield return EnumSetting("colorSpace.Color", "Custom color selector", "Color space", "Red|Green|Blue|Yellow|Cyan|Magenta", "Requires Custom color space. Changes which color the RGB commands address.");
        yield return EnumSetting("colorSpace.ColorAdjustmentPoint", "Color adjustment point (deprecated)", "Color space", "50%|75%|100%", "Deprecated. 2020 list specifies percentages, not color names. Wire field comes from the firmware notes; those notes' color-name candidates failed.");
        foreach (var color in new[] { "Red", "Green", "Blue" })
            yield return Level("colorSpace." + color, "Custom color " + color, "Color space", 0, 100,
                "Only changes the currently selected custom color. Mode and color selector must remain unchanged. No selector is changed automatically.");

        // Additional consumer-TV fields found in the 2025 firmware notes. Candidate enums are
        // intentionally bounded and explicitly labeled when the reference has no successful setter.
        yield return EnumSetting("HDRToneMapping", "HDR tone mapping", "Gamma / HDR", "0|1|Static|Active", "Candidate strings from firmware notes; only getter 0 observed there, setters failed in SDR. Do not infer a 0/1-to-label mapping.");
        yield return EnumSetting("colorSpaceGamut", "Color gamut", "Color space", "BT.709|DCI-P3|BT.2020|Auto", "Candidate strings; reference getter reported BT.709, setters failed in that state.");
        yield return EnumSetting("peakBrightness", "Peak brightness", "Gamma / HDR", "Off|Medium|High", "Candidate strings; reference setters failed. Model/signal dependent.");
        yield return EnumSetting("colorBooster", "Color booster", "Motion / Processing", "Off|Low|High", "Candidate strings; reference setters failed. Confirm actual on-screen mapping.");
        yield return EnumSetting("autoHDRRemastering", "Auto HDR remastering", "Gamma / HDR", "Off|On");
        yield return EnumSetting("brightnessOptimization", "Brightness optimization", "Power / Eco", "Off|On");
        yield return EnumSetting("energySavingSolution", "Energy saving solution", "Power / Eco", "Off|Low|Medium|High|Auto", "Only Off was accepted in the reference; other values are candidates.");
        yield return EnumSetting("gameMode", "Game mode", "Motion / Processing", "Off|On|Auto", "Candidate strings; source dependent. May recall other picture settings.");
        yield return EnumSetting("applyPictureSettings", "Apply picture settings scope", "Picture", "CurrentSource|AllSources", "AllSources may affect other inputs. Review the scope before sending.");
        yield return EnumSetting("motionLighting", "Motion lighting", "Power / Eco", "Off|On");
        yield return EnumSetting("autoPowerSaving", "Auto power saving", "Power / Eco", "Off|On");
        yield return EnumSetting("autoPowerOff", "Auto power off", "Power / Eco", "Off|On", "Changes future power behavior. Only Off was accepted in the reference state; On is a candidate.");
        yield return EnumSetting("pixelShiftMenu", "Pixel shift", "Panel", "Off|On", "Panel protection setting; candidate values. Turning protection off can increase retention risk.");
        yield return EnumSetting("orientation", "Display orientation", "Panel", "landscape|portrait", "May physically rotate a supported display. Keep the surrounding area clear.")
            with
        { Method = "displayRotatorControl", ReadbackMethod = "displayRotatorControl" };
        yield return new("firstScreenAppControl", "Home apps list / launch", "Apps", Fields(new IpRemoteParameter("applicationName", IpRemoteParameterKind.Text, [])), CanQuery: true, SkipReadback: true,
            Notes: "Read/list installed apps first. Enter the exact application alias from the display; the method is known, but accepted aliases vary. No app is launched by Read/list.");
        yield return new("multiviewControl", "Multi View modes", "Picture", Fields(new IpRemoteParameter("multiviewMode", IpRemoteParameterKind.Text, [])), CanQuery: true, SkipReadback: true,
            Notes: "Read/list first; mode strings are display/state dependent. Use an exact supported mode from the display, not a guessed On/Off. An empty list does not establish a valid write value.");
    }

    private static SamsungIpRemoteCommand WithQuerySupport(SamsungIpRemoteCommand command) => command.Method switch
    {
        "remoteKeyControl" => command with { Parameters = Fields(Choice("remoteKey", string.Join('|', command.Parameters[0].Choices.Concat(new[] { "channelUp", "channelDn", "volumeUp", "volumeDn", "multiview" })))) },
        "soundModeControl" => command with { CanQuery = true, Parameters = Fields(Choice("soundMode", "Standard|Amplify|Optimized|ExternalStandard|Movie|Music")) },
        "pictureModeControl" => command with { CanQuery = true, Parameters = Fields(Choice("pictureMode", "Dynamic|Standard|Movie|Natural|HDR+|FilmmakerMode|CAL-NIGHT|CAL-DAY")) },
        "inputSourceControl" => command with { CanQuery = true, Parameters = Fields(Choice("inputSource", "TV|HDMI1|HDMI2|HDMI3|HDMI4|AV1|AV2|COMPONENT1|USB|RVU")) },
        "pictureSizeControl" or "directVolumeControl" or "muteControl" or "speakerSelectControl" or "directChannelControl" or "directAccessControl" or "powerControl" => command with { CanQuery = true },
        "artModeControl" => command with { CanQuery = true, ReadbackMethod = "artModeControl" },
        _ => command
    };

    internal static IReadOnlyList<IpRemoteCommandRequirement> RequirementsFor(string method)
    {
        static IpRemoteCommandRequirement Need(string field, params string[] values) => new(field + "Control", field, Array.AsReadOnly(values));
        if (method.StartsWith("AMP.", StringComparison.Ordinal)) return Array.AsReadOnly(new[] { Need("autoMotionPlus", "Custom") });
        if (method.StartsWith("WB20P.", StringComparison.Ordinal))
            return Array.AsReadOnly(method == "WB20P.IntervalControl" ? new[] { Need("WB20PointMode", "On") }
                : new[] { Need("WB20PointMode", "On"), Need("WB20P.Interval", Enumerable.Range(1, 20).Select(i => $"{i * 5}%").ToArray()) });
        if (method.StartsWith("colorSpace.", StringComparison.Ordinal))
            return Array.AsReadOnly(method is "colorSpace.ColorControl" or "colorSpace.ColorAdjustmentPointControl" ? new[] { Need("colorSpace", "Custom") }
                : new[] { Need("colorSpace", "Custom"), Need("colorSpace.Color", "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta") });
        return method switch
        {
            "gamma.BT1886Control" => Array.AsReadOnly(new[] { Need("gammaMode", "BT.1886") }),
            "gamma.ST2084Control" => Array.AsReadOnly(new[] { Need("gammaMode", "ST.2084") }),
            "gamma.HLGControl" => Array.AsReadOnly(new[] { Need("gammaMode", "HLG") }),
            _ => Array.Empty<IpRemoteCommandRequirement>()
        };
    }
}
