using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;
using SamsungController.Core.Devices;
using SamsungController.Core.Protocol;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class DeviceInfoIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.Web.DeviceInfoTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProbeUsesSavedConnectionProfileAndRecordsLabeledResponse()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "settings.json"),
            JsonSerializer.Serialize(new
            {
                Host = "tv.local",
                Secure = false,
                Port = 9123,
                AllowUntrustedCertificate = false
            }));
        var deviceInfoClient = new RecordingDeviceInfoClient();
        await using var controller = CreateController(deviceInfoClient);

        var observation = await controller.ProbeDeviceInfoAsync("Netflix SDR");

        Assert.Equal("Netflix SDR", observation.Label);
        Assert.Equal(200, observation.StatusCode);
        Assert.True(observation.IsSuccess);
        Assert.Null(observation.Error);
        Assert.Equal("tv.local", deviceInfoClient.LastRequest?.Host);
        Assert.False(deviceInfoClient.LastRequest?.Secure);
        Assert.Equal(9123, deviceInfoClient.LastRequest?.Port);
        Assert.False(deviceInfoClient.LastRequest?.AllowUntrustedCertificate);

        var snapshot = controller.GetDeviceInfoSnapshot();
        Assert.False(snapshot.IsQuerying);
        Assert.Equal(observation, Assert.Single(snapshot.Observations));
        Assert.Collection(
            controller.GetMessages(),
            message =>
            {
                Assert.Equal(SamsungMessageDirection.Tx, message.Direction);
                Assert.Contains("GET /api/v2/", message.Event, StringComparison.Ordinal);
            },
            message =>
            {
                Assert.Equal(SamsungMessageDirection.Rx, message.Direction);
                Assert.Equal("Living Room", message.ParsedPayload?["device"]?["name"]?.GetValue<string>());
            });

        var captureLines = await File.ReadAllLinesAsync(controller.ProtocolLogPath);
        Assert.Equal(2, captureLines.Length);
    }

    [Fact]
    public async Task NetworkFailureIsRetainedAsAnObservation()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "settings.json"),
            JsonSerializer.Serialize(new { Host = "192.0.2.10" }));
        var deviceInfoClient = new RecordingDeviceInfoClient
        {
            Failure = new HttpRequestException("TV did not answer")
        };
        await using var controller = CreateController(deviceInfoClient);

        var observation = await controller.ProbeDeviceInfoAsync("Standby");

        Assert.Equal("Standby", observation.Label);
        Assert.False(observation.IsSuccess);
        Assert.Null(observation.StatusCode);
        Assert.Equal("TV did not answer", observation.Error);
        Assert.Single(controller.GetDeviceInfoSnapshot().Observations);
        Assert.Single(controller.GetMessages());
    }

    private SamsungControllerService CreateController(ISamsungDeviceInfoClient deviceInfoClient)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SamsungController:ConfigurationDirectory"] = _directory
            })
            .Build();
        return new SamsungControllerService(
            configuration,
            new IdleSamsungTransport(),
            SystemMenuDelay.Instance,
            deviceInfoClient);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingDeviceInfoClient : ISamsungDeviceInfoClient
    {
        public SamsungDeviceInfoRequest? LastRequest { get; private set; }

        public Exception? Failure { get; init; }

        public Task<SamsungDeviceInfoResponse> GetAsync(
            SamsungDeviceInfoRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Failure is not null)
            {
                return Task.FromException<SamsungDeviceInfoResponse>(Failure);
            }

            const string rawJson = """{"device":{"name":"Living Room"}}""";
            return Task.FromResult(new SamsungDeviceInfoResponse(
                DateTimeOffset.UtcNow,
                SamsungDeviceInfoClient.GetEndpoint(request),
                HttpStatusCode.OK,
                JsonNode.Parse(rawJson),
                rawJson,
                null));
        }
    }

    private sealed class IdleSamsungTransport : ISamsungTransport
    {
        public bool IsConnected => false;

        public long Generation => 0;

        public Task ConnectAsync(
            Uri endpoint,
            TimeSpan timeout,
            bool allowUntrustedCertificate,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(string rawJson, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<string> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
