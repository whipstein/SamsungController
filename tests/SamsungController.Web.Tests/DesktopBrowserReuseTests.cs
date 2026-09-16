using System.Net;
using System.Net.Http.Json;
using SamsungController.Desktop;

namespace SamsungController.Web.Tests;

public sealed class DesktopBrowserReuseTests
{
    private static readonly DesktopInstance Instance = new("test-instance", "test-only-private-token", 123, 55123);
    private static DesktopStatus Ready() => new(DesktopFiles.Product, DesktopFiles.Version, true, Instance.Instance, Instance.ProcessId);

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public async Task OpensOnlyWhenRequestedAndNoLiveTabAcknowledges(bool show, bool reused, int expectedOpened)
    {
        var opened = new List<string>(); var probes = 0;
        await DesktopBrowser.OpenReadyAsync(Ready(), DesktopFiles.Address(55123), show, opened.Add,
            () => { probes++; return Task.FromResult(reused); }, 123);
        Assert.Equal(show ? 1 : 0, probes);
        Assert.Equal(expectedOpened, opened.Count);
        if (expectedOpened > 0) Assert.Equal(DesktopFiles.Address(55123).ToString(), opened[0]);
    }

    [Fact]
    public async Task UnrelatedServerNeverReceivesReuseRequestOrBrowserOpen()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopBrowser.OpenReadyAsync(
            Ready() with { Product = "Other" }, DesktopFiles.Address(55123), true,
            _ => Assert.Fail("Must not open"), () => throw new Exception("Must not probe")));
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("no-tab", false)]
    [InlineData("different-instance", false)]
    [InlineData("http-error", false)]
    [InlineData("invalid-json", false)]
    [InlineData("network", false)]
    [InlineData("timeout", false)]
    public async Task PrivateAuthenticatedRequestRequiresMatchingLiveAcknowledgement(string reply, bool expected)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri(DesktopFiles.Address(55123), DesktopFiles.ReopenRoute), request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Instance.Token, request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("Origin"));
            return reply switch
            {
                "network" => throw new HttpRequestException(),
                "timeout" => throw new TaskCanceledException(),
                "http-error" => new(HttpStatusCode.Forbidden),
                "invalid-json" => new(HttpStatusCode.OK) { Content = new StringContent("not json") },
                _ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new DesktopBrowserReuse(
                    reply == "different-instance" ? "other-instance" : Instance.Instance, reply != "no-tab")) }
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = DesktopFiles.Address(55123) };
        Assert.Equal(expected, await DesktopBrowser.TryReuseAsync(client, Ready(), Instance));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("instance")]
    [InlineData("process")]
    [InlineData("port")]
    public async Task MissingOrStalePrivateMetadataDoesNotSendCredentials(string problem)
    {
        using var handler = new Handler(_ => throw new Exception("Must not send credentials"));
        using var client = new HttpClient(handler) { BaseAddress = DesktopFiles.Address(55123) };
        var metadata = problem switch
        {
            "missing" => null,
            "instance" => Instance with { Instance = "stale" },
            "process" => Instance with { ProcessId = 999 },
            _ => Instance with { Port = 55124 }
        };
        Assert.False(await DesktopBrowser.TryReuseAsync(client, Ready(), metadata));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
