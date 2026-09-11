using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SamsungController.Web.Components.Shared;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuSavedStateTests
{
    private const string Contrast = "contrastControl/contrast";

    [Fact]
    public async Task SavedSettingsSpanEveryTabAndEveryLoadedCalibrationRowWithoutSendingCommands()
    {
        using var fixture = await MenuFixture.CreateAsync();
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.RequiredMode;
            fixture.Values[grid.SelectorField] = grid.Values[0];
            foreach (var value in grid.Values)
                fixture.GridValues[grid.Section + "/" + value] = new JsonObject(grid.Fields.Select(field =>
                    KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(grid.Section == "white20" ? -5 : 50))));
        }
        await fixture.Service.ConnectMenuAsync();
        fixture.Display.Requests.Clear();
        await fixture.Service.SaveMenuStateAsync("Whole display");
        var saved = Assert.Single(fixture.Service.GetSnapshot().SavedStates);
        foreach (var section in IpMenuCatalog.Sections)
            Assert.Contains(saved.Values.Keys, id => IpMenuCatalog.Get(id).Section == section.Id);
        foreach (var grid in IpMenuGrids.All)
            foreach (var control in grid.Values.SelectMany(grid.Row))
                Assert.Equal(grid.Section == "white20" ? "-5" : "50", saved.Values[control.Id]);
        Assert.Equal(IpMenuSavedStates.Controls.Select(control => control.Id).Order(), saved.Values.Keys.Concat(saved.Missing.Keys).Order());
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task SaveAndDeleteAreLocalPrivateAndSurviveRestart()
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Evening settings");
        Assert.Empty(fixture.Display.Requests);
        var state = Assert.Single(fixture.Service.GetSnapshot().SavedStates);
        Assert.Equal("45", state.Values[Contrast]);
        Assert.Contains(IpMenuCatalog.IndexedControls[0].Id, state.Missing.Keys);
        Assert.DoesNotContain(state.Values.Keys, key => IpMenuCatalog.Get(key).ChangesContext || IpMenuCatalog.Get(key).IsSelector);
        var file = Path.Combine(fixture.Service.SavedStatesDirectory, id.ToString("N") + ".json");
        var text = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(ContrastFixture.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Certificate", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        await fixture.RestartAsync();
        Assert.Equal(id, Assert.Single(fixture.Service.GetSnapshot().SavedStates).Id);
        Assert.Empty(fixture.Display.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteMenuStateAsync(id, false));
        Assert.True(File.Exists(file));
        await fixture.Service.DeleteMenuStateAsync(id, true);
        Assert.False(File.Exists(file));
        Assert.Empty(fixture.Service.GetSnapshot().SavedStates);
        await fixture.RestartAsync();
        Assert.Empty(fixture.Service.GetSnapshot().SavedStates);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task DuplicateNamesAndPendingEditsDoNotOverwriteExistingStates()
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.SaveMenuStateAsync("Baseline");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveMenuStateAsync(" baseline "));
        fixture.Service.StageMenuValue(Contrast, "42");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveMenuStateAsync("Unsent"));
        Assert.Equal("45", Assert.Single(fixture.Service.GetSnapshot().SavedStates).Values[Contrast]);
        Assert.Equal(42, fixture.Service.GetSnapshot().Menu.Pending[Contrast].Target.GetValue<int>());
        Assert.Empty(fixture.Display.Requests);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("name\nwith control")]
    public async Task InvalidNamesFailWithoutWritingOrSending(string name)
    {
        using var fixture = await ReadyAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveMenuStateAsync(name));
        Assert.Empty(fixture.Service.GetSnapshot().SavedStates);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task NamesAreNotPathsAndDeletionTargetsOnlyTheSelectedGuid()
    {
        using var fixture = await ReadyAsync();
        var first = await fixture.Service.SaveMenuStateAsync("../outside/state");
        var second = await fixture.Service.SaveMenuStateAsync("Another");
        Assert.Equal(2, Directory.GetFiles(fixture.Service.SavedStatesDirectory, "*.json").Length);
        await fixture.Service.DeleteMenuStateAsync(first, true);
        Assert.Equal(second, Assert.Single(fixture.Service.GetSnapshot().SavedStates).Id);
        Assert.Single(Directory.GetFiles(fixture.Service.SavedStatesDirectory, "*.json"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecallUsesSavedTargetsAndPostReadbackInEitherApplyMode(bool immediate)
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        fixture.Display.Contrast = 40; fixture.Display.Color = 12;
        await fixture.Service.SaveMenuPreferencesAsync(immediate, false);
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(new[] { "contrastControl", "colorControl" }, fixture.Writes.Select(request => request["method"]!.ToString()));
        Assert.Equal("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Display.Batches);
        Assert.DoesNotContain("remoteKeyControl", fixture.Display.Methods);
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("pending")]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("profile")]
    public async Task WrongContextOrUnconfirmedRecallSendsNoWrites(string problem)
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        if (problem == "pending") fixture.Service.StageMenuValue(Contrast, "42");
        if (problem == "input") fixture.Display.Input = "HDMI1";
        if (problem == "mode") fixture.Display.Mode = "Standard";
        if (problem == "profile")
        {
            await fixture.Service.SaveProfileAsync(fixture.Service.GetSnapshot().ActiveProfile! with { Signal = "Different physical signal" });
            await fixture.Service.ConnectMenuAsync(false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RecallMenuStateAsync(id, problem != "unconfirmed"));
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().StateRecall);
    }

    [Fact]
    public async Task MalformedOrTamperedFilesCannotSendAnyRequestsAndDoNotBreakTheLibrary()
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Good");
        var file = Path.Combine(fixture.Service.SavedStatesDirectory, id.ToString("N") + ".json");
        var original = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, original.Replace("\"45\"", "\"999\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RecallMenuStateAsync(id, true));
        Assert.Empty(fixture.Display.Requests);
        await fixture.RestartAsync();
        Assert.Empty(fixture.Service.GetSnapshot().SavedStates);
        Assert.NotNull(fixture.Service.GetSnapshot().SavedStatesWarning);
        Assert.True(File.Exists(file));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task PrerequisiteModesAreRestoredBeforeTheirDependentSliders()
    {
        using var fixture = await ReadyAsync();
        fixture.Values["gammaMode"] = "BT.1886"; fixture.Values["gamma.BT1886"] = -2;
        await fixture.Service.RefreshMenuSectionAsync("expert");
        var id = await fixture.Service.SaveMenuStateAsync("Gamma");
        fixture.Values["gammaMode"] = "2.2"; fixture.Values["gamma.BT1886"] = 0;
        fixture.Display.Requests.Clear();
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal("BT.1886", fixture.Values["gammaMode"]!.ToString());
        Assert.Equal(-2, fixture.Values["gamma.BT1886"]!.GetValue<int>());
        var methods = fixture.Writes.Select(request => request["method"]!.ToString()).ToArray();
        Assert.True(Array.IndexOf(methods, "gammaModeControl") < Array.IndexOf(methods, "gamma.BT1886Control"));
        Assert.Contains("gamma.ST2084Control/gamma.ST2084", fixture.Service.GetSnapshot().StateRecall!.Skipped.Keys);
    }

    [Theory]
    [InlineData("white20", false)]
    [InlineData("white20", true)]
    [InlineData("color", false)]
    [InlineData("color", true)]
    public async Task FullGridRecallRestoresAllRowsAndTheSavedMode(string section, bool saveInactive)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        if (saveInactive)
        {
            // Inactive white balance can retain queried rows; portable snapshot model also permits retained Custom rows.
            var snapshot = fixture.Service.GetSnapshot();
            var menu = snapshot.Menu with { Readings = snapshot.Menu.Readings.ToDictionary() };
            var read = menu.Readings[grid.ModeMethod];
            ((Dictionary<string, IpMenuRead>)menu.Readings)[grid.ModeMethod] = read with { Values = new JsonObject { [grid.ModeField] = section == "white20" ? "Off" : "Auto" } };
            var captured = IpMenuSavedStates.Capture("Inactive grid", snapshot.ActiveProfile!, menu, DateTimeOffset.UtcNow);
            await WriteStateAsync(fixture, captured);
        }
        else await fixture.Service.SaveMenuStateAsync("Full grid");
        var saved = Assert.Single(fixture.Service.GetSnapshot().SavedStates);
        Assert.Equal(grid.Values.Count * 3, saved.Values.Count(pair => IpMenuCatalog.Get(pair.Key).IsIndexed));
        foreach (var row in fixture.GridValues.Values) foreach (var field in grid.Fields) row[field] = 20;
        fixture.Values[grid.ModeField] = section == "white20" ? "Off" : "Auto";
        await fixture.Service.RecallMenuStateAsync(saved.Id, true);
        foreach (var row in fixture.GridValues.Values) foreach (var field in grid.Fields) Assert.Equal(10, row[field]!.GetValue<int>());
        Assert.Equal(saveInactive ? section == "white20" ? "Off" : "Auto" : grid.RequiredMode, fixture.Values[grid.ModeField]!.ToString());
        Assert.Equal("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
        Assert.Empty(fixture.Display.Batches);
    }

    [Fact]
    public async Task InterruptedRecallStopsLaterWritesAndNeverResumesAfterRestart()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        var grid = IpMenuGrids.ForSection("white20")!;
        foreach (var row in fixture.GridValues.Values) foreach (var field in grid.Fields) row[field] = 20;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (request, token) =>
        {
            if (request["method"]!.ToString() == "WB20P.GreenControl" && request["params"]!.AsObject().Count > 1)
            { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return null;
        };
        var recall = fixture.Service.RecallMenuStateAsync(id, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        fixture.Service.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => recall.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain(fixture.Writes, request => request["method"]!.ToString() == "WB20P.BlueControl");
        Assert.True(fixture.Service.GetSnapshot().StateRecall!.NeedsReview);
        fixture.Display.Requests.Clear();
        await fixture.RestartAsync();
        Assert.Empty(fixture.Display.Requests);
        Assert.True(fixture.Service.GetSnapshot().StateRecall!.NeedsReview);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CloseStateRecallReviewAsync());
        await fixture.Service.CloseMenuUpdateReviewAsync();
        await fixture.Service.CloseStateRecallReviewAsync();
        Assert.False(fixture.Service.GetSnapshot().StateRecall!.NeedsReview);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task PanelRequiresExplicitRecallAndDeleteConfirmation()
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(SavedSettingsStates));
        await page.StartAsync(new() { [nameof(SavedSettingsStates.Snapshot)] = fixture.Service.GetSnapshot() });
        await page.SelectAsync("Saved state", id.ToString());
        await page.ClickAsync("Recall settings…");
        await page.AssertDisabledAsync("Apply saved state now", true);
        await page.SetCheckboxAsync("Confirm saved state conditions", true);
        await page.AssertDisabledAsync("Apply saved state now", false);
        await page.ClickAsync("Cancel recall");
        await page.ClickAsync("Delete state");
        await page.ClickAsync("Cancel delete");
        Assert.Single(fixture.Service.GetSnapshot().SavedStates);
        Assert.Empty(fixture.Display.Requests);
        await page.ClickAsync("Delete state");
        await page.ClickAsync("Delete saved state permanently");
        Assert.Empty(fixture.Service.GetSnapshot().SavedStates);
        await page.AssertTextAsync("TV settings are unchanged");
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task MissingRgbPeerSkipsOnlyThatRowAndReportsIt()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        var id = await fixture.Service.SaveMenuStateAsync("All rows");
        var grid = IpMenuGrids.ForSection("white20")!;
        foreach (var row in fixture.GridValues.Values) foreach (var field in grid.Fields) row[field] = 20;
        fixture.GridValues["white20/5%"]["WB20P.Blue"] = null;
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal(3, fixture.Service.GetSnapshot().StateRecall!.Skipped.Count);
        Assert.Equal("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
        Assert.Equal(20, fixture.GridValues["white20/5%"]["WB20P.Red"]!.GetValue<int>());
        Assert.Equal(10, fixture.GridValues["white20/10%"]["WB20P.Red"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExplicitInactiveWhiteBalanceReadCanBeSavedWithoutEnablingItAgain()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        fixture.Values["WB20PointMode"] = "Off";
        await fixture.Service.RefreshInactiveWhiteBalanceAsync();
        fixture.Display.Requests.Clear();
        await fixture.Service.SaveMenuStateAsync("Off with loaded rows");
        var saved = Assert.Single(fixture.Service.GetSnapshot().SavedStates);
        Assert.Equal("Off", saved.Values["WB20PointModeControl/WB20PointMode"]);
        Assert.Equal(60, saved.Values.Count(pair => IpMenuCatalog.Get(pair.Key).IsIndexed));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task PanelCanSaveAndExplicitlyRecallWhileRetainingTheSavedState()
    {
        using var fixture = await ReadyAsync();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(SavedSettingsStates));
        await page.StartAsync(new() { [nameof(SavedSettingsStates.Snapshot)] = fixture.Service.GetSnapshot() });
        await page.AssertTextAbsentAsync("Recall settings…");
        await page.ChangeAsync("State name", "From panel");
        await page.ClickAsync("Save current settings");
        await page.AssertTextAsync("Settings saved locally");
        Assert.Empty(fixture.Display.Requests);
        fixture.Display.Contrast = 40;
        await page.ClickAsync("Recall settings…");
        await page.SetCheckboxAsync("Confirm saved state conditions", true);
        await page.ClickAsync("Apply saved state now");
        await page.AssertTextAsync("Recalled");
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Single(fixture.Service.GetSnapshot().SavedStates);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("wrong id")]
    [InlineData("unknown setting")]
    public async Task UnsafeFilesAreRejectedBeforeAnyNetworkRequest(string problem)
    {
        using var fixture = await ReadyAsync();
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        var file = Path.Combine(fixture.Service.SavedStatesDirectory, id.ToString("N") + ".json");
        var text = await File.ReadAllTextAsync(file);
        text = problem switch
        {
            "duplicate" => text.Insert(1, "\"Name\":\"Duplicate\","),
            "oversized" => text + new string(' ', 256 * 1024),
            "wrong id" => text.Replace(id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal),
            _ => text.Replace(Contrast, "powerControl/power", StringComparison.Ordinal)
        };
        await File.WriteAllTextAsync(file, text);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.RecallMenuStateAsync(id, true));
        Assert.Empty(fixture.Display.Requests);
    }

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        fixture.Display.Requests.Clear();
        return fixture;
    }

    private static async Task WriteStateAsync(MenuFixture fixture, IpMenuSavedState state)
    {
        Directory.CreateDirectory(fixture.Service.SavedStatesDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.Service.SavedStatesDirectory, state.Id.ToString("N") + ".json"), JsonSerializer.Serialize(state));
        await fixture.RestartAsync();
        await fixture.Service.ConnectMenuAsync(false);
    }
}
