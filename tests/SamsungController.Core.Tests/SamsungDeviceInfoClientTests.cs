using System.Net;
using System.Text;
using SamsungController.Core.Devices;

namespace SamsungController.Core.Tests;

public sealed class SamsungDeviceInfoClientTests
{
    [Fact]
    public async Task SecureProbeUsesConfiguredSamsungEndpointAndParsesJson()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"device":{"name":"Living Room"}}""");
        using var httpClient = new HttpClient(handler);
        var client = new SamsungDeviceInfoClient(httpClient);

        var result = await client.GetAsync(new SamsungDeviceInfoRequest
        {
            Host = "192.0.2.10",
            Secure = true,
            AllowUntrustedCertificate = true
        });

        Assert.Equal("https://192.0.2.10:8002/api/v2/", handler.LastRequestUri?.AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.IsSuccessStatusCode);
        Assert.Equal("Living Room", result.ParsedPayload?["device"]?["name"]?.GetValue<string>());
        Assert.Null(result.ParseError);
    }

    [Fact]
    public async Task ProbeHonorsPlaintextPortOverride()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var client = new SamsungDeviceInfoClient(httpClient);

        await client.GetAsync(new SamsungDeviceInfoRequest
        {
            Host = "tv.local",
            Secure = false,
            Port = 9123
        });

        Assert.Equal("http://tv.local:9123/api/v2/", handler.LastRequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task NonJsonErrorResponseIsPreserved()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "TV sleeping");
        using var httpClient = new HttpClient(handler);
        var client = new SamsungDeviceInfoClient(httpClient);

        var result = await client.GetAsync(new SamsungDeviceInfoRequest
        {
            Host = "192.0.2.10"
        });

        Assert.False(result.IsSuccessStatusCode);
        Assert.Equal("TV sleeping", result.RawContent);
        Assert.Null(result.ParsedPayload);
        Assert.NotNull(result.ParseError);
    }

    [Fact]
    public async Task ProbeTimeoutIsReportedAsTimeoutInsteadOfCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}")
        {
            WaitForCancellation = true
        };
        using var httpClient = new HttpClient(handler);
        var client = new SamsungDeviceInfoClient(httpClient);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetAsync(new SamsungDeviceInfoRequest
            {
                Host = "192.0.2.10",
                Timeout = TimeSpan.FromMilliseconds(20)
            }));

        Assert.Contains("did not answer", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string content)
        : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public bool WaitForCancellation { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
