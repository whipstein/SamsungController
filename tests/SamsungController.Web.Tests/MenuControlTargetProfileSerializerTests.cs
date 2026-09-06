using SamsungController.Automation.Navigation;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class MenuControlTargetProfileSerializerTests
{
    [Fact]
    public void RoundTripPreservesAllConditionSetsAndIndexedValues()
    {
        var document = new MenuControlTargetProfile(2, "Combined", "test", null, null, null, DateTimeOffset.UtcNow, [],
        [
            new(new Dictionary<string, string> { ["format"] = "RGB", ["depth"] = "8-bit" }, [new("brightness", "25")]),
            new(new Dictionary<string, string> { ["format"] = "RGB", ["depth"] = "10-bit" }, [new("red", "3", "interval", "5%")])
        ]);
        var restored = MenuControlTargetProfileSerializer.Deserialize(MenuControlTargetProfileSerializer.Serialize(document));
        Assert.Empty(restored.Values);
        Assert.Equal(2, restored.ConditionValues!.Count);
        Assert.Equal("10-bit", restored.ConditionValues[1].Conditions["depth"]);
        Assert.Equal(document.ConditionValues![1].Values, restored.ConditionValues[1].Values);
    }

    [Theory]
    [InlineData("duplicate", "duplicated")]
    [InlineData("empty", "at least one value")]
    [InlineData("mixed", "without top-level values")]
    [InlineData("version", "version 2")]
    public void InvalidCombinedDocumentsAreRejected(string kind, string expected)
    {
        var set = new MenuControlConditionValues(new Dictionary<string, string> { ["format"] = "RGB", ["depth"] = "8-bit" }, [new("brightness", "25")]);
        var document = new MenuControlTargetProfile(2, "Combined", "test", null, null, null, DateTimeOffset.UtcNow, [], [set]);
        document = kind switch
        {
            "duplicate" => document with { ConditionValues = [set, set with { Conditions = new Dictionary<string, string> { ["DEPTH"] = "8-BIT", ["FORMAT"] = "rgb" } }] },
            "empty" => document with { ConditionValues = [set with { Values = [] }] },
            "mixed" => document with { Values = [new("brightness", "20")] },
            "version" => document with { Version = 1 },
            _ => document
        };
        var error = Assert.Throws<InvalidOperationException>(() => MenuControlTargetProfileSerializer.Serialize(document));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void RoundTripPreservesPortableTargetMetadataAndIndexedValues()
    {
        var document = new MenuControlTargetProfile(
            MenuControlTargetProfileSerializer.CurrentVersion,
            "Reference SDR calibration",
            "s95f-1296-sdr",
            "S95F 1296 SDR",
            "S95F",
            new MenuDefinitionContext("1296"),
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
