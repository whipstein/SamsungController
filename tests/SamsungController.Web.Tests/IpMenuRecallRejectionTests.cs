using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuRecallRejectionTests
{
    [Fact]
    public async Task ExternalSoundModeRejectionDoesNotPreventWhiteBalanceOrColorRecall()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var targets = new Dictionary<string, string> { ["soundModeControl/soundMode"] = "Standard" };
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.Section == "white20" ? "Off" : "Auto";
            targets[grid.ModeMethod + "/" + grid.ModeField] = grid.RequiredMode;
            foreach (var value in grid.Values)
                fixture.GridValues[grid.Section + "/" + value] = new JsonObject(grid.Fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(20))));
            foreach (var control in grid.Row(grid.Values[0])) targets[control.Id] = "10";
        }
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() switch
        {
            "getTVStates" => ContrastDisplay.Reply(request, new JsonObject { ["inputSource"] = fixture.Display.Input, ["pictureMode"] = fixture.Display.Mode,
                ["volume"] = 10, ["mute"] = "muteOff", ["pictureSize"] = "16:9", ["soundMode"] = "ExternalStandard", ["speakerSelect"] = "External" }),
            "soundModeControl" => request["params"]?["soundMode"] is not null ? MenuFixture.Reject(request, -32002)
                : ContrastDisplay.Reply(request, new JsonObject { ["soundMode"] = "ExternalStandard" }),
            _ => null
        });
        await fixture.Service.ConnectMenuAsync(false);
        var id = await SaveTargetsAsync(fixture, targets);
        fixture.Display.Requests.Clear();
        await fixture.Service.RecallMenuStateAsync(id, true);
        var result = fixture.Service.GetSnapshot();
        Assert.Equal("Completed", result.StateRecall!.Status);
        Assert.Equal(targets.Count - 1, result.StateRecall.Confirmed);
        Assert.Equal("soundModeControl/soundMode", Assert.Single(result.StateRecall.Skipped).Key);
        Assert.Contains("ExternalStandard", result.StateRecall.Skipped.Values.Single());
        Assert.Equal("ExternalStandard", result.Menu.Tv["soundMode"]!.ToString());
        Assert.Single(fixture.Writes, request => request["method"]!.ToString() == "soundModeControl");
        Assert.Contains("skipped", result.Menu.ActionWarning);
        foreach (var grid in IpMenuGrids.All)
        {
            Assert.Equal(grid.RequiredMode, fixture.Values[grid.ModeField]!.ToString());
            Assert.All(grid.Row(grid.Values[0]), control => Assert.Equal(10, fixture.Value(control.Id)!.GetValue<int>()));
        }
        Assert.False(result.StateRecall.NeedsReview);
        Assert.False(result.Menu.Update!.NeedsReview);
        await fixture.RestartAsync();
        Assert.Single(fixture.Service.GetSnapshot().StateRecall!.Skipped);
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task RejectedRgbChannelIsNotRetriedAndRemainingChannelsAndRowsContinue(string section)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        var controls = grid.Values.SelectMany(grid.Row).ToArray();
        var targets = controls.ToDictionary(control => control.Id, _ => "10");
        targets[grid.ModeMethod + "/" + grid.ModeField] = grid.RequiredMode;
        var id = await SaveTargetsAsync(fixture, targets);
        foreach (var row in fixture.GridValues.Values) foreach (var field in grid.Fields) row[field] = 20;
        var rejected = grid.Row(grid.Values[0]).ElementAt(1);
        var attempts = new List<string>();
        fixture.Override = (request, _) =>
        {
            var control = controls.FirstOrDefault(control => control.IndexValue == fixture.Values[grid.SelectorField]!.ToString() && request["params"]?[control.Field] is not null);
            if (control is not null) attempts.Add(control.Id);
            return Task.FromResult<HttpResponseMessage?>(control?.Id == rejected.Id ? MenuFixture.Reject(request, -32002) : null);
        };
        await fixture.Service.RecallMenuStateAsync(id, true);
        var result = fixture.Service.GetSnapshot();
        Assert.Equal("Completed", result.StateRecall!.Status);
        Assert.Equal(targets.Count - 1, result.StateRecall.Confirmed);
        Assert.Equal(rejected.Id, Assert.Single(result.StateRecall.Skipped).Key);
        Assert.Equal(controls.Length, attempts.Count);
        Assert.All(attempts.GroupBy(value => value), group => Assert.Single(group));
        foreach (var control in controls) Assert.Equal(control == rejected ? 20 : 10, fixture.Value(control.Id)!.GetValue<int>());
        Assert.False(result.Menu.Update!.NeedsReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCalibrationModeSkipsDependentsOrReportsUnrestoredFinalMode(bool rejectFinalMode)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        var targets = row.ToDictionary(control => control.Id, _ => "11");
        targets[grid.ModeMethod + "/" + grid.ModeField] = rejectFinalMode ? "Off" : "On";
        var id = await SaveTargetsAsync(fixture, targets);
        if (!rejectFinalMode) fixture.Values[grid.ModeField] = "Off";
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]?[grid.ModeField] is not null ? MenuFixture.Reject(request, -32002) : null);
        fixture.Display.Requests.Clear();
        await fixture.Service.RecallMenuStateAsync(id, true);
        var result = fixture.Service.GetSnapshot().StateRecall!;
        Assert.Equal("Completed", result.Status);
        Assert.Equal(rejectFinalMode ? 3 : 0, result.Confirmed);
        Assert.Equal(rejectFinalMode ? 1 : 4, result.Skipped.Count);
        Assert.Contains(grid.ModeMethod + "/" + grid.ModeField, result.Skipped.Keys);
        Assert.Equal(rejectFinalMode ? "On" : "Off", fixture.Values[grid.ModeField]!.ToString());
        Assert.Single(fixture.Writes, request => request["params"]?[grid.ModeField] is not null);
        if (!rejectFinalMode) Assert.DoesNotContain(fixture.Writes, request => grid.Fields.Any(field => request["params"]?[field] is not null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainRejectionOrPowerLossStillStopsLaterWrites(bool powerOff)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        var id = await SaveTargetsAsync(fixture, new() { ["contrastControl/contrast"] = "45", ["colorControl/color"] = "25" });
        fixture.Display.Contrast = 40; fixture.Display.Color = 12;
        fixture.Override = (request, _) =>
        {
            if (request["params"]?["contrast"] is null) return Task.FromResult<HttpResponseMessage?>(null);
            if (powerOff) fixture.Values["power"] = "powerOff";
            else fixture.Display.Contrast = 41; // Rejection with an unexpected write cannot be skipped.
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.RecallMenuStateAsync(id, true));
        Assert.Equal(12, fixture.Display.Color);
        Assert.Single(fixture.Writes);
        Assert.NotEqual("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
        Assert.Equal(!powerOff, fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    private static async Task<Guid> SaveTargetsAsync(MenuFixture fixture, Dictionary<string, string> targets)
    {
        var id = await fixture.Service.SaveMenuStateAsync("Recall regression");
        var saved = fixture.Service.GetSnapshot().SavedStates.Single() with { Values = targets, Missing = new() };
        await File.WriteAllTextAsync(fixture.Service.SavedStatePath(id), JsonSerializer.Serialize(saved));
        return id;
    }
}
