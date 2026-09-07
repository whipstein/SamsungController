using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("SamsungController-IP-service-").FullName;
    internal static readonly IpRemoteProfile Profile = new()
    {
        Connection = new() { Host = "192.0.2.10" },
        Model = "Test display",
        Firmware = "1296",
        InputSource = "HDMI 1",
        PictureMode = "Filmmaker",
        Signal = "SDR, RGB, 8-bit"
    };

    [Fact]
    public async Task ProfilesAndTokensSurviveRestartWithoutNetworkOrChangesToExistingRemoteFiles()
    {
        var sentinel = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(sentinel, "existing remote profile");
        var client = new RecordingClient();
        using (var service = CreateService(client))
        {
            await service.InitializeAsync();
            await service.SaveProfileAsync(Profile);
            Assert.Empty(client.Calls);
            await service.PairAsync();
            Assert.Equal(new[] { "createAccessToken" }, client.Calls);
            Assert.True(service.GetSnapshot().HasToken);
        }
        using (var restarted = CreateService(client))
        {
            await restarted.InitializeAsync();
            Assert.Equal(Profile, restarted.GetSnapshot().ActiveProfile);
            Assert.True(restarted.GetSnapshot().HasToken);
            Assert.Empty(restarted.GetSnapshot().Observations);
            Assert.Single(client.Calls);
        }
        Assert.Equal("existing remote profile", await File.ReadAllTextAsync(sentinel));
        var log = await File.ReadAllTextAsync(Path.Combine(_directory, "ip-remote", "diagnostics.ndjson"));
        Assert.Contains("createAccessToken", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-credential", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBothIsExplicitOrderedAndUnsupportedFirstMethodDoesNotBlockSecond()
    {
        var client = new RecordingClient { FirstOutcome = SamsungIpRemoteOutcome.Unsupported };
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadBothAsync("unpaired"));
        Assert.Empty(client.Calls);
        await service.PairAsync();
        await service.ReadBothAsync("baseline");
        Assert.Equal(new[] { "createAccessToken", "getTVStates", "getVideoStates" }, client.Calls);
        Assert.Equal(3, service.GetSnapshot().Observations.Count);
        Assert.All(service.GetSnapshot().Observations.Skip(1), item => Assert.Equal("baseline", item.Label));
        Assert.False(service.GetSnapshot().IsBusy);
    }

    [Theory]
    [InlineData(SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData(SamsungIpRemoteOutcome.Timeout)]
    [InlineData(SamsungIpRemoteOutcome.TransportError)]
    [InlineData(SamsungIpRemoteOutcome.CertificateError)]
    [InlineData(SamsungIpRemoteOutcome.ProtocolError)]
    public async Task ReadBothStopsOnFailuresWithoutAutomaticPairingOrFallback(SamsungIpRemoteOutcome outcome)
    {
        var client = new RecordingClient { FirstOutcome = outcome };
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        await service.ReadBothAsync("failure");
        Assert.Equal(new[] { "createAccessToken", "getTVStates" }, client.Calls);
        Assert.Equal(outcome, service.GetSnapshot().Observations.Last().Exchange.Outcome);
        Assert.False(service.GetSnapshot().IsBusy);
        if (outcome == SamsungIpRemoteOutcome.Unauthorized)
        {
            Assert.True(service.GetSnapshot().AuthorizationRejected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadBothAsync("blocked"));
            Assert.Equal(2, client.Calls.Count);
            await service.PairAsync();
            Assert.False(service.GetSnapshot().AuthorizationRejected);
        }
    }

    [Fact]
    public async Task ObservationsKeepTheirOriginalContextAndDoNotBecomeAnotherDisplaysValues()
    {
        var client = new RecordingClient();
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        await service.ReadBothAsync("baseline");
        var changed = Profile with { PictureMode = "Movie", Signal = "HDR, YCbCr, 10-bit" };
        await service.SaveProfileAsync(changed);
        Assert.All(service.GetSnapshot().Observations, item => Assert.Equal(Profile.ContextKey, item.UserEnteredContext.ContextKey));
        var report = JsonNode.Parse(service.ExportReport(false))!;
        Assert.All(report["Methods"]!.AsArray(), method => Assert.Null(method!["LastAttempt"]));
        await service.ReadBothAsync("different context");
        Assert.Equal(changed, service.GetSnapshot().Observations.Last().UserEnteredContext);
        var another = Profile with { Connection = Profile.Connection with { Host = "192.0.2.11" } };
        await service.SaveProfileAsync(another);
        Assert.False(service.GetSnapshot().HasToken);
        await service.SelectProfileAsync(Profile.Endpoint);
        Assert.True(service.GetSnapshot().HasToken);
        Assert.Equal(changed, service.GetSnapshot().ActiveProfile);
    }

    [Fact]
    public async Task ExportRedactsDeviceIdentifiersByDefaultAndRetainsRawValueTypes()
    {
        using var service = CreateService(new RecordingClient());
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        await service.ReadBothAsync("baseline");
        var report = service.ExportReport();
        Assert.DoesNotContain("192.0.2.10", report, StringComparison.Ordinal);
        Assert.DoesNotContain("01:23:45:67:89:ab", report, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678-1234-1234-1234-123456789abc", report, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::10", report, StringComparison.Ordinal);
        Assert.Contains("192.0.2.10", service.ExportReport(false), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-credential", report, StringComparison.Ordinal);
        var result = JsonNode.Parse(report)!["Observations"]![1]!["Exchange"]!["Result"]!;
        Assert.Equal(10, result["unknownNumber"]!.GetValue<int>());
        Assert.Null(result["nullable"]);
        Assert.False(result["switch"]!.GetValue<bool>());
        Assert.Contains("Not tested; writes disabled", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelStopsRequestAndConcurrentPairIsRejectedRatherThanQueued()
    {
        var client = new RecordingClient { BlockReads = true };
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        var pending = service.ReadBothAsync("cancel this");
        await client.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.GetSnapshot().IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PairAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync(Profile)).WaitAsync(TimeSpan.FromSeconds(5));
        service.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.GetSnapshot().IsBusy);
        Assert.Equal(SamsungIpRemoteOutcome.Canceled, service.GetSnapshot().Observations.Last().Exchange.Outcome);
        Assert.Equal(new[] { "createAccessToken", "getTVStates" }, client.Calls);
    }

    [Fact]
    public async Task MissingFieldsAreNotMergedWithPreviousValuesAndFailedAttemptKeepsOnlyHistoricalEvidence()
    {
        var client = new RecordingClient();
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        await service.ReadAsync("getTVStates", "baseline");
        client.Result = new JsonObject();
        await service.ReadAsync("getTVStates", "empty result");
        Assert.Empty(service.GetSnapshot().Observations.Last().Exchange.Result!);
        client.FirstOutcome = SamsungIpRemoteOutcome.Timeout;
        await service.ReadAsync("getTVStates", "timeout");
        var report = JsonNode.Parse(service.ExportReport(false))!;
        Assert.Null(report["Methods"]![0]!["LastAttempt"]!["Result"]);
        Assert.Equal(4, service.GetSnapshot().Observations.Count);
    }

    [Fact]
    public async Task ForgetOnlyRemovesActiveEndpointToken()
    {
        var client = new RecordingClient();
        using var service = CreateService(client);
        await service.SaveProfileAsync(Profile);
        await service.PairAsync();
        var other = Profile with { Connection = Profile.Connection with { Port = 1515 } };
        await service.SaveProfileAsync(other);
        await service.PairAsync();
        await service.ForgetTokenAsync();
        Assert.False(service.GetSnapshot().HasToken);
        await service.SelectProfileAsync(Profile.Endpoint);
        Assert.True(service.GetSnapshot().HasToken);
        Assert.Equal(2, client.Calls.Count);
    }

    [Theory]
    [InlineData("{\"Profiles\":null}")]
    [InlineData("{\"Profiles\":[null]}")]
    [InlineData("{\"Profiles\":[{\"Connection\":null}]}")]
    public async Task InvalidProfileStructureIsReportedWithoutOverwritingTheFile(string content)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "ip-remote"));
        var path = Path.Combine(_directory, "ip-remote", "profiles.json");
        await File.WriteAllTextAsync(path, content);
        var client = new RecordingClient();
        using var service = CreateService(client);
        await Assert.ThrowsAsync<JsonException>(() => service.InitializeAsync());
        Assert.False(service.GetSnapshot().Initialized);
        Assert.Empty(client.Calls);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    private SamsungIpRemoteService CreateService(ISamsungIpRemoteClient client) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["SamsungController:ConfigurationDirectory"] = _directory }).Build(), client);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    internal sealed class RecordingClient : ISamsungIpRemoteClient
    {
        private readonly HashSet<string> _paired = [];
        public List<string> Calls { get; } = [];
        public SamsungIpRemoteOutcome FirstOutcome { get; set; } = SamsungIpRemoteOutcome.Success;
        public bool BlockReads { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonObject Result { get; set; } = new()
        {
            ["unknownNumber"] = 10,
            ["nullable"] = null,
            ["switch"] = false,
            ["ip"] = "192.0.2.10",
            ["ipv6"] = "2001:db8::10",
            ["mac"] = "01:23:45:67:89:ab",
            ["uuid"] = "12345678-1234-1234-1234-123456789abc"
        };
        public Task<bool> HasTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) => Task.FromResult(_paired.Contains(options.Endpoint.AbsoluteUri));
        public Task ForgetTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) { _paired.Remove(options.Endpoint.AbsoluteUri); return Task.CompletedTask; }
        public Task<SamsungIpRemoteExchange> PairAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("createAccessToken"); _paired.Add(options.Endpoint.AbsoluteUri);
            return Task.FromResult(Exchange(options, "createAccessToken", SamsungIpRemoteOutcome.Success) with { ResponseJson = "{\"result\":{\"AccessToken\":\"[redacted]\"}}", Result = null });
        }
        public async Task<SamsungIpRemoteExchange> ReadAsync(SamsungIpRemoteOptions options, string method, CancellationToken cancellationToken = default)
        {
            Calls.Add(method); ReadStarted.TrySetResult();
            if (BlockReads)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { return Exchange(options, method, SamsungIpRemoteOutcome.Canceled); }
            }
            return Exchange(options, method, method == "getTVStates" ? FirstOutcome : SamsungIpRemoteOutcome.Success);
        }
        private SamsungIpRemoteExchange Exchange(SamsungIpRemoteOptions options, string method, SamsungIpRemoteOutcome outcome) => new(
            DateTimeOffset.UtcNow, Calls.Count, method, options.Endpoint.AbsoluteUri, outcome, outcome.ToString(),
            "{\"params\":{\"AccessToken\":\"[redacted]\"}}", ResponseJson: Result.ToJsonString(),
            Result: outcome == SamsungIpRemoteOutcome.Success ? (JsonObject)Result.DeepClone() : null);
    }
}
