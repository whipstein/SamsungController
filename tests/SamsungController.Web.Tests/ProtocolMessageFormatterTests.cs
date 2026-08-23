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
