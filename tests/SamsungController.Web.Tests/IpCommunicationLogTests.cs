using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpCommunicationLogTests
{
    [Fact]
    public async Task SavedHistoryPagesAndExportsAllMatchingRecordsAcrossRestartWithoutTvRequests()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var records = Enumerable.Range(1, 135).Select(index => Sample(index, failed: index % 2 == 0)).ToArray();
        var original = string.Concat(records.Select(record => JsonSerializer.Serialize(record) + Environment.NewLine));
        await File.WriteAllTextAsync(fixture.Service.DiagnosticLogPath, original);
        await fixture.RestartAsync();
        Assert.Empty(fixture.Service.GetSnapshot().Observations);
        var first = await fixture.Service.ReadCommunicationLogAsync(new(), pageSize: 50);
        var second = await fixture.Service.ReadCommunicationLogAsync(new(), page: 1, pageSize: 50);
        var last = await fixture.Service.ReadCommunicationLogAsync(new(), page: 2, pageSize: 50);
        Assert.Equal(135, first.Total);
        Assert.Equal(135, first.Entries[0].Number);
        Assert.Equal(85, second.Entries[0].Number);
        Assert.Equal(35, last.Entries.Count);
        Assert.Equal(135, first.Entries.Concat(second.Entries).Concat(last.Entries).Select(entry => entry.Number).Distinct().Count());
        var filtered = await fixture.Service.ReadCommunicationLogAsync(new(ErrorsOnly: true));
        Assert.Equal(67, filtered.Total);
        Assert.All(filtered.Entries, entry => Assert.False(entry.Observation.Exchange.IsSuccess));
        var export = await fixture.Service.ExportCommunicationLogAsync(new(), savedHistory: true);
        Assert.Equal(135, export.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var failedExport = await fixture.Service.ExportCommunicationLogAsync(new(ErrorsOnly: true), savedHistory: true);
        Assert.Equal(67, failedExport.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("sample-secret", export, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", export, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.Service.DiagnosticLogPath));
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ViewAndExportRedactEmbeddedTokensAndOptionalIdentifiersWithoutChangingSource(bool identifiers, bool sha)
    {
        var observation = Sample(1);
        var original = JsonSerializer.Serialize(observation);
        var safe = IpCommunicationLog.Safe(observation, identifiers, sha);
        var text = IpCommunicationLog.Format(observation, identifiers, sha);
        Assert.DoesNotContain("sample-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-secret", safe.Exchange.RequestJson, StringComparison.Ordinal);
        Assert.Equal(!identifiers, text.Contains("192.0.2.10", StringComparison.Ordinal));
        Assert.Equal(!identifiers, text.Contains("00:11:22:33:44:55", StringComparison.Ordinal));
        Assert.Equal(!identifiers, text.Contains("SAMPLE-SERIAL", StringComparison.Ordinal));
        Assert.Equal(!sha, text.Contains(new string('A', 64), StringComparison.Ordinal));
        Assert.Equal(original, JsonSerializer.Serialize(observation));
    }

    [Fact]
    public async Task CorruptAndPartialRecordsAreReportedAndNeverSilentlyExported()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Service.DiagnosticLogPath, JsonSerializer.Serialize(Sample(1)) + Environment.NewLine + "bad json" + Environment.NewLine + "{\"partial\":");
        var page = await fixture.Service.ReadCommunicationLogAsync(new());
        Assert.Single(page.Entries);
        Assert.Equal(2, page.SkippedLines);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExportCommunicationLogAsync(new(), true));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task HistoryHandlesBothNewlineStylesAndCancellationWithoutChangingTheArchive()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var original = JsonSerializer.Serialize(Sample(1)) + "\r\n" + JsonSerializer.Serialize(Sample(2)) + "\n";
        await File.WriteAllTextAsync(fixture.Service.DiagnosticLogPath, original);
        var page = await fixture.Service.ReadCommunicationLogAsync(new());
        Assert.Equal(2, page.Total);
        Assert.Equal(0, page.SkippedLines);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ReadCommunicationLogAsync(new(), cancellationToken: cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.Service.DiagnosticLogPath));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task LogPageReplacesTestingRoutesAndSupportsHistorySearchAndDownloadWithoutSending()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Service.DiagnosticLogPath, string.Concat(new[] { Sample(1), Sample(2, failed: true) }.Select(record => JsonSerializer.Serialize(record) + Environment.NewLine)));
        var javascript = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(CommunicationLog));
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Communication log");
        await renderer.AssertTextAbsentAsync("Prepare command");
        await renderer.ClickAsync("Saved history");
        await renderer.AssertTextAsync("2 matching exchanges");
        await renderer.AssertTextAsync("TX · sent to TV");
        await renderer.AssertTextAsync("RX · received from TV");
        await renderer.AssertTextAbsentAsync("sample-secret");
        await renderer.SetCheckboxAsync("Errors only", true);
        await renderer.AssertTextAsync("1 matching exchanges");
        await renderer.ClickAsync("Export filtered log");
        Assert.Single(javascript.Download.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("sample-secret", javascript.Download, StringComparison.Ordinal);
        await renderer.ChangeAsync("Search communication log", "no-match", "onchange");
        await renderer.AssertTextAsync("0 matching exchanges");
        await renderer.AssertDisabledAsync("Export filtered log", true);
        Assert.Empty(fixture.Display.Requests);
        foreach (var route in new[] { "/diagnostics", "/ip-commands", "/diagnostics/picture-tests", "/diagnostics/picture-workspace" })
        {
            var type = typeof(CommunicationLog).Assembly.GetTypes().Single(type => type.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Any(attribute => attribute.Template == route));
            Assert.Equal(typeof(CommunicationLog), type);
        }
    }

    [Fact]
    public async Task LiveViewKeepsTheWholeConnectionAndCanPauseWithoutStoppingLogging()
    {
        using var fixture = await MenuFixture.CreateAsync();
        for (var index = 0; index < 45; index++) await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        Assert.Equal(135, fixture.Service.GetSnapshot().Observations.Count);
        var javascript = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(CommunicationLog));
        await renderer.StartAsync();
        await renderer.AssertTextAsync("135 matching exchanges");
        await renderer.ClickAsync("Older");
        await renderer.AssertTextAsync("page 2 of 3");
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await renderer.AssertTextAsync("135 matching exchanges");
        await renderer.ClickAsync("Resume live view");
        await renderer.AssertTextAsync("138 matching exchanges");
        var requests = fixture.Display.Requests.Count;
        await renderer.ClickAsync("Export filtered log");
        Assert.Equal(138, javascript.Download.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(requests, fixture.Display.Requests.Count);
    }

    private static IpRemoteObservation Sample(int id, bool failed = false)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "localDimmingControl", ["params"] = new JsonObject { ["AccessToken"] = "sample-secret" } };
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["echo"] = "sample-secret", ["serialNumber"] = "SAMPLE-SERIAL", ["mac"] = "00:11:22:33:44:55" } };
        return new(IpRemoteServiceTests.Profile, "Row ü " + id, new(DateTimeOffset.UtcNow, id, "localDimmingControl", "https://192.0.2.10:1516/",
            failed ? SamsungIpRemoteOutcome.RpcError : SamsungIpRemoteOutcome.Success, failed ? "TV failed" : "Read", request.ToJsonString(), response.ToJsonString(),
            HttpStatus: 200, RpcErrorCode: failed ? -32002 : null, ObservedCertificateSha256: new string('A', 64)));
    }
}
