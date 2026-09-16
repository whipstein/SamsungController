using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SamsungController.Web.Components.Shared;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuSavedStateFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewAndLegacyStatesRecallOnCurrentInputWithoutSwitchingIt(bool legacy)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        var id = await fixture.Service.SaveMenuStateAsync("Evening calibration");
        var path = fixture.Service.SavedStatePath(id);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Null(file["Context"]!["Input"]);
        if (legacy)
        {
            file["Context"]!["Input"] = "HDMI4";
            await File.WriteAllTextAsync(path, file.ToJsonString());
            var legacyPath = Path.Combine(fixture.Service.SavedStatesDirectory, id.ToString("N") + ".json");
            File.Move(path, legacyPath);
            await fixture.RestartAsync();
            Assert.False(File.Exists(legacyPath));
            Assert.Equal(path, fixture.Service.SavedStatePath(id));
            Assert.Equal(file.ToJsonString(), await File.ReadAllTextAsync(path)); // Rename only, no content rewrite.
        }
        fixture.Display.Input = "HDMI1";
        fixture.Display.Contrast = 40;
        await fixture.Service.ConnectMenuAsync(false);
        Assert.Null(fixture.Service.RecallMenuStateDisabledReason(fixture.Service.GetSnapshot().SavedStates.Single()));
        fixture.Display.Requests.Clear();
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal("HDMI1", fixture.Display.Input);
        Assert.DoesNotContain(fixture.Writes, request => request["method"]!.ToString() == "inputSourceControl");
        Assert.Equal("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
    }

    [Fact]
    public async Task ReadableFilesMayBeRenamedInFinderAndSelectedForDeletion()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        var id = await fixture.Service.SaveMenuStateAsync("Evening calibration");
        var path = fixture.Service.SavedStatePath(id);
        Assert.StartsWith("Evening calibration - ", Path.GetFileName(path));
        var renamed = Path.Combine(fixture.Service.SavedStatesDirectory, "My preferred calibration.json");
        File.Move(path, renamed);
        await fixture.RestartAsync();
        Assert.Equal(renamed, fixture.Service.SavedStatePath(id));
        fixture.Display.Requests.Clear();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(SavedSettingsStates));
        await page.StartAsync(new() { [nameof(SavedSettingsStates.Snapshot)] = fixture.Service.GetSnapshot() });
        await page.SelectAsync("Saved state", id.ToString());
        await page.AssertTextAsync("My preferred calibration.json");
        await page.AssertDisabledAsync(SavedStateFileReveal.Label, false);
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.DeleteMenuStateAsync(id, true);
        Assert.False(File.Exists(renamed));
    }

    [Theory]
    [InlineData("../../bad:name?*")]
    [InlineData("CON")]
    [InlineData(".../")]
    [InlineData("日本語設定")]
    public void NamesAreReadableSafeAndCollisionResistant(string name)
    {
        var state = new IpMenuSavedState { Name = name, SavedAt = DateTimeOffset.Parse("2026-09-16T14:30:00Z"),
            Context = new("https://example.invalid:8080", new string('界', 180), "", "", null, "Movie") };
        var file = SamsungIpRemoteService.SavedStateFileName(state);
        Assert.Equal(file, Path.GetFileName(file));
        Assert.DoesNotContain("..", file);
        Assert.DoesNotContain(":", file);
        Assert.Contains("2026-09-16_143000Z", file);
        Assert.True(Encoding.UTF8.GetByteCount(file) < 255);
        Assert.NotEqual(file, SamsungIpRemoteService.SavedStateFileName(state with { Id = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("macos", "/usr/bin/open")]
    [InlineData("windows", "explorer.exe")]
    [InlineData("linux", "xdg-open")]
    public void RevealUsesFixedProgramAndSeparateArgumentsWithoutShell(string platform, string executable)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Saved settings", "My settings; echo test.json"));
        var command = SavedStateFileReveal.CreateStartInfo(path, platform);
        Assert.False(command.UseShellExecute);
        Assert.Equal(executable, command.FileName);
        Assert.Equal(platform == "macos" ? new[] { "-R", path } : platform == "windows" ? ["/select," + path] : [Path.GetDirectoryName(path)!], command.ArgumentList);
    }
}
