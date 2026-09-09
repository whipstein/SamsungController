using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteTrustTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("SamsungController-trust-").FullName;
    private readonly TrustClient _client = new();
    private readonly TestClock _clock = new();
    private SamsungIpRemoteService CreateService() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["SamsungController:ConfigurationDirectory"] = _directory }).Build(), _client, _clock);
    private static readonly IpRemoteProfile Profile = new() { Model = "Test display", Connection = new() { Host = "192.0.2.10", AllowUntrustedCertificate = true } };

    [Fact]
    public async Task ReviewSendsNoRpcThenConfirmationPersistsStrictPinBeforePairingAndSurvivesRestart()
    {
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await service.InspectDisplayCertificateAsync();
        Assert.Equal(Profile, service.GetSnapshot().ActiveProfile);
        Assert.Empty(_client.PairCalls);
        _client.OnPair = options =>
        {
            var saved = File.ReadAllText(Path.Combine(_directory, "ip-remote", "profiles.json"));
            Assert.Contains(_client.Pin, saved, StringComparison.Ordinal);
            Assert.False(options.AllowUntrustedCertificate);
            Assert.Equal(_client.Pin, options.NormalizedCertificatePin);
        };
        await service.TrustDisplayAndPairAsync(service.GetSnapshot().CertificateReview!.Id);
        Assert.Single(_client.PairCalls);
        Assert.Null(service.GetSnapshot().CertificateReview);
        using var restarted = CreateService();
        await restarted.InitializeAsync();
        Assert.True(restarted.GetSnapshot().HasToken);
        Assert.Equal(_client.Pin, restarted.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
        Assert.False(restarted.GetSnapshot().ActiveProfile!.Connection.AllowUntrustedCertificate);
        Assert.Null(restarted.GetSnapshot().CertificateReview);
        Assert.Single(_client.PairCalls);
    }

    [Fact]
    public async Task AlreadyPairedDisplayKeepsTokenWithoutReapproval()
    {
        _client.Paired = true;
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await service.InspectDisplayCertificateAsync();
        await service.TrustDisplayAndPairAsync(service.GetSnapshot().CertificateReview!.Id);
        Assert.Empty(_client.PairCalls);
        Assert.True(service.GetSnapshot().HasToken);
        Assert.False(service.GetSnapshot().ActiveProfile!.Connection.AllowUntrustedCertificate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("endpoint")]
    [InlineData("timeout")]
    public async Task InvalidInspectionCannotProduceATrustConfirmation(string failure)
    {
        _client.InspectionFailure = failure;
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await Assert.ThrowsAnyAsync<Exception>(() => service.InspectDisplayCertificateAsync());
        Assert.Null(service.GetSnapshot().CertificateReview);
        Assert.Equal(Profile, service.GetSnapshot().ActiveProfile);
        Assert.False(service.GetSnapshot().IsBusy);
        Assert.Empty(_client.PairCalls);
    }

    [Fact]
    public async Task DifferentSavedPinNeedsSeparateReplacementApproval()
    {
        using var service = CreateService();
        await service.SaveProfileAsync(Profile with { Connection = Profile.Connection with { CertificateSha256 = new string('B', 64) } });
        await service.InspectDisplayCertificateAsync();
        var review = service.GetSnapshot().CertificateReview!;
        Assert.True(review.ReplacesCertificate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TrustDisplayAndPairAsync(review.Id));
        Assert.Equal(new string('B', 64), service.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
        Assert.Empty(_client.PairCalls);
        await service.TrustDisplayAndPairAsync(review.Id, confirmReplacement: true);
        Assert.Single(_client.PairCalls);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("edit")]
    [InlineData("switch")]
    [InlineData("expire")]
    [InlineData("recheck")]
    public async Task StaleConfirmationNeverChangesTrustOrPairs(string reason)
    {
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await service.InspectDisplayCertificateAsync();
        var review = service.GetSnapshot().CertificateReview!;
        if (reason == "cancel") service.Cancel();
        if (reason == "edit") await service.SaveProfileAsync(Profile with { Model = "Renamed" });
        if (reason == "switch") await service.SelectProfileAsync(Profile.Endpoint);
        if (reason == "expire") _clock.Now += TimeSpan.FromMinutes(6);
        if (reason == "recheck") await service.InspectDisplayCertificateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TrustDisplayAndPairAsync(review.Id));
        Assert.Empty(_client.PairCalls);
        Assert.Null(service.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
        Assert.False(service.GetSnapshot().IsBusy);
    }

    [Fact]
    public async Task StorageFailureDoesNotPairOrAcceptInMemoryTrust()
    {
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await service.InspectDisplayCertificateAsync();
        Directory.CreateDirectory(Path.Combine(_directory, "ip-remote", "profiles.json.tmp"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.TrustDisplayAndPairAsync(service.GetSnapshot().CertificateReview!.Id));
        Assert.Empty(_client.PairCalls);
        Assert.Null(service.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
    }

    [Fact]
    public async Task FailedPairRetainsPinAndLeavesNormalRetryAvailable()
    {
        _client.PairOutcome = SamsungIpRemoteOutcome.Timeout;
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await service.InspectDisplayCertificateAsync();
        await service.TrustDisplayAndPairAsync(service.GetSnapshot().CertificateReview!.Id);
        Assert.False(service.GetSnapshot().HasToken);
        Assert.False(service.GetSnapshot().IsBusy);
        Assert.Equal(_client.Pin, service.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
        Assert.False(service.GetSnapshot().ActiveProfile!.Connection.AllowUntrustedCertificate);
        _client.PairOutcome = SamsungIpRemoteOutcome.Success;
        await service.PairAsync();
        Assert.True(service.GetSnapshot().HasToken);
    }

    [Fact]
    public async Task CanceledInspectionCannotLeaveAnApprovalAndBlocksOtherOperationsUntilFinished()
    {
        _client.Block = true;
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        var pending = service.InspectDisplayCertificateAsync();
        await _client.Started.Task;
        Assert.True(service.GetSnapshot().IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync(Profile));
        service.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(service.GetSnapshot().IsBusy);
        Assert.Null(service.GetSnapshot().CertificateReview);
        Assert.Empty(_client.PairCalls);
    }

    [Fact]
    public async Task DisplayPageRequiresConfirmationAndCancelClearsReviewWithoutPairing()
    {
        using var service = CreateService();
        await service.SaveProfileAsync(Profile);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(service)
            .AddSingleton<NavigationManager>(new IpMenuTests.MenuNavigation()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DisplaySetup));
        await renderer.StartAsync();
        await renderer.ClickAsync("Trust this display and pair");
        await renderer.AssertTextAsync("Confirm this display");
        await renderer.AssertDisabledAsync("Confirm trust and pair", true);
        await renderer.SetCheckboxAsync("Confirm display certificate trust", true);
        await renderer.AssertDisabledAsync("Confirm trust and pair", false);
        await renderer.ClickAsync("Cancel trust");
        Assert.Null(service.GetSnapshot().CertificateReview);
        await renderer.ClickAsync("Trust this display and pair");
        await renderer.AssertCheckboxAsync("Confirm display certificate trust", false);
        Assert.Empty(_client.PairCalls);
        // Pair failure remains on Display with an error, a saved pin, and retry enabled.
        _client.PairOutcome = SamsungIpRemoteOutcome.Timeout;
        await renderer.SetCheckboxAsync("Confirm display certificate trust", true);
        await renderer.ClickAsync("Confirm trust and pair");
        await renderer.AssertTextAsync("Timeout");
        await renderer.AssertDisabledAsync("Pair with TV", false);
        Assert.Equal(_client.Pin, service.GetSnapshot().ActiveProfile!.Connection.CertificateSha256);
    }

    public void Dispose() => Directory.Delete(_directory, true);
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TrustClient : ISamsungIpRemoteClient
    {
        public string Pin { get; } = new('A', 64);
        public bool Paired { get; set; }
        public bool Block { get; set; }
        public string? InspectionFailure { get; set; }
        public SamsungIpRemoteOutcome PairOutcome { get; set; } = SamsungIpRemoteOutcome.Success;
        public List<SamsungIpRemoteOptions> PairCalls { get; } = [];
        public Action<SamsungIpRemoteOptions>? OnPair { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SamsungCertificateInspection> InspectCertificateAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(InspectionFailure == "endpoint" ? "https://192.0.2.11:1516/" : options.Endpoint.AbsoluteUri,
                InspectionFailure == "timeout" ? SamsungIpRemoteOutcome.Timeout : SamsungIpRemoteOutcome.Success,
                "Certificate retrieved", InspectionFailure == "missing" ? null : InspectionFailure == "invalid" ? "bad-pin" : Pin);
        }
        public Task<bool> HasTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) => Task.FromResult(Paired);
        public Task ForgetTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Trust must never clear tokens.");
        public Task<SamsungIpRemoteExchange> PairAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default)
        {
            OnPair?.Invoke(options); PairCalls.Add(options);
            if (PairOutcome == SamsungIpRemoteOutcome.Success) Paired = true;
            return Task.FromResult(new SamsungIpRemoteExchange(DateTimeOffset.UtcNow, 1, "createAccessToken", options.Endpoint.AbsoluteUri,
                PairOutcome, PairOutcome.ToString(), "{}"));
        }
        public Task<SamsungIpRemoteExchange> ReadAsync(SamsungIpRemoteOptions options, string method, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inspection must not read TV settings.");
        public Task<SamsungIpRemoteExchange> WritePictureControlAsync(SamsungIpRemoteOptions options, string control, int value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Trust must not change TV settings.");
    }
}
