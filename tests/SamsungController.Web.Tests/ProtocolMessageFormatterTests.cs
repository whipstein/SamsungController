using System.Text.Json.Nodes;
using SamsungController.Core.Protocol;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class ProtocolMessageFormatterTests
{
    [Fact]
    public void RedactsTokensAtEveryObjectDepthByDefault()
    {
        var message = CreateMessage(
            """
            {
              "event": "ms.channel.connect",
              "data": {
                "token": "direct-secret",
                "clients": [
                  { "attributes": { "Token": "nested-secret" } }
                ]
              }
            }
            """);

        var formatted = ProtocolMessageFormatter.Format(message, revealSensitive: false);

        Assert.DoesNotContain("direct-secret", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", formatted, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(formatted, "[redacted]"));
    }

    [Fact]
    public void RevealsTokensOnlyWhenExplicitlyRequested()
    {
        var message = CreateMessage(
            """
            { "event": "ms.channel.connect", "data": { "token": "pairing-secret" } }
            """);

        var formatted = ProtocolMessageFormatter.Format(message, revealSensitive: true);

        Assert.Contains("pairing-secret", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingDoesNotMutateTheCapturedPayload()
    {
        var message = CreateMessage(
            """
            { "event": "ms.channel.connect", "data": { "token": "original" } }
            """);

        _ = ProtocolMessageFormatter.Format(message, revealSensitive: false);

        Assert.Equal("original", message.ParsedPayload?["data"]?["token"]?.GetValue<string>());
    }

    [Fact]
    public void RawDeviceInfoJsonUsesTheSameTokenRedactionPolicy()
    {
        const string rawJson = """{"device":{"token":"secret","name":"TV"}}""";

        var redacted = ProtocolMessageFormatter.FormatJson(rawJson, revealSensitive: false);
        var revealed = ProtocolMessageFormatter.FormatJson(rawJson, revealSensitive: true);

        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("secret", revealed, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsDeviceIdentifiersAndAddressesByDefault()
    {
        var message = CreateMessage(
            """
            {
              "device": {
                "duid": "uuid:00000000-1111-4222-8333-444444444444",
                "ip": "192.0.2.50",
                "ipv6": "fe80::1",
                "developerIP": "0.0.0.0",
                "wifiMac": "02:00:00:00:00:01"
              },
              "uri": "https://192.0.2.50:8002/api/v2/"
            }
            """);

        var formatted = ProtocolMessageFormatter.Format(
            message,
            revealSensitive: false,
            revealDeviceIdentifiers: false);

        Assert.DoesNotContain("00000000-1111", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.50", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("fe80::1", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("02:00:00", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted-uuid]", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted-mac]", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted-ip]", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceIdentifierRevealDoesNotRevealToken()
    {
        var message = CreateMessage(
            """
            {
              "token": "pairing-secret",
              "duid": "uuid:00000000-1111-4222-8333-444444444444",
              "ip": "192.0.2.50",
              "wifiMac": "02:00:00:00:00:01"
            }
            """);

        var formatted = ProtocolMessageFormatter.Format(
            message,
            revealSensitive: false,
            revealDeviceIdentifiers: true);

        Assert.DoesNotContain("pairing-secret", formatted, StringComparison.Ordinal);
        Assert.Contains("00000000-1111", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("192.0.2.50", formatted, StringComparison.Ordinal);
        Assert.Contains("02:00:00", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactsIpAddressInProtocolChannelText()
    {
        const string endpoint = "https://192.0.2.50:8002/api/v2/";

        var redacted = ProtocolMessageFormatter.FormatIdentifierText(
            endpoint,
            revealDeviceIdentifiers: false);
        var revealed = ProtocolMessageFormatter.FormatIdentifierText(
            endpoint,
            revealDeviceIdentifiers: true);

        Assert.Equal("https://[redacted-ip]:8002/api/v2/", redacted);
        Assert.Equal(endpoint, revealed);
    }

    private static SamsungMessage CreateMessage(string rawJson) => new(
        DateTimeOffset.UtcNow,
        SamsungMessageDirection.Rx,
        "samsung.remote.control",
        "ms.channel.connect",
        JsonNode.Parse(rawJson),
        rawJson,
        null,
        1);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
