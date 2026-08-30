using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.Protocol;
using YamlDotNet.RepresentationModel;

namespace SamsungController.Automation.Navigation;

public sealed class MenuDefinitionJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MenuDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new MenuDefinitionParseException(
                    "A JSON menu definition must contain one object at its document root.");
            }

            var yaml = new YamlStream(new YamlDocument(ToYaml(document.RootElement, "document root")));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            yaml.Save(writer, assignAnchors: false);
            return new MenuDefinitionParser().Parse(writer.ToString());
        }
        catch (MenuDefinitionParseException exception)
        {
            throw MenuDefinitionSourceDiagnostics.AddJsonLocation(json, exception);
        }
        catch (JsonException exception)
        {
            throw MenuDefinitionSourceDiagnostics.FromJsonSyntaxError(exception);
        }
    }

    public string Serialize(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        return CreateRoot(definition).ToJsonString(SerializerOptions) + Environment.NewLine;
    }

    private static YamlNode ToYaml(JsonElement element, string context) => element.ValueKind switch
    {
        JsonValueKind.Object => ToYamlMapping(element, context),
        JsonValueKind.Array => new YamlSequenceNode(element.EnumerateArray()
            .Select((item, index) => ToYaml(item, $"{context}[{index}]"))),
        JsonValueKind.String => new YamlScalarNode(element.GetString() ?? string.Empty),
        JsonValueKind.Number => new YamlScalarNode(element.GetRawText()),
        JsonValueKind.True => new YamlScalarNode("true"),
        JsonValueKind.False => new YamlScalarNode("false"),
        JsonValueKind.Null => new YamlScalarNode(string.Empty),
        _ => throw new MenuDefinitionParseException($"Unsupported JSON value in {context}.")
    };

    private static YamlMappingNode ToYamlMapping(JsonElement element, string context)
    {
        var mapping = new YamlMappingNode();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new MenuDefinitionParseException(
                    $"Field '{property.Name}' appears more than once in {context}.");
            }

            mapping.Add(
                new YamlScalarNode(property.Name),
                ToYaml(property.Value, $"'{property.Name}' in {context}"));
        }

        return mapping;
    }

    private static JsonObject CreateRoot(MenuDefinition definition)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["id"] = definition.Id,
            ["name"] = definition.Name,
            ["model"] = definition.Model,
            ["context"] = new JsonObject
            {
                ["firmware"] = definition.Context.Firmware,
                ["signal"] = definition.Context.Signal,
                ["pictureMode"] = definition.Context.PictureMode,
                ["input"] = definition.Context.Input
            }
        };
        if (definition.Verification is { } verification)
        {
            root["verification"] = CreateVerification(verification);
        }

        if (definition.Configurations.Count > 0)
        {
            root["configurations"] = new JsonArray(definition.Configurations.Values
                .Select(CreateConfiguration)
                .ToArray<JsonNode?>());
        }

        root["timing"] = new JsonObject
        {
            ["defaultDelay"] = $"{definition.Timing.DefaultDelayMilliseconds}ms",
            ["screenChangeDelay"] = $"{definition.Timing.ScreenChangeDelayMilliseconds}ms",
            ["returnDelay"] = $"{definition.Timing.ReturnDelayMilliseconds}ms",
            ["adjustmentDelay"] = $"{definition.Timing.AdjustmentDelayMilliseconds}ms",
            ["verified"] = definition.Timing.Verified
        };
        root["nodes"] = CreateNodes(definition);
        root["anchors"] = new JsonArray(definition.Anchors.Values
            .Select(CreateAnchor)
            .ToArray<JsonNode?>());
        root["transitions"] = new JsonArray(definition.Transitions.Values
            .Select(CreateTransition)
            .ToArray<JsonNode?>());
        return root;
    }

    private static JsonObject CreateVerification(MenuVerificationManifest verification) => new()
    {
        ["display"] = new JsonObject
        {
            ["model"] = verification.Display.Model,
            ["firmware"] = verification.Display.Firmware,
            ["signal"] = verification.Display.Signal,
            ["pictureMode"] = verification.Display.PictureMode,
            ["input"] = verification.Display.Input
        },
        ["checks"] = new JsonArray(verification.Checks
            .OrderBy(check => check.Id, StringComparer.OrdinalIgnoreCase)
            .Select(check => (JsonNode?)new JsonObject
            {
                ["id"] = check.Id,
                ["fingerprint"] = check.Fingerprint,
                ["verifiedAt"] = check.VerifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            })
            .ToArray())
    };

    private static JsonObject CreateConfiguration(MenuConfiguration configuration)
    {
        var result = new JsonObject
        {
            ["id"] = configuration.Id,
            ["name"] = configuration.Name
        };
        AddOptional(result, "conditions", configuration.Conditions);
        return result;
    }

    private static JsonArray CreateNodes(MenuDefinition definition)
    {
        var orderedNodes = definition.Nodes.Values.ToArray();
        var childrenByParent = orderedNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.ParentId))
            .GroupBy(node => node.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new JsonArray(orderedNodes
            .Where(node => string.IsNullOrWhiteSpace(node.ParentId))
            .Select(node => (JsonNode?)CreateNode(node, childrenByParent))
            .ToArray());
    }

    private static JsonObject CreateNode(
        MenuNode node,
        IReadOnlyDictionary<string, MenuNode[]> childrenByParent)
    {
        var result = new JsonObject
        {
            ["id"] = node.Id,
            ["label"] = node.Label,
            ["controlType"] = FormatControlType(node.ControlType)
        };
        AddOptional(result, "description", node.Description);
        AddOptional(result, "defaultValue", node.DefaultValue);
        if (node.MinimumValue is { } minimum)
        {
            result["minimumValue"] = minimum;
        }
        if (node.MaximumValue is { } maximum)
        {
            result["maximumValue"] = maximum;
        }
        if (node.Disabled)
        {
            result["disabled"] = true;
        }
        if (node.SelectionOptions is { Count: > 0 })
        {
            result["options"] = new JsonArray(node.SelectionOptions
                .Select(option => (JsonNode?)JsonValue.Create(option))
                .ToArray());
        }
        if (node.DisabledWhen is { Count: > 0 })
        {
            result["disabledWhen"] = CreateConditions(node.DisabledWhen.Select(condition =>
                (condition.SettingNodeId, condition.EqualsValue)));
        }
        if (node.HiddenWhen is { Count: > 0 })
        {
            result["hiddenWhen"] = CreateConditions(node.HiddenWhen.Select(condition =>
                (condition.SettingNodeId, condition.EqualsValue)));
        }
        if (childrenByParent.TryGetValue(node.Id, out var children))
        {
            result["children"] = new JsonArray(children
                .Select(child => (JsonNode?)CreateNode(child, childrenByParent))
                .ToArray());
        }
        return result;
    }

    private static JsonArray CreateConditions(IEnumerable<(string Setting, string ExpectedValue)> conditions) =>
        new(conditions.Select(condition => (JsonNode?)new JsonObject
        {
            ["setting"] = condition.Setting,
            ["equals"] = condition.ExpectedValue
        }).ToArray());

    private static JsonObject CreateAnchor(MenuAnchor anchor)
    {
        var result = new JsonObject
        {
            ["id"] = anchor.Id,
            ["label"] = anchor.Label,
            ["target"] = anchor.TargetNodeId,
            ["verified"] = anchor.Verified
        };
        AddOptional(result, "configuration", anchor.ConfigurationId);
        AddOptional(result, "description", anchor.Description);
        AddOptional(result, "validationSource", anchor.ValidationSourceNodeId);
        if (anchor.ReturnStrategy is { } strategy)
        {
            result["returnStrategy"] = CreateReturnStrategy(strategy);
        }
        result["steps"] = CreateOperations(anchor.Operations);
        return result;
    }

    private static JsonObject CreateReturnStrategy(MenuReturnStrategy strategy)
    {
        var result = new JsonObject
        {
            ["menuRoot"] = strategy.MenuRootNodeId,
            ["atMenuRoot"] = CreateReturnScript(strategy.AtMenuRoot),
            ["belowMenuRoot"] = CreateReturnScript(strategy.BelowMenuRoot)
        };
        if (strategy.NodeOverrides is { Count: > 0 })
        {
            result["overrides"] = new JsonArray(strategy.NodeOverrides
                .Select(item => (JsonNode?)new JsonObject
                {
                    ["node"] = item.NodeId,
                    ["verified"] = item.Script.Verified,
                    ["steps"] = CreateOperations(item.Script.Operations)
                })
                .ToArray());
        }
        return result;
    }

    private static JsonObject CreateReturnScript(MenuReturnScript script) => new()
    {
        ["verified"] = script.Verified,
        ["steps"] = CreateOperations(script.Operations)
    };

    private static JsonObject CreateTransition(MenuTransition transition)
    {
        var result = new JsonObject
        {
            ["id"] = transition.Id,
            ["from"] = transition.FromNodeId,
            ["to"] = transition.ToNodeId,
            ["verified"] = transition.Verified
        };
        AddOptional(result, "configuration", transition.ConfigurationId);
        AddOptional(result, "description", transition.Description);
        if (transition.GeneratedFromTopology)
        {
            result["generatedFromTopology"] = true;
            AddOptional(result, "topologySeed", transition.TopologySeedTransitionId);
            AddOptional(result, "validationGroup", transition.ValidationGroupId);
            result["validationRoute"] = transition.IsValidationRoute;
        }
        if (transition.ReturnToVideoOperations is { Count: > 0 } returnOperations)
        {
            result["returnSteps"] = CreateOperations(returnOperations);
        }
        result["steps"] = CreateOperations(transition.Operations);
        return result;
    }

    private static JsonArray CreateOperations(IEnumerable<MenuOperation> operations) =>
        new(operations.Select(operation =>
        {
            var result = new JsonObject { ["key"] = operation.Key };
            if (operation.Action != RemoteKeyAction.Click)
            {
                result["action"] = operation.Action.ToString();
            }
            if (operation.Repeat != 1)
            {
                result["repeat"] = operation.Repeat;
            }
            if (operation.DelayAfter is { } delay)
            {
                result["delay"] = $"{delay.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms";
            }
            return (JsonNode?)result;
        }).ToArray());

    private static string FormatControlType(MenuControlType controlType) => controlType switch
    {
        MenuControlType.Submenu => "submenu",
        MenuControlType.Slider => "slider",
        MenuControlType.Selection => "selection",
        MenuControlType.SubmenuSelection => "submenu-selection",
        MenuControlType.IndexedSelection => "indexed-selection",
        MenuControlType.Switch => "switch",
        MenuControlType.Confirmation => "confirmation",
        MenuControlType.Action => "action",
        _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, null)
    };

    private static void AddOptional(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }
}
