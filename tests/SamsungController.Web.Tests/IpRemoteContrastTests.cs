using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemotePictureTests
{
    [Fact]
    public async Task GuidedRoundTripReadsFreshValuesRequiresVisualConfirmationAndPersistsEvidence()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var service = fixture.Service;
        Assert.Empty(fixture.Display.Requests);
        await service.PreparePictureTestAsync();
        var test = service.GetSnapshot().PictureTest!;
        Assert.Equal(45, test.Original);
        Assert.Equal(44, test.Target);
        Assert.Empty(fixture.Display.Writes);
        await service.ApplyPictureTestAsync(test.Id, true);
        Assert.Equal(IpRemotePictureStage.AwaitingVisualCheck, service.GetSnapshot().PictureTest!.Stage);
        Assert.False(service.GetSnapshot().PictureTest!.Verified);
        Assert.Equal(44, fixture.Display.Contrast);
        await service.RestorePictureTestAsync(test.Id, true);
        var completed = service.GetSnapshot().PictureTest!;
        Assert.True(completed.Verified);
        Assert.True(completed.RestorationConfirmed);
        Assert.False(completed.RequiresRecovery);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", "getTVStates", "getVideoStates", "contrastControl",
            "getTVStates", "getVideoStates", "getTVStates", "getVideoStates", "contrastControl", "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        var report = service.ExportReport();
        Assert.True(JsonNode.Parse(report)!["PictureTest"]!["Verified"]!.GetValue<bool>());
        Assert.DoesNotContain(ContrastFixture.Token, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Profile.Connection.Host, report, StringComparison.Ordinal);
        var journal = await File.ReadAllTextAsync(fixture.JournalPath);
        Assert.DoesNotContain(ContrastFixture.Token, journal, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.JournalPath));
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.True(fixture.Service.GetSnapshot().HasToken);
        Assert.Equal(count, fixture.Display.Requests.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"45\"")]
    [InlineData("45.5")]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("101")]
    public async Task InvalidBaselineNeverEnablesWrites(string value)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        fixture.Display.VideoOverride = new JsonObject { ["contrast"] = JsonNode.Parse(value) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync());
        Assert.Null(fixture.Service.GetSnapshot().PictureTest);
        Assert.Empty(fixture.Display.Writes);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("contrast")]
    [InlineData("other-field")]
    public async Task FreshPreflightRejectsChangedConditionsBeforeWrite(string change)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        switch (change)
        {
            case "input": fixture.Display.Input = "HDMI1"; break;
            case "mode": fixture.Display.Mode = "Standard"; break;
            case "contrast": fixture.Display.Contrast = 43; break;
            default: fixture.Display.Color = 26; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Empty(fixture.Display.Writes);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
    }

    [Fact]
    public async Task MissingConsentOrStaleTestIdentityDoesNotSendEvenReads()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(Guid.NewGuid(), true));
        Assert.Equal(2, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Display.Writes);
    }

    [Fact]
    public async Task ProfileChangeInvalidatesPreparedTestAndPendingWriteLocksProfileAndCredentialRemoval()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { Signal = "different signal" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Empty(fixture.Display.Writes);
        await fixture.Service.PreparePictureTestAsync();
        id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(ContrastFixture.Profile));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SelectProfileAsync(ContrastFixture.Profile.Endpoint));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ForgetTokenAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync());
        Assert.Single(fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
    }

    [Fact]
    public async Task NegativeVisualCheckRestoresButDoesNotVerify()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        await fixture.Service.RestorePictureTestAsync(id, false);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Fact]
    public async Task AcknowledgmentWithoutChangedReadbackIsNotSuccessAndRecoveryAvoidsRedundantWrite()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        fixture.Display.Override = (request, _) => Task.FromResult(request["method"]!.GetValue<string>() == "contrastControl"
            ? ContrastDisplay.Reply(request, new JsonObject { ["contrast"] = 44 }) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Equal(IpRemotePictureStage.RecoveryRequired, fixture.Service.GetSnapshot().PictureTest!.Stage);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.ChangeReadbackConfirmed);
        await fixture.Service.RestorePictureTestAsync(id);
        Assert.Single(fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
    }

    [Theory]
    [InlineData("rpc-unsupported")]
    [InlineData("unauthorized")]
    [InlineData("wrong-id")]
    [InlineData("transport")]
    public async Task FailedWriteDoesNotRetryReadOrRestoreAutomatically(string failure)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != "contrastControl") return Task.FromResult<HttpResponseMessage?>(null);
            if (failure == "transport") throw new HttpRequestException("simulated disconnect");
            var response = failure == "wrong-id" ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 999, ["result"] = new JsonObject() }
                : new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = failure == "unauthorized" ? -32010 : -32001, ["message"] = ContrastFixture.Token }
                };
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) });
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Equal(5, fixture.Display.Requests.Count);
        Assert.Single(fixture.Display.Writes);
        Assert.Equal(IpRemotePictureStage.RecoveryRequired, fixture.Service.GetSnapshot().PictureTest!.Stage);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.DoesNotContain(ContrastFixture.Token, fixture.Service.ExportReport(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDuringAmbiguousWritePersistsOriginalRejectsConcurrentActionsAndRecoversAfterRestart()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() != "contrastControl") return null;
            fixture.Display.Contrast = 44; // TV may apply before our socket is canceled.
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!;
            Assert.True(journal["WriteAttempted"]!.GetValue<bool>());
            Assert.Equal(45, journal["Original"]!.GetValue<int>());
            sent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        };
        var applying = fixture.Service.ApplyPictureTestAsync(id, true);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReadBothAsync("concurrent"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PairAsync());
        fixture.Service.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => applying);
        Assert.Equal(5, fixture.Display.Requests.Count);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
        fixture.Display.Override = null;
        await fixture.RestartAsync();
        Assert.Equal(5, fixture.Display.Requests.Count);
        Assert.Equal(IpRemotePictureStage.RecoveryRequired, fixture.Service.GetSnapshot().PictureTest!.Stage);
        await fixture.Service.RestorePictureTestAsync(id);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("unexpected-value")]
    [InlineData("other-field")]
    public async Task RecoveryNeverOverwritesAChangedContextOrUnrelatedValue(string change)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        if (change == "input") fixture.Display.Input = "HDMI1";
        else if (change == "unexpected-value") fixture.Display.Contrast = 40;
        else fixture.Display.Color = 26;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestorePictureTestAsync(id, true));
        Assert.Single(fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        await fixture.Service.CloseManuallyRestoredPictureTestAsync(id);
        Assert.Single(fixture.Display.Writes);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
    }

    [Fact]
    public async Task FailedRestorationIsNotRepeatedAndManualReadbackDoesNotFalselyVerify()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        fixture.Display.Override = (request, _) => request["method"]!.GetValue<string>() == "contrastControl"
            ? throw new HttpRequestException("simulated restoration failure") : Task.FromResult<HttpResponseMessage?>(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestorePictureTestAsync(id, true));
        fixture.Display.Override = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestorePictureTestAsync(id));
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        fixture.Display.Contrast = 45;
        await fixture.Service.RestorePictureTestAsync(id);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
    }

    [Fact]
    public async Task RecoveryFileWriteFailurePreventsAnySettingRequest()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        Directory.CreateDirectory(fixture.JournalPath + ".tmp"); // block atomic replacement without changing file permissions
        var error = await Record.ExceptionAsync(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(fixture.Display.Writes);
    }

    [Fact]
    public async Task PreviousSessionPreparedBaselineCannotWriteWithoutNewPreparation()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.RestartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Empty(fixture.Display.Writes);
        Assert.Equal(2, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task WriteTimeoutLeavesRecoveryPendingWithoutAnyFollowUp()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with
        {
            Connection = ContrastFixture.Profile.Connection with { RequestTimeout = TimeSpan.FromMilliseconds(100) }
        });
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() != "contrastControl") return null;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(id, true));
        Assert.Equal(SamsungIpRemoteOutcome.Timeout, fixture.Service.GetSnapshot().Observations.Last().Exchange.Outcome);
        Assert.Equal(5, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
    }

    [Fact]
    public async Task FailedFinalReadCanBeConfirmedAfterRestartWithoutRepeatingTheRestoration()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        fixture.Display.Override = (request, _) =>
        {
            if (fixture.Display.Writes.Count() == 2 && request["method"]!.GetValue<string>() == "getVideoStates")
                throw new HttpRequestException("simulated final read failure");
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestorePictureTestAsync(id, true));
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestoreAcknowledged);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
        fixture.Display.Override = null;
        await fixture.RestartAsync();
        await fixture.Service.RestorePictureTestAsync(id);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.Verified);
    }

    [Theory]
    [InlineData("inputSource")]
    [InlineData("pictureMode")]
    public async Task MissingReportedContextStopsPreparationBeforeAnyVideoQueryOrWrite(string field)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        fixture.Display.Override = (request, _) =>
        {
            var result = new JsonObject { ["inputSource"] = "HDMI4", ["pictureMode"] = "FilmmakerMode" };
            result.Remove(field);
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, result));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync());
        Assert.Single(fixture.Display.Requests);
        Assert.Empty(fixture.Display.Writes);
    }
}

internal sealed class ContrastFixture : IDisposable
{
    internal const string Token = "simulated-private-contrast-credential";
    internal static readonly IpRemoteProfile Profile = IpRemoteServiceTests.Profile;
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("SamsungController-IP-contrast-").FullName;
    public string JournalPath => Path.Combine(DirectoryPath, "ip-remote", "contrast-test.json");
    public ContrastDisplay Display { get; } = new();
    public SamsungIpRemoteService Service { get; private set; } = null!;
    private readonly List<HttpClient> _clients = [];
    private TimeProvider? _timeProvider;
    private SamsungIpRemoteService CreateService()
    {
        var http = new HttpClient(Display, disposeHandler: false);
        _clients.Add(http);
        return new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["SamsungController:ConfigurationDirectory"] = DirectoryPath }).Build(),
            new SamsungIpRemoteClient(new PrivateIpRemoteTokenStore(Path.Combine(DirectoryPath, "ip-remote")), http), _timeProvider);
    }
    public static async Task<ContrastFixture> CreateAsync(TimeProvider? timeProvider = null)
    {
        var fixture = new ContrastFixture { _timeProvider = timeProvider };
        await new PrivateIpRemoteTokenStore(Path.Combine(fixture.DirectoryPath, "ip-remote")).SaveAsync(Profile.Endpoint, Token);
        fixture.Service = fixture.CreateService();
        await fixture.Service.SaveProfileAsync(Profile);
        return fixture;
    }
    public async Task VerifyAsync(bool visualPass = true, string control = "contrast")
    {
        await Service.PreparePictureTestAsync(control);
        var id = Service.GetSnapshot().PictureTest!.Id;
        await Service.ApplyPictureTestAsync(id, true);
        await Service.RestorePictureTestAsync(id, visualPass);
    }
    public async Task RestartAsync()
    {
        Service.Dispose();
        Service = CreateService();
        await Service.InitializeAsync();
    }
    public void Dispose()
    {
        Service.Dispose();
        foreach (var client in _clients) client.Dispose();
        Display.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class ContrastDisplay : HttpMessageHandler
{
    public int Contrast { get; set; } = 45;
    public int Color { get; set; } = 25;
    public int Sharpness { get; set; }
    public string Input { get; set; } = "HDMI4";
    public string Mode { get; set; } = "FilmmakerMode";
    public JsonObject? VideoOverride { get; set; }
    public List<JsonObject> Requests { get; } = [];
    public IEnumerable<string> Methods => Requests.Select(item => item["method"]!.GetValue<string>());
    public IEnumerable<int> Writes => Requests.Where(item => item["method"]!.GetValue<string>() == "contrastControl")
        .Select(item => item["params"]!["contrast"]!.GetValue<int>());
    public IEnumerable<int> WritesFor(string control) => Requests.Where(item => item["method"]!.GetValue<string>() == control + "Control")
        .Select(item => item["params"]![control]!.GetValue<int>());
    public Func<JsonObject, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Assert.Equal(ContrastFixture.Token, json["params"]!["AccessToken"]!.GetValue<string>());
        Requests.Add(json);
        if (Override is not null && await Override(json, cancellationToken) is { } overridden) return overridden;
        var method = json["method"]!.GetValue<string>();
        if (method == "contrastControl") Contrast = json["params"]!["contrast"]!.GetValue<int>();
        if (method == "colorControl") Color = json["params"]!["color"]!.GetValue<int>();
        if (method == "sharpnessControl") Sharpness = json["params"]!["sharpness"]!.GetValue<int>();
        return Reply(json, method switch
        {
            "getTVStates" => new JsonObject { ["inputSource"] = Input, ["pictureMode"] = Mode },
            "getVideoStates" => VideoOverride?.DeepClone() ?? new JsonObject { ["contrast"] = Contrast, ["color"] = Color, ["sharpness"] = Sharpness, ["brightness"] = 0 },
            "contrastControl" => new JsonObject { ["contrast"] = Contrast, ["echo"] = ContrastFixture.Token },
            "colorControl" => new JsonObject { ["color"] = Color, ["echo"] = ContrastFixture.Token },
            "sharpnessControl" => new JsonObject { ["sharpness"] = Sharpness, ["echo"] = ContrastFixture.Token },
            _ => throw new InvalidOperationException("Unexpected test request: " + method)
        });
    }
    public static HttpResponseMessage Reply(JsonObject request, JsonNode result) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.ToJsonString(), ["result"] = result }.ToJsonString())
    };
}
