using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemotePicturePresetTests
{
    private static IpRemoteWorkspaceReading Reading() => new(Guid.NewGuid(), ContrastFixture.Profile, DateTimeOffset.UtcNow,
        "HDMI4", "FilmmakerMode", new JsonObject { ["contrast"] = 45, ["color"] = 25, ["sharpness"] = 0 });
    private static string Export(IpRemoteWorkspaceReading reading) => IpRemotePicturePresets.Export(reading,
        new Dictionary<string, int> { ["contrast"] = 44, ["color"] = 24, ["sharpness"] = 1 }, "Example preset");

    [Fact]
    public void RoundTripContainsOnlyDesiredValuesAndContextNotEndpointOrVerification()
    {
        var read = Reading();
        var json = Export(read);
        var preset = IpRemotePicturePresets.Import(json, read);
        Assert.Equal("Example preset", preset.Name);
        Assert.Equal(44, preset.Values["contrast"]);
        Assert.Equal(24, preset.Values["color"]);
        Assert.Equal(1, preset.Values["sharpness"]);
        Assert.DoesNotContain(read.Profile.Connection.Host, json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verified", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readAt", json, StringComparison.Ordinal);
        Assert.Equal(45, read.Value("contrast"));
        // Same physical display can have another network address; context must still match.
        var moved = read with { Profile = read.Profile with { Connection = read.Profile.Connection with { Host = "192.0.2.99" } } };
        Assert.Equal(preset.Context, IpRemotePicturePresets.Import(json, moved).Context);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("firmware")]
    [InlineData("inputSource")]
    [InlineData("pictureMode")]
    [InlineData("signal")]
    [InlineData("reportedInput")]
    [InlineData("reportedPictureMode")]
    public void EachContextDimensionMustMatch(string field)
    {
        var read = Reading();
        var json = JsonNode.Parse(Export(read))!;
        json["context"]![field] = "Different";
        Assert.Throws<InvalidOperationException>(() => IpRemotePicturePresets.Import(json.ToJsonString(), read));
    }

    [Theory]
    [InlineData("format-missing")]
    [InlineData("format-wrong")]
    [InlineData("unknown-property")]
    [InlineData("credential")]
    [InlineData("context-null")]
    [InlineData("context-incomplete")]
    [InlineData("values-null")]
    [InlineData("values-empty")]
    [InlineData("unknown-control")]
    [InlineData("negative")]
    [InlineData("too-high")]
    [InlineData("fraction")]
    [InlineData("string-value")]
    [InlineData("name-empty")]
    [InlineData("name-long")]
    public void InvalidPresetsAreRejectedAsAWhole(string problem)
    {
        var read = Reading();
        var json = JsonNode.Parse(Export(read))!.AsObject();
        switch (problem)
        {
            case "format-missing": json.Remove("format"); break;
            case "format-wrong": json["format"] = "calibration"; break;
            case "unknown-property": json["verified"] = true; break;
            case "credential": json["context"]!["accessToken"] = "must-not-be-imported"; break;
            case "context-null": json["context"] = null; break;
            case "context-incomplete": json["context"]!.AsObject().Remove("signal"); break;
            case "values-null": json["values"] = null; break;
            case "values-empty": json["values"] = new JsonObject(); break;
            case "unknown-control": json["values"]!["brightness"] = 40; break;
            case "negative": json["values"]!["color"] = -1; break;
            case "too-high": json["values"]!["color"] = 101; break;
            case "fraction": json["values"]!["color"] = 0.5; break;
            case "string-value": json["values"]!["color"] = "24"; break;
            case "name-empty": json["name"] = " "; break;
            case "name-long": json["name"] = new string('a', 81); break;
        }
        var error = Record.Exception(() => IpRemotePicturePresets.Import(json.ToJsonString(), read));
        Assert.True(error is InvalidOperationException or JsonException, error?.ToString());
    }

    [Theory]
    [InlineData("\"color\": 24", "\"color\": 24, \"color\": 23")]
    [InlineData("\"name\": \"Example preset\"", "\"name\": \"Example preset\", \"name\": \"Ambiguous\"")]
    public void DuplicatePropertiesAreNotSilentlyOverwritten(string from, string to)
    {
        var read = Reading();
        Assert.Throws<JsonException>(() => IpRemotePicturePresets.Import(Export(read).Replace(from, to, StringComparison.Ordinal), read));
    }

    [Fact]
    public void OversizedAndUnreportedControlsAreRejected()
    {
        var read = Reading();
        Assert.Throws<InvalidOperationException>(() => IpRemotePicturePresets.Import(new string(' ', IpRemotePicturePresets.MaximumFileBytes + 1), read));
        var json = Export(read);
        read.VideoBaseline.Remove("color");
        Assert.Throws<InvalidOperationException>(() => IpRemotePicturePresets.Import(json, read));
        Assert.Throws<InvalidOperationException>(() => Export(read));
    }
}
