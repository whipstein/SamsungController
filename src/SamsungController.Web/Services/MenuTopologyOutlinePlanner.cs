using System.Globalization;
using System.Text;
using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Services;

internal sealed record MenuTopologyOutlinePlan(
    IReadOnlyList<MenuNode> Nodes,
    MenuTopologyOutlinePreview Preview);

internal static class MenuTopologyOutlinePlanner
{
    private const int MaximumOutlineNodes = 500;
    private const int MaximumLabelLength = 200;

    public static MenuTopologyOutlinePlan Create(
        MenuDefinition definition,
        MenuTopologyOutlineRequest request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        var parentNodeId = request.ParentNodeId?.Trim();
        if (string.IsNullOrWhiteSpace(parentNodeId))
        {
            throw new InvalidOperationException("Choose where the outline belongs in the menu tree.");
        }

        definition.GetRequiredNode(parentNodeId);
        var entries = ParseOutline(request.Outline);
        var existingNodes = definition.Nodes.Values.ToArray();
        var nodesById = new Dictionary<string, MenuNode>(
            definition.Nodes,
            StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(definition.Nodes.Keys, StringComparer.OrdinalIgnoreCase);
        var plannedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedChildren = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var resolvedByDepth = new Dictionary<int, MenuNode>();
        var changes = new List<string>();
        var addedCount = 0;
        var updatedCount = 0;

        foreach (var entry in entries)
        {
            var resolvedParentId = entry.Depth == 0
                ? parentNodeId
                : resolvedByDepth[entry.Depth - 1].Id;
            var existing = ResolveExistingNode(
                definition,
                entry,
                resolvedParentId,
                plannedIds);
            var nodeId = existing?.Id
                ?? (entry.ExplicitId is not null
                    ? entry.ExplicitId
                    : CreateAvailableId(entry.Label, resolvedParentId, usedIds));
            if (!plannedIds.Add(nodeId))
            {
                throw new InvalidOperationException(
                    $"Outline line {entry.LineNumber} uses node ID '{nodeId}' more than once. Add an explicit [stable-id] to distinguish repeated labels.");
            }

            usedIds.Add(nodeId);
            var controlType = entry.ControlType ?? existing?.ControlType ?? MenuControlType.Submenu;
            var defaultValue = entry.DefaultValueSpecified
                ? entry.DefaultValue
                : entry.ControlType is MenuControlType.Submenu or MenuControlType.Action
                    ? null
                    : existing?.DefaultValue;
            var disabledWhen = entry.DisabledWhenSpecified
                ? entry.DisabledWhen
                : entry.DisabledSpecified && entry.Disabled
                    ? []
                    : existing?.DisabledWhen ?? [];
            var disabled = entry.DisabledSpecified
                ? entry.Disabled
                : entry.DisabledWhenSpecified
                    ? false
                    : existing?.Disabled ?? false;
            var hiddenWhen = entry.HiddenWhenSpecified
                ? entry.HiddenWhen
                : existing?.HiddenWhen ?? [];
            var selectionOptions = entry.SelectionOptionsSpecified
                ? entry.SelectionOptions
                : entry.ControlType is not null
                  && entry.ControlType is not MenuControlType.Selection
                      and not MenuControlType.SubmenuSelection
                      and not MenuControlType.IndexedSelection
                      and not MenuControlType.Confirmation
                    ? []
                    : existing?.SelectionOptions ?? [];
            var minimumValue = entry.MinimumValueSpecified
                ? entry.MinimumValue
                : entry.ControlType is not null && entry.ControlType != MenuControlType.Slider
                    ? null
                    : existing?.MinimumValue;
            var maximumValue = entry.MaximumValueSpecified
                ? entry.MaximumValue
                : entry.ControlType is not null && entry.ControlType != MenuControlType.Slider
                    ? null
                    : existing?.MaximumValue;
            var node = new MenuNode(
                nodeId,
                entry.Label,
                resolvedParentId,
                existing?.Description,
                controlType,
                defaultValue,
                disabledWhen,
                selectionOptions,
                minimumValue,
                maximumValue,
                hiddenWhen,
                disabled);
            nodesById[node.Id] = node;
            if (!plannedChildren.TryGetValue(resolvedParentId, out var children))
            {
                children = [];
                plannedChildren.Add(resolvedParentId, children);
            }

            children.Add(node.Id);
            resolvedByDepth[entry.Depth] = node;
            foreach (var staleDepth in resolvedByDepth.Keys.Where(depth => depth > entry.Depth).ToArray())
            {
                resolvedByDepth.Remove(staleDepth);
            }

            if (existing is null)
            {
                addedCount++;
                changes.Add($"Add {BuildPath(nodesById, node.Id)} [{node.Id}]");
            }
            else if (!existing.Label.Equals(node.Label, StringComparison.Ordinal)
                     || !string.Equals(existing.ParentId, node.ParentId, StringComparison.OrdinalIgnoreCase)
                     || existing.ControlType != node.ControlType
                     || !string.Equals(existing.DefaultValue, node.DefaultValue, StringComparison.Ordinal)
                     || existing.MinimumValue != node.MinimumValue
                     || existing.MaximumValue != node.MaximumValue
                     || existing.Disabled != node.Disabled
                     || !SequenceEqual(existing.SelectionOptions, node.SelectionOptions)
                     || !ConditionsEqual(existing.DisabledWhen, node.DisabledWhen)
                     || !HiddenConditionsEqual(existing.HiddenWhen, node.HiddenWhen))
            {
                updatedCount++;
                changes.Add($"Update {BuildPath(nodesById, node.Id)} [{node.Id}]");
            }
        }

        var removedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!request.KeepUnlistedNodes)
        {
            removedIds = GetDescendantIds(definition, parentNodeId);
            removedIds.ExceptWith(plannedIds);
            EnsureNodesCanBeRemoved(definition, removedIds);
            foreach (var removedId in removedIds)
            {
                changes.Add($"Remove {definition.GetPath(removedId)} [{removedId}]");
                nodesById.Remove(removedId);
            }
        }

        var finalNodes = OrderNodes(
            existingNodes,
            nodesById,
            plannedChildren);
        var reorderedLevelCount = CountReorderedLevels(
            existingNodes,
            finalNodes,
            plannedChildren.Keys);
        if (reorderedLevelCount > 0)
        {
            changes.Add($"Reorder {reorderedLevelCount} menu level(s) to match the outline");
        }

        var candidate = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            finalNodes,
            definition.Transitions.Values,
            definition.Anchors.Values,
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId,
            definition.Verification);
        new MenuDefinitionValidator().ValidateAndThrow(candidate);

        var preview = new MenuTopologyOutlinePreview(
            entries.Count,
            addedCount,
            updatedCount,
            removedIds.Count,
            reorderedLevelCount,
            finalNodes.Count,
            changes);
        return new MenuTopologyOutlinePlan(finalNodes, preview);
    }

    private static IReadOnlyList<OutlineEntry> ParseOutline(string? outline)
    {
        if (string.IsNullOrWhiteSpace(outline))
        {
            throw new InvalidOperationException(
                "Enter at least one menu item. Use two spaces per level to show parent and child menus.");
        }

        var result = new List<OutlineEntry>();
        var lines = outline.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains('\t'))
            {
                throw new InvalidOperationException(
                    $"Outline line {index + 1} contains a tab. Use two spaces for each menu level.");
            }

            var indentation = line.Length - line.TrimStart(' ').Length;
            if (indentation % 2 != 0)
            {
                throw new InvalidOperationException(
                    $"Outline line {index + 1} has {indentation} leading spaces. Use exactly two spaces per menu level.");
            }

            var depth = indentation / 2;
            if (result.Count == 0 && depth != 0)
            {
                throw new InvalidOperationException(
                    $"Outline line {index + 1} must start at the first level with no leading spaces.");
            }

            if (result.Count > 0 && depth > result[^1].Depth + 1)
            {
                throw new InvalidOperationException(
                    $"Outline line {index + 1} skips a menu level. Indent only two more spaces than the preceding parent.");
            }

            var content = line.Trim();
            if (content.StartsWith("- ", StringComparison.Ordinal))
            {
                content = content[2..].Trim();
            }

            result.Add(ParseOutlineEntry(content, index + 1, depth));
            if (result.Count > MaximumOutlineNodes)
            {
                throw new InvalidOperationException(
                    $"An outline can contain at most {MaximumOutlineNodes} menu items.");
            }
        }

        return result;
    }

    private static OutlineEntry ParseOutlineEntry(
        string content,
        int lineNumber,
        int depth)
    {
        string? explicitId = null;
        var label = content;
        if (content.EndsWith(']'))
        {
            var openingBracket = content.LastIndexOf(" [", StringComparison.Ordinal);
            if (openingBracket >= 0)
            {
                explicitId = content[(openingBracket + 2)..^1].Trim();
                label = content[..openingBracket].Trim();
                if (string.IsNullOrWhiteSpace(explicitId))
                {
                    throw new InvalidOperationException(
                        $"Outline line {lineNumber} has an empty stable ID.");
                }
            }
        }

        MenuControlType? controlType = null;
        var defaultValueSpecified = false;
        string? defaultValue = null;
        var disabledSpecified = false;
        var disabled = false;
        var disabledWhenSpecified = false;
        IReadOnlyList<MenuNodeDisabledCondition> disabledWhen = [];
        var hiddenWhenSpecified = false;
        IReadOnlyList<MenuNodeHiddenCondition> hiddenWhen = [];
        var selectionOptionsSpecified = false;
        IReadOnlyList<string> selectionOptions = [];
        var minimumValueSpecified = false;
        decimal? minimumValue = null;
        var maximumValueSpecified = false;
        decimal? maximumValue = null;
        if (label.EndsWith('}'))
        {
            var openingBrace = label.LastIndexOf(" {", StringComparison.Ordinal);
            if (openingBrace < 0)
            {
                throw new InvalidOperationException(
                    $"Outline line {lineNumber} has control details without a preceding space.");
            }

            var metadata = label[(openingBrace + 2)..^1];
            label = label[..openingBrace].Trim();
            foreach (var rawPart in metadata.Split(';', StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(rawPart))
                {
                    continue;
                }

                if (rawPart.StartsWith("default=", StringComparison.OrdinalIgnoreCase))
                {
                    defaultValueSpecified = true;
                    defaultValue = rawPart["default=".Length..].Trim();
                    continue;
                }

                if (rawPart.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    disabledSpecified = true;
                    disabled = true;
                    continue;
                }

                if (rawPart.StartsWith("disabled=", StringComparison.OrdinalIgnoreCase))
                {
                    disabledSpecified = true;
                    if (!bool.TryParse(rawPart["disabled=".Length..].Trim(), out disabled))
                    {
                        throw new InvalidOperationException(
                            $"Outline line {lineNumber} disabled must be true or false.");
                    }

                    continue;
                }

                if (rawPart.StartsWith("disabledWhen=", StringComparison.OrdinalIgnoreCase))
                {
                    disabledWhenSpecified = true;
                    disabledWhen = ParseDisabledConditions(
                        rawPart["disabledWhen=".Length..],
                        lineNumber);
                    continue;
                }

                if (rawPart.StartsWith("hiddenWhen=", StringComparison.OrdinalIgnoreCase))
                {
                    hiddenWhenSpecified = true;
                    hiddenWhen = ParseHiddenConditions(
                        rawPart["hiddenWhen=".Length..],
                        lineNumber);
                    continue;
                }

                if (rawPart.StartsWith("options=", StringComparison.OrdinalIgnoreCase))
                {
                    selectionOptionsSpecified = true;
                    selectionOptions = rawPart["options=".Length..]
                        .Split('|', StringSplitOptions.TrimEntries);
                    continue;
                }

                if (rawPart.StartsWith("min=", StringComparison.OrdinalIgnoreCase))
                {
                    minimumValueSpecified = true;
                    minimumValue = ParseDecimalMetadata(
                        rawPart["min=".Length..],
                        "minimum",
                        lineNumber);
                    continue;
                }

                if (rawPart.StartsWith("max=", StringComparison.OrdinalIgnoreCase))
                {
                    maximumValueSpecified = true;
                    maximumValue = ParseDecimalMetadata(
                        rawPart["max=".Length..],
                        "maximum",
                        lineNumber);
                    continue;
                }

                var typeValue = rawPart.StartsWith("type=", StringComparison.OrdinalIgnoreCase)
                    ? rawPart["type=".Length..]
                    : rawPart;
                controlType = ParseControlType(typeValue, lineNumber);
            }
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new InvalidOperationException(
                $"Outline line {lineNumber} needs a menu-item name.");
        }

        if (label.Length > MaximumLabelLength)
        {
            throw new InvalidOperationException(
                $"Outline line {lineNumber} exceeds the {MaximumLabelLength}-character menu-item limit.");
        }

        return new OutlineEntry(
            lineNumber,
            depth,
            label,
            explicitId,
            controlType,
            defaultValueSpecified,
            string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue,
            disabledSpecified,
            disabled,
            disabledWhenSpecified,
            disabledWhen,
            hiddenWhenSpecified,
            hiddenWhen,
            selectionOptionsSpecified,
            selectionOptions,
            minimumValueSpecified,
            minimumValue,
            maximumValueSpecified,
            maximumValue);
    }

    private static decimal ParseDecimalMetadata(
        string value,
        string name,
        int lineNumber) => decimal.TryParse(
            value.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
        ? result
        : throw new InvalidOperationException(
            $"Outline line {lineNumber} {name} value must be a number.");

    private static MenuControlType ParseControlType(string value, int lineNumber)
    {
        var normalized = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<MenuControlType>(normalized, ignoreCase: true, out var result)
               && Enum.IsDefined(result)
            ? result
            : throw new InvalidOperationException(
                $"Outline line {lineNumber} control type must be submenu, slider, selection, submenu-selection, indexed-selection, switch, confirmation, or action.");
    }

    private static IReadOnlyList<MenuNodeDisabledCondition> ParseDisabledConditions(
        string value,
        int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new List<MenuNodeDisabledCondition>();
        foreach (var rawCondition in value.Split('|', StringSplitOptions.TrimEntries))
        {
            var equals = rawCondition.IndexOf('=');
            if (equals <= 0 || equals == rawCondition.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Outline line {lineNumber} disabledWhen entries must use setting-id=value.");
            }

            result.Add(new MenuNodeDisabledCondition(
                rawCondition[..equals].Trim(),
                rawCondition[(equals + 1)..].Trim()));
        }

        return result;
    }

    private static IReadOnlyList<MenuNodeHiddenCondition> ParseHiddenConditions(
        string value,
        int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new List<MenuNodeHiddenCondition>();
        foreach (var rawCondition in value.Split('|', StringSplitOptions.TrimEntries))
        {
            var equals = rawCondition.IndexOf('=');
            if (equals <= 0 || equals == rawCondition.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Outline line {lineNumber} hiddenWhen entries must use setting-id=value.");
            }

            result.Add(new MenuNodeHiddenCondition(
                rawCondition[..equals].Trim(),
                rawCondition[(equals + 1)..].Trim()));
        }

        return result;
    }

    private static MenuNode? ResolveExistingNode(
        MenuDefinition definition,
        OutlineEntry entry,
        string parentNodeId,
        IReadOnlySet<string> plannedIds)
    {
        if (entry.ExplicitId is not null)
        {
            return definition.Nodes.TryGetValue(entry.ExplicitId, out var explicitNode)
                ? explicitNode
                : null;
        }

        var matches = definition.Nodes.Values
            .Where(node => !plannedIds.Contains(node.Id)
                && string.Equals(node.ParentId, parentNodeId, StringComparison.OrdinalIgnoreCase)
                && node.Label.Equals(entry.Label, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Outline line {entry.LineNumber} matches more than one '{entry.Label}' item under the same parent. Add [stable-id] to that line.")
        };
    }

    private static string CreateAvailableId(
        string label,
        string parentNodeId,
        IReadOnlySet<string> usedIds)
    {
        var labelId = CreateIdPart(label);
        if (!usedIds.Contains(labelId))
        {
            return labelId;
        }

        var parentId = $"{CreateIdPart(parentNodeId)}-{labelId}";
        if (!usedIds.Contains(parentId))
        {
            return parentId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{parentId}-{suffix}";
            if (!usedIds.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string CreateIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var result = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "menu-node";
        }

        return char.IsLetter(result[0]) || result[0] == '_'
            ? result
            : $"node-{result}";
    }

    private static HashSet<string> GetDescendantIds(
        MenuDefinition definition,
        string parentNodeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in definition.Nodes.Values)
            {
                if (node.ParentId is not null
                    && (node.ParentId.Equals(parentNodeId, StringComparison.OrdinalIgnoreCase)
                        || result.Contains(node.ParentId))
                    && result.Add(node.Id))
                {
                    changed = true;
                }
            }
        }

        return result;
    }

    private static void EnsureNodesCanBeRemoved(
        MenuDefinition definition,
        IReadOnlySet<string> removedIds)
    {
        if (removedIds.Count == 0)
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in definition.Transitions.Values)
        {
            if (removedIds.Contains(transition.FromNodeId))
            {
                referenced.Add(transition.FromNodeId);
            }

            if (removedIds.Contains(transition.ToNodeId))
            {
                referenced.Add(transition.ToNodeId);
            }
        }

        foreach (var anchor in definition.Anchors.Values)
        {
            if (removedIds.Contains(anchor.TargetNodeId))
            {
                referenced.Add(anchor.TargetNodeId);
            }

            if (anchor.ValidationSourceNodeId is { } source && removedIds.Contains(source))
            {
                referenced.Add(source);
            }

            if (anchor.ReturnStrategy is not { } strategy)
            {
                continue;
            }

            if (removedIds.Contains(strategy.MenuRootNodeId))
            {
                referenced.Add(strategy.MenuRootNodeId);
            }

            foreach (var item in strategy.NodeOverrides ?? [])
            {
                if (removedIds.Contains(item.NodeId))
                {
                    referenced.Add(item.NodeId);
                }
            }
        }

        if (referenced.Count > 0)
        {
            var paths = referenced
                .Select(definition.GetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                "The outline would remove menu items used by recorded routes or return scripts: "
                + string.Join(", ", paths)
                + ". Keep unlisted items, or remove those recordings first.");
        }
    }

    private static IReadOnlyList<MenuNode> OrderNodes(
        IReadOnlyList<MenuNode> existingNodes,
        IReadOnlyDictionary<string, MenuNode> finalNodesById,
        IReadOnlyDictionary<string, List<string>> plannedChildren)
    {
        var result = new List<MenuNode>(finalNodesById.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<MenuNode> GetChildren(string? parentId)
        {
            var orderedIds = new List<string>();
            if (parentId is not null && plannedChildren.TryGetValue(parentId, out var planned))
            {
                orderedIds.AddRange(planned.Where(finalNodesById.ContainsKey));
            }

            orderedIds.AddRange(existingNodes
                .Where(node => finalNodesById.TryGetValue(node.Id, out var final)
                    && string.Equals(final.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.Id)
                .Where(id => !orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
            orderedIds.AddRange(finalNodesById.Values
                .Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.Id)
                .Where(id => !orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
            return orderedIds.Select(id => finalNodesById[id]);
        }

        void Append(MenuNode node)
        {
            if (!visited.Add(node.Id))
            {
                return;
            }

            result.Add(node);
            foreach (var child in GetChildren(node.Id))
            {
                Append(child);
            }
        }

        foreach (var root in GetChildren(null))
        {
            Append(root);
        }

        foreach (var node in finalNodesById.Values)
        {
            Append(node);
        }

        return result;
    }

    private static int CountReorderedLevels(
        IReadOnlyList<MenuNode> existingNodes,
        IReadOnlyList<MenuNode> finalNodes,
        IEnumerable<string> plannedParentIds)
    {
        var count = 0;
        foreach (var parentId in plannedParentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = existingNodes
                .Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.Id)
                .Where(id => finalNodes.Any(node => node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var after = finalNodes
                .Where(node => string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.Id)
                .Where(id => before.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static string BuildPath(
        IReadOnlyDictionary<string, MenuNode> nodes,
        string nodeId)
    {
        var labels = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = nodes[nodeId];
        while (visited.Add(current.Id))
        {
            labels.Push(current.Label);
            if (current.ParentId is null || !nodes.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return string.Join(" / ", labels);
    }

    private static bool ConditionsEqual(
        IReadOnlyList<MenuNodeDisabledCondition>? left,
        IReadOnlyList<MenuNodeDisabledCondition>? right)
    {
        var leftItems = left ?? [];
        var rightItems = right ?? [];
        return leftItems.Count == rightItems.Count
               && leftItems.Zip(rightItems).All(pair =>
                   pair.First.SettingNodeId.Equals(
                       pair.Second.SettingNodeId,
                       StringComparison.OrdinalIgnoreCase)
                   && pair.First.EqualsValue.Equals(
                       pair.Second.EqualsValue,
                       StringComparison.Ordinal));
    }

    private static bool HiddenConditionsEqual(
        IReadOnlyList<MenuNodeHiddenCondition>? left,
        IReadOnlyList<MenuNodeHiddenCondition>? right)
    {
        var leftItems = left ?? [];
        var rightItems = right ?? [];
        return leftItems.Count == rightItems.Count
               && leftItems.Zip(rightItems).All(pair =>
                   pair.First.SettingNodeId.Equals(
                       pair.Second.SettingNodeId,
                       StringComparison.OrdinalIgnoreCase)
                   && pair.First.EqualsValue.Equals(
                       pair.Second.EqualsValue,
                       StringComparison.Ordinal));
    }

    private static bool SequenceEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        var leftItems = left ?? [];
        var rightItems = right ?? [];
        return leftItems.Count == rightItems.Count
               && leftItems.Zip(rightItems).All(pair => pair.First.Equals(
                   pair.Second,
                   StringComparison.Ordinal));
    }

    private sealed record OutlineEntry(
        int LineNumber,
        int Depth,
        string Label,
        string? ExplicitId,
        MenuControlType? ControlType,
        bool DefaultValueSpecified,
        string? DefaultValue,
        bool DisabledSpecified,
        bool Disabled,
        bool DisabledWhenSpecified,
        IReadOnlyList<MenuNodeDisabledCondition> DisabledWhen,
        bool HiddenWhenSpecified,
        IReadOnlyList<MenuNodeHiddenCondition> HiddenWhen,
        bool SelectionOptionsSpecified,
        IReadOnlyList<string> SelectionOptions,
        bool MinimumValueSpecified,
        decimal? MinimumValue,
        bool MaximumValueSpecified,
        decimal? MaximumValue);
}
