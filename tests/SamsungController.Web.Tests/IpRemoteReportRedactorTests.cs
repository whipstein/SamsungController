using System.Text.Json.Nodes;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteReportRedactorTests
{
    [Fact]
    public void RedactsNestedFieldsEmbeddedJsonAndCaseOrSeparatorVariantsInEchoes()
    {
        var pin = string.Concat(Enumerable.Repeat("Ab12", 16));
        var colonPin = string.Join(":", Enumerable.Range(0, 32).Select(index => pin.Substring(index * 2, 2)));
        var spacedPin = colonPin.Replace(':', ' ');
        var original = new JsonObject
        {
            ["ResponseJson"] = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "3",
                ["result"] = new JsonObject
                {
                    ["certificateSha256"] = colonPin,
                    ["echo"] = pin.ToUpperInvariant(),
                    ["brightness"] = 0
                }
            }.ToJsonString(),
            ["annotations"] = new JsonArray($"pin {pin.ToLowerInvariant()}", colonPin.ToUpperInvariant(), spacedPin),
            ["nested"] = new JsonArray(new JsonObject { ["NormalizedCertificatePin"] = pin }),
            ["ObservedCertificateSha256"] = null
        };
        var json = IpRemoteReportRedactor.RedactCertificateFingerprints(original.ToJsonString());
        Assert.DoesNotContain(pin, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(colonPin, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(spacedPin, json, StringComparison.OrdinalIgnoreCase);
        var result = JsonNode.Parse(json)!;
        var reply = JsonNode.Parse(result["ResponseJson"]!.GetValue<string>())!;
        Assert.Equal(IpRemoteReportRedactor.CertificateMarker, reply["result"]!["certificateSha256"]!.GetValue<string>());
        Assert.Equal(IpRemoteReportRedactor.CertificateMarker, reply["result"]!["echo"]!.GetValue<string>());
        Assert.Equal(0, reply["result"]!["brightness"]!.GetValue<int>());
        Assert.Equal("2.0", reply["jsonrpc"]!.GetValue<string>());
        Assert.Equal("3", reply["id"]!.GetValue<string>());
        Assert.Null(result["ObservedCertificateSha256"]);
        Assert.Equal(pin, original["nested"]![0]!["NormalizedCertificatePin"]!.GetValue<string>());
    }

    [Fact]
    public void NullFingerprintsAndOmittedResponseBodiesRemainIntact()
    {
        const string json = """{"CertificateSha256":null,"NormalizedCertificatePin":null,"ResponseJson":"[Malformed response omitted for credential safety.]"}""";
        var result = JsonNode.Parse(IpRemoteReportRedactor.RedactCertificateFingerprints(json))!;
        Assert.Null(result["CertificateSha256"]);
        Assert.Null(result["NormalizedCertificatePin"]);
        Assert.Equal("[Malformed response omitted for credential safety.]", result["ResponseJson"]!.GetValue<string>());
    }
}
