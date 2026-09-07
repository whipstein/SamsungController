using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Core.Tests;

public sealed class SamsungIpRemoteCommandTests
{
    private static readonly SamsungIpRemoteOptions Options = new() { Host = "192.0.2.10" };
    private const string Token = "private-command-test-token";
    public static TheoryData<string, string> Commands => new()
    {
        { "getTVStates", "{}" }, { "getVideoStates", "{}" },
        { "getDeviceInformation", "{}" },
        { "backlightControl", "{\"backlight\":25}" },
        { "pictureCalibrationModeControl", "{\"pictureCalibrationMode\":\"On\"}" },
        { "digitalCleanViewControl", "{\"digitalCleanView\":\"Auto\"}" },
        { "autoMotionPlusControl", "{\"autoMotionPlus\":\"Custom\"}" },
        { "AMP.blurReductionControl", "{\"AMP.blurReduction\":1}" },
        { "AMP.judderReductionControl", "{\"AMP.judderReduction\":2}" },
        { "AMP.LEDClearMotionControl", "{\"AMP.LEDClearMotion\":\"Off\"}" },
        { "localDimmingControl", "{\"localDimming\":\"Standard\"}" },
        { "filmModeControl", "{\"filmMode\":\"Auto2\"}" },
        { "contrastEnhancerControl", "{\"contrastEnhancer\":\"Low\"}" },
        { "colorToneControl", "{\"colorTone\":\"Warm2\"}" },
        { "WB2PointControl", "{\"R-Gain\":-1,\"B-Offset\":2}" },
        { "WB20PointModeControl", "{\"WB20PointMode\":\"On\"}" },
        { "WB20P.IntervalControl", "{\"WB20P.Interval\":\"35%\"}" },
        { "WB20P.RedControl", "{\"WB20P.Red\":-50}" },
        { "WB20P.GreenControl", "{\"WB20P.Green\":50}" },
        { "WB20P.BlueControl", "{\"WB20P.Blue\":-1}" },
        { "gammaModeControl", "{\"gammaMode\":\"2.20\"}" },
        { "gamma.BT1886Control", "{\"gamma.BT1886\":-3}" },
        { "gamma.ST2084Control", "{\"gamma.ST2084\":3}" },
        { "gamma.HLGControl", "{\"gamma.HLG\":1}" },
        { "RGBOnlyModeControl", "{\"RGBOnlyMode\":\"Green\"}" },
        { "colorSpaceControl", "{\"colorSpace\":\"Custom\"}" },
        { "colorSpace.ColorControl", "{\"colorSpace.Color\":\"Magenta\"}" },
        { "colorSpace.ColorAdjustmentPointControl", "{\"colorSpace.ColorAdjustmentPoint\":\"75%\"}" },
        { "colorSpace.RedControl", "{\"colorSpace.Red\":0}" },
        { "colorSpace.GreenControl", "{\"colorSpace.Green\":53}" },
        { "colorSpace.BlueControl", "{\"colorSpace.Blue\":100}" },
        { "HDRToneMappingControl", "{\"HDRToneMapping\":\"Static\"}" },
        { "colorSpaceGamutControl", "{\"colorSpaceGamut\":\"DCI-P3\"}" },
        { "peakBrightnessControl", "{\"peakBrightness\":\"High\"}" },
        { "colorBoosterControl", "{\"colorBooster\":\"Low\"}" },
        { "autoHDRRemasteringControl", "{\"autoHDRRemastering\":\"On\"}" },
        { "brightnessOptimizationControl", "{\"brightnessOptimization\":\"Off\"}" },
        { "energySavingSolutionControl", "{\"energySavingSolution\":\"Off\"}" },
        { "gameModeControl", "{\"gameMode\":\"Auto\"}" },
        { "applyPictureSettingsControl", "{\"applyPictureSettings\":\"CurrentSource\"}" },
        { "motionLightingControl", "{\"motionLighting\":\"Off\"}" },
        { "autoPowerSavingControl", "{\"autoPowerSaving\":\"Off\"}" },
        { "autoPowerOffControl", "{\"autoPowerOff\":\"Off\"}" },
        { "pixelShiftMenuControl", "{\"pixelShiftMenu\":\"On\"}" },
        { "displayRotatorControl", "{\"orientation\":\"landscape\"}" },
        { "firstScreenAppControl", "{\"applicationName\":\"Plex\"}" },
        { "multiviewControl", "{\"multiviewMode\":\"example-returned-mode\"}" },
        { "contrastControl", "{\"contrast\":45}" }, { "colorControl", "{\"color\":25}" }, { "sharpnessControl", "{\"sharpness\":0}" },
        { "brightnessControl", "{\"brightness\":-1}" }, { "tintControl", "{\"tint\":1}" },
        { "pictureModeControl", "{\"pictureMode\":\"FilmmakerMode\"}" }, { "pictureSizeControl", "{\"pictureSize\":\"16:9\"}" },
        { "directVolumeControl", "{\"volume\":10}" }, { "volumeUpDnControl", "{\"control\":\"volumeUp\"}" },
        { "muteControl", "{\"mute\":\"muteOn\"}" }, { "soundModeControl", "{\"soundMode\":\"Standard\"}" },
        { "speakerSelectControl", "{\"speakerSelect\":\"Internal\"}" },
        { "externalSpeakerControl", "{\"deviceId\":1,\"deviceName\":\"Example speaker\"}" },
        { "inputSourceControl", "{\"inputSource\":\"HDMI1\"}" },
        { "USBSourceControl", "{\"deviceId\":\"usb1\",\"deviceName\":\"Example USB\"}" },
        { "RVUSourceControl", "{\"deviceId\":\"rvu1\",\"deviceName\":\"Example RVU\"}" },
        { "directChannelControl", "{\"atvDtv\":\"dtv\",\"airCable\":\"air\",\"channelNum\":\"12\"}" },
        { "channelUpDnControl", "{\"control\":\"channelDn\"}" }, { "remoteKeyControl", "{\"remoteKey\":\"cursorUp\"}" },
        { "directAccessControl", "{\"applicationName\":\"webBrowser\",\"url\":\"https://example.com/\"}" },
        { "artModeControl", "{\"artMode\":\"artModeOff\"}" }, { "powerControl", "{\"power\":\"powerOff\"}" }
    };
    [Theory, MemberData(nameof(Commands))]
    public async Task DocumentedMethodsUseExactTypedParametersAndSeparateRedactedCredentials(string method, string json)
    {
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens();
        tokens.Values[Options.Endpoint.AbsoluteUri] = Token;
        var handler = new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject { ["echo"] = Token })));
        using var http = new HttpClient(handler);
        var parameters = JsonNode.Parse(json)!.AsObject();
        var exchange = await new SamsungIpRemoteClient(tokens, http).ExecuteCommandAsync(Options, method, parameters, method.StartsWith("get", StringComparison.Ordinal));
        Assert.True(exchange.IsSuccess, exchange.Message);
        Assert.Equal(method, Assert.Single(handler.Requests)["method"]!.GetValue<string>());
        var sent = handler.Requests[0]["params"]!.AsObject();
        Assert.Equal(Token, sent["AccessToken"]!.GetValue<string>());
        sent.Remove("AccessToken");
        Assert.True(JsonNode.DeepEquals(parameters, sent));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
    }
    [Fact]
    public void CatalogHasEveryIndependentlySpecifiedFamilyAndNoDuplicateMethods()
    {
        Assert.Equal(69, SamsungIpRemoteCommands.All.Count);
        Assert.Equal(69, SamsungIpRemoteCommands.All.Select(item => item.Method).Distinct().Count());
        Assert.Equal(Commands.Select(row => (string)row[0]).Order(), SamsungIpRemoteCommands.All.Select(item => item.Method).Order());
    }
    [Theory]
    [InlineData("factoryReset", "{}")]
    [InlineData("whiteBalanceControl", "{}")]
    [InlineData("remoteKeyControl", "{\"remoteKey\":\"serviceMenu\"}")]
    [InlineData("powerControl", "{\"power\":\"reset\"}")]
    [InlineData("muteControl", "{\"mute\":true}")]
    [InlineData("muteControl", "{\"mute\":\"muteOn\",\"AccessToken\":\"override\"}")]
    [InlineData("directVolumeControl", "{\"volume\":101}")]
    [InlineData("directVolumeControl", "{\"volume\":\"10\"}")]
    [InlineData("brightnessControl", "{\"brightness\":-6}")]
    [InlineData("brightnessControl", "{\"brightness\":6}")]
    [InlineData("directChannelControl", "{\"channelNum\":\"12\"}")]
    [InlineData("directChannelControl", "{\"atvDtv\":\"dtv\",\"airCable\":\"air\",\"channelNum\":\"1000\"}")]
    [InlineData("directAccessControl", "{\"applicationName\":\"netflix\",\"url\":\"https://example.com\"}")]
    [InlineData("directAccessControl", "{\"applicationName\":\"webBrowser\",\"url\":\"https://user:pass@example.com\"}")]
    [InlineData("directAccessControl", "{\"applicationName\":\"webBrowser\",\"url\":\"file:///etc/passwd\"}")]
    public async Task InvalidCommandsNeverReachHttp(string method, string json)
    {
        var handler = new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject())));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(new SamsungIpRemoteClientTests.MemoryTokens(), http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteCommandAsync(Options, method, JsonNode.Parse(json)!.AsObject()));
        Assert.Empty(handler.Requests);
    }
    [Theory]
    [InlineData("USBSourceControl")]
    [InlineData("RVUSourceControl")]
    [InlineData("externalSpeakerControl")]
    public async Task DeviceQueriesAllowSanitizedArraysWithoutSendingSelectionParameters(string method)
    {
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens();
        tokens.Values[Options.Endpoint.AbsoluteUri] = Token;
        var handler = new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(Reply(request,
            new JsonArray(new JsonObject { ["deviceId"] = 1, ["deviceName"] = "Example", ["echo"] = Token }))));
        using var http = new HttpClient(handler);
        var exchange = await new SamsungIpRemoteClient(tokens, http).ExecuteCommandAsync(Options, method, new(), query: true);
        Assert.True(exchange.IsSuccess);
        Assert.Single(handler.Requests[0]["params"]!.AsObject());
        Assert.Single(Assert.IsType<JsonArray>(exchange.Payload));
        Assert.Null(exchange.Result);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
    }
    private static HttpResponseMessage Reply(JsonObject request, JsonNode result) => new(HttpStatusCode.OK)
    { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.ToJsonString(), ["result"] = result }.ToJsonString()) };
}
