using SamsungController.Automation.Navigation;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class MenuControlTargetProfileSerializerTests
{
    [Fact]
    public void RoundTripPreservesPortableTargetMetadataAndIndexedValues()
    {
        var document = new MenuControlTargetProfile(
            MenuControlTargetProfileSerializer.CurrentVersion,
            "Reference SDR calibration",
            "s95f-1296-sdr",
            "S95F 1296 SDR",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Filmmaker Mode", "Home Theater System"),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            [
                new MenuControlProfileValue("brightness", "25"),
                new MenuControlProfileValue(
                    "white-balance-20-point-red",
                    "2",
                    "white-balance-20-point-interval",
                    "5%")
            ]);

        var json = MenuControlTargetProfileSerializer.Serialize(document);
        var restored = MenuControlTargetProfileSerializer.Deserialize(json);

        Assert.Equal(document.Name, restored.Name);
        Assert.Equal(document.DefinitionId, restored.DefinitionId);
        Assert.Equal(document.Context, restored.Context);
        Assert.Equal(document.Values, restored.Values);
        Assert.Contains("\"definitionId\": \"s95f-1296-sdr\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectorValue\": \"5%\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonReportsOneBasedLineAndColumn()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MenuControlTargetProfileSerializer.Deserialize(
                "{\n  \"version\": 1,\n  \"name\": ]\n}"));

        Assert.Contains("line 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateTargetKeysAreRejectedBeforeStaging()
    {
        var document = new MenuControlTargetProfile(
            1,
            "Duplicate",
            "test",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            [
                new MenuControlProfileValue("brightness", "1"),
                new MenuControlProfileValue("Brightness", "2")
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MenuControlTargetProfileSerializer.Serialize(document));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
