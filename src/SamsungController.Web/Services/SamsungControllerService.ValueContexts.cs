using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Services;

public sealed partial class SamsungControllerService
{
    private Dictionary<string, string> ValueConditions(MenuControlProfileValue value,
        IReadOnlyDictionary<string, string>? settings = null) => MenuValueContext.Sources(_menuDefinition!, _menuDefinition!.GetRequiredNode(value.NodeId))
        .ToDictionary(source => source,
            source => MenuValueContext.SourceValue(_menuDefinition!, source, _menuExternalStateValues, settings ?? _menuControlValues)!, StringComparer.OrdinalIgnoreCase);

    private static bool SameConditions(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        MenuControlTargetProfileSerializer.ConditionKey(left) == MenuControlTargetProfileSerializer.ConditionKey(right);

    private IEnumerable<MenuControlProfileValue> ScopedValueIdentities() => _menuDefinition!.Nodes.Values
        .Where(node => node.ControlType is MenuControlType.Slider or MenuControlType.Switch or MenuControlType.Selection
            or MenuControlType.SubmenuSelection or MenuControlType.IndexedSelection)
        .Select(node => new MenuControlProfileValue(node.Id, node.DefaultValue!))
        .Concat(DisplayConditionBanks().SelectMany(bank => bank.CurrentValues.Concat(bank.TargetValues))
            .Where(value => value.SelectorNodeId is not null && IsSupportedStoredIdentity(value)))
        .DistinctBy(ConditionValueKey, StringComparer.OrdinalIgnoreCase);

    private bool IsSupportedStoredIdentity(MenuControlProfileValue value)
    {
        try
        {
            var node = _menuDefinition!.GetRequiredNode(value.NodeId);
            NormalizeMenuControlProfileValue(_menuDefinition, value with { Value = node.DefaultValue ?? string.Empty });
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private (string? Value, IReadOnlyList<MenuValueContextCandidate> Conflicts) LookupScopedValue(MenuControlProfileValue identity, bool target)
    {
        var conditions = ValueConditions(identity);
        var banks = DisplayConditionBanks().ToList();
        if (_settings.MenuControlProfileDefinitionId?.Equals(_menuDefinition!.Id, StringComparison.OrdinalIgnoreCase) == true
            && _settings.MenuControlProfileValues is { Count: > 0 } oldTargets)
            banks.Add(new(_menuDefinition.Id, ConditionDisplayKey, new Dictionary<string, string>(), [], oldTargets));
        if (target && banks.Any(bank => bank.Scoped && SameConditions(bank.Conditions, conditions)
            && (bank.ClearedTargetKeys ?? []).Contains(ConditionValueKey(identity), StringComparer.OrdinalIgnoreCase)))
            return (null, []);
        var exact = banks.Where(bank => bank.Scoped && SameConditions(bank.Conditions, conditions))
            .SelectMany(bank => target ? bank.TargetValues : bank.CurrentValues)
            .FirstOrDefault(value => ConditionValueKey(value).Equals(ConditionValueKey(identity), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            try { return (NormalizeMenuControlProfileValue(_menuDefinition!, exact).Value, []); }
            catch (InvalidOperationException)
            {
                return (null, [new(exact.Value, "No longer valid for this menu's declared range or choices; enter a valid replacement.")]);
            }
        }

        var candidates = new List<MenuValueContextCandidate>();
        var uncertain = false;
        foreach (var bank in banks)
        {
            var value = (target ? bank.TargetValues : bank.CurrentValues).FirstOrDefault(item =>
                ConditionValueKey(item).Equals(ConditionValueKey(identity), StringComparison.OrdinalIgnoreCase));
            if (value is null) continue;
            var previous = bank.Conditions.ToDictionary(pair => MenuValueContext.CanonicalSource(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            // Old files stored Picture Mode as a value, not as a context key.
            foreach (var setting in bank.CurrentValues.Where(item => item.SelectorNodeId is null))
                previous.TryAdd("setting:" + setting.NodeId, setting.Value);
            if (conditions.Any(pair => previous.TryGetValue(pair.Key, out var old) && !old.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            var missing = conditions.Keys.Where(key => !previous.ContainsKey(key)).ToArray();
            uncertain |= missing.Length > 0;
            try { NormalizeMenuControlProfileValue(_menuDefinition!, value); }
            catch (InvalidOperationException) { uncertain = true; }
            candidates.Add(new(value.Value, string.Join(", ", bank.Conditions.Select(pair => $"{pair.Key}={pair.Value}"))
                + (missing.Length > 0 ? $"; not recorded: {string.Join(", ", missing)}" : string.Empty)));
        }
        var distinct = candidates.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 1 && !uncertain ? (distinct[0], []) : (null, candidates);
    }

    private IReadOnlyList<MenuValueContextConflict> GetValueContextConflicts()
    {
        if (!UsesValueContexts) return [];
        var result = new List<MenuValueContextConflict>();
        foreach (var identity in ScopedValueIdentities())
            foreach (var target in new[] { false, true })
            {
                var lookup = LookupScopedValue(identity, target);
                if (lookup.Conflicts.Count > 0)
                    result.Add(new(identity, _menuDefinition!.GetPath(identity.NodeId)
                        + (identity.SelectorValue is { } interval ? $" · {interval}" : string.Empty), target, lookup.Conflicts));
            }
        return result;
    }

    private MenuControlConditionBank ComposeScopedBank() => new(_menuDefinition!.Id, ConditionDisplayKey, ActiveConditions(),
        ScopedValueIdentities().Select(value => (value, found: LookupScopedValue(value, false).Value))
            .Where(item => item.found is not null).Select(item => item.value with { Value = item.found! }).ToArray(),
        ScopedValueIdentities().Select(value => (value, found: LookupScopedValue(value, true).Value))
            .Where(item => item.found is not null).Select(item => item.value with { Value = item.found! }).ToArray(), true);

    private SamsungWebSettings WriteScopedValues(SamsungWebSettings settings, IEnumerable<MenuControlProfileValue> values, bool target,
        IReadOnlyDictionary<string, string>? fixedConditions = null)
    {
        foreach (var group in values.GroupBy(value => MenuControlTargetProfileSerializer.ConditionKey(fixedConditions ?? ValueConditions(value))))
        {
            var conditions = fixedConditions ?? ValueConditions(group.First());
            var old = (settings.MenuControlConditionBanks ?? []).FirstOrDefault(bank => bank.Scoped
                && bank.DefinitionId.Equals(_menuDefinition!.Id, StringComparison.OrdinalIgnoreCase)
                && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase) && SameConditions(bank.Conditions, conditions));
            var replacement = group.ToDictionary(ConditionValueKey, StringComparer.OrdinalIgnoreCase);
            var merged = (target ? old?.TargetValues : old?.CurrentValues) ?? [];
            merged = merged.Where(value => !replacement.ContainsKey(ConditionValueKey(value))).Concat(replacement.Values).ToArray();
            settings = UpsertConditionBank(settings, new(_menuDefinition!.Id, ConditionDisplayKey, conditions,
                target ? old?.CurrentValues ?? [] : merged,
                target ? merged : old?.TargetValues ?? [], true,
                (old?.ClearedTargetKeys ?? []).Where(key => !target || !replacement.ContainsKey(key)).ToArray()));
        }
        return settings;
    }

    private SamsungWebSettings StoreScopedCondition(SamsungWebSettings settings, IReadOnlyList<MenuControlProfileValue>? targets)
    {
        settings = WriteScopedValues(settings, CaptureCurrentConditionValues(), target: false);
        if (targets is null) return settings;
        var targetKeys = targets.Select(ConditionValueKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ScopedValueIdentities().Where(value => !targetKeys.Contains(ConditionValueKey(value)))
            .GroupBy(value => MenuControlTargetProfileSerializer.ConditionKey(ValueConditions(value))))
        {
            var conditions = ValueConditions(group.First());
            var old = (settings.MenuControlConditionBanks ?? []).FirstOrDefault(bank => bank.Scoped
                && bank.DefinitionId.Equals(_menuDefinition!.Id, StringComparison.OrdinalIgnoreCase)
                && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase) && SameConditions(bank.Conditions, conditions));
            settings = UpsertConditionBank(settings, new(_menuDefinition!.Id, ConditionDisplayKey, conditions,
                old?.CurrentValues ?? [], old?.TargetValues ?? [], true,
                (old?.ClearedTargetKeys ?? []).Concat(group.Select(ConditionValueKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        // Replace only the currently selected per-control target contexts.
        settings = settings with
        {
            MenuControlConditionBanks = (settings.MenuControlConditionBanks ?? []).Select(bank =>
            bank.Scoped && bank.DefinitionId.Equals(_menuDefinition!.Id, StringComparison.OrdinalIgnoreCase)
                && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase)
                ? bank with
                {
                    TargetValues = bank.TargetValues.Where(value => !_menuDefinition.Nodes.ContainsKey(value.NodeId)
                    || !SameConditions(ValueConditions(value), bank.Conditions)).ToArray()
                } : bank).ToArray()
        };
        return WriteScopedValues(settings, targets, target: true);
    }

    private void RestoreScopedConditionValues()
    {
        ResetMenuControlValues(_menuDefinition);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Restore(MenuNode node)
        {
            if (!visited.Add(node.Id)) return;
            foreach (var id in MenuValueContext.SettingDependencies(_menuDefinition!, node))
                Restore(_menuDefinition!.GetRequiredNode(id));
            if (node.DefaultValue is null || node.ControlType is MenuControlType.Confirmation) return;
            var lookup = LookupScopedValue(new(node.Id, node.DefaultValue), false);
            try
            {
                _menuControlValues[node.Id] = NormalizePictureControlValue(_menuDefinition!, node, lookup.Value
                    ?? MenuDefaultValueResolver.Resolve(_menuDefinition!, node, _menuExternalStateValues, _menuControlValues)!);
                if (lookup.Value is not null) _explicitMenuControlValues.Add(node.Id);
            }
            catch (InvalidOperationException) { /* An old value outside edited bounds is not a known baseline. */ }
        }
        foreach (var node in _menuDefinition!.Nodes.Values) Restore(node);
        foreach (var identity in ScopedValueIdentities().Where(value => value.SelectorNodeId is not null))
            if (LookupScopedValue(identity, false).Value is { } value)
            {
                try
                {
                    var normalized = NormalizeMenuControlProfileValue(_menuDefinition, identity with { Value = value });
                    _indexedConditionValues[ConditionValueKey(normalized)] = normalized;
                }
                catch (InvalidOperationException) { }
            }
    }

    public async Task ResolveMenuValueContextConflictAsync(MenuControlProfileValue value, bool asTarget, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("resolve saved-value contexts");
        EnsureNoMenuRecording("resolve saved-value contexts");
        var normalized = NormalizeMenuControlProfileValue(_menuDefinition!, value);
        if (!UsesValueContexts) throw new InvalidOperationException("Declare valueContext in the menu first.");
        await UpdateSettingsAsync(settings => WriteScopedValues(settings, [normalized], asTarget), cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            RestoreScopedConditionValues();
            _conditionProfileRevision++;
        }
        NotifyChanged();
    }

    private bool IsValueContextSelector(string nodeId) => UsesValueContexts && (_menuDefinition!.Nodes.Values.Any(node =>
        MenuValueContext.Sources(_menuDefinition, node).Contains("setting:" + nodeId, StringComparer.OrdinalIgnoreCase))
        || IsDefaultConditionSelector(nodeId));

    private bool IsDefaultConditionSelector(string nodeId) => _menuDefinition?.Nodes.Values.Any(node =>
        (node.DefaultValueWhen ?? []).Any(rule => rule.When.Keys.Any(key =>
            MenuValueContext.CanonicalSource(key).Equals("setting:" + nodeId, StringComparison.OrdinalIgnoreCase)))) == true;

    public bool IsMenuValueContextSelector(string nodeId)
    {
        lock (_sync) return IsValueContextSelector(nodeId);
    }

    // This records the context already selected on the physical TV. It must
    // never send a key, or carry dependent values over from the old context.
    public async Task SetRecordedMenuContextAsync(string nodeId, string value, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("record the current settings context");
        EnsureNoMenuRecording("record the current settings context");
        if (!IsValueContextSelector(nodeId))
            throw new InvalidOperationException($"'{nodeId}' is not a saved-value or default condition selector.");
        var normalized = NormalizePictureControlValue(_menuDefinition!, _menuDefinition!.GetRequiredNode(nodeId), value);
        cancellationToken.ThrowIfCancellationRequested();
        await RecordAppliedControlValueAsync(nodeId, normalized).ConfigureAwait(false);
        NotifyChanged();
    }

    public void ValidateMenuValueContextBatch(IReadOnlyList<MenuControlValueUpdate> updates,
        IReadOnlyList<MenuIndexedControlValueUpdate>? indexedUpdates = null)
    {
        lock (_sync)
        {
            if (!UsesValueContexts && !updates.Any(update => IsDefaultConditionSelector(update.NodeId))) return;
            var ids = updates.Select(update => update.NodeId).Concat((indexedUpdates ?? []).Select(update => update.NodeId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool DependsOn(string id, string source, HashSet<string> visited)
            {
                if (!visited.Add(id)) return false;
                return MenuValueContext.SettingDependencies(_menuDefinition!, _menuDefinition!.GetRequiredNode(id))
                    .Any(dependency => dependency.Equals(source, StringComparison.OrdinalIgnoreCase) || DependsOn(dependency, source, visited));
            }
            var conflicts = GetValueContextConflicts().Where(conflict => !conflict.IsTarget);
            if (conflicts.Any(conflict => ids.Contains(conflict.Value.NodeId)
                || ids.Any(id => DependsOn(id, conflict.Value.NodeId, new(StringComparer.OrdinalIgnoreCase)))))
                throw new InvalidOperationException("Resolve the saved-value context conflicts or enter the TV's current values before applying these controls.");
            foreach (var selector in updates.Where(update => (IsValueContextSelector(update.NodeId) || IsDefaultConditionSelector(update.NodeId))
                && !update.FromValue.Equals(update.ToValue, StringComparison.OrdinalIgnoreCase)))
                if (ids.Any(id => DependsOn(id, selector.NodeId, new(StringComparer.OrdinalIgnoreCase))))
                    throw new InvalidOperationException($"Apply '{_menuDefinition!.GetPath(selector.NodeId)}' separately before adjusting settings that depend on it. Their current values change with the selected context.");
        }
    }

    private async Task RecordAppliedControlValueAsync(string nodeId, string value)
    {
        var changesContext = IsValueContextSelector(nodeId) && !_menuControlValues.GetValueOrDefault(nodeId, "").Equals(value, StringComparison.OrdinalIgnoreCase);
        if (changesContext) await PersistActiveConditionAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            _menuControlValues[nodeId] = value;
            _explicitMenuControlValues.Add(nodeId);
            RefreshMenuDefaultControlValues(_menuDefinition);
        }
        if (changesContext)
        {
            // Only the selector changed on the TV. Never relabel old-mode
            // dependent values as if they were readings from the new mode.
            await UpdateSettingsAsync(settings => WriteScopedValues(settings, [new(nodeId, value)], false), CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                RestoreScopedConditionValues();
                _conditionProfileRevision++;
            }
        }
        else await PersistActiveConditionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<MenuControlConditionValues> ValidateScopedCalibration(MenuControlTargetProfile document)
    {
        if (document.Version != MenuControlTargetProfileSerializer.ScopedVersion)
            throw new InvalidOperationException("This menu uses per-setting valueContext rules. Use a version 3 calibration with each setting's declared conditions (including setting:picture-mode where needed). Old saved banks remain available for review on Menu.");
        var sets = document.ConditionValues ?? [];
        if (sets.Count == 0) throw new InvalidOperationException("A scoped calibration needs conditionValues; use conditions: {} for shared settings.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sets.Select((set, index) =>
        {
            if (set.Conditions.Keys.Select(MenuValueContext.CanonicalSource).Distinct(StringComparer.OrdinalIgnoreCase).Count() != set.Conditions.Count)
                throw new InvalidOperationException($"Combination {index + 1}: a condition source is duplicated.");
            var conditions = set.Conditions.ToDictionary(pair => MenuValueContext.CanonicalSource(pair.Key),
                pair => MenuValueContext.NormalizeSourceValue(_menuDefinition!, pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(MenuControlTargetProfileSerializer.ConditionKey(conditions)))
                throw new InvalidOperationException($"Combination {index + 1}: conditions duplicate an earlier entry. Group their values together.");
            foreach (var pair in conditions)
                if (MenuValueContext.ValidateSource(_menuDefinition!, pair.Key, pair.Value) is { } error)
                    throw new InvalidOperationException($"Combination {index + 1}: {error}");
            var values = NormalizeMenuControlProfileValues(_menuDefinition!, set.Values);
            foreach (var value in values)
            {
                var required = MenuValueContext.Sources(_menuDefinition!, _menuDefinition!.GetRequiredNode(value.NodeId));
                if (!required.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(conditions.Keys))
                    throw new InvalidOperationException($"Combination {index + 1}, '{value.NodeId}': conditions must be exactly [{string.Join(", ", required)}]. Use a separate entry for settings with different valueContext rules.");
            }
            return new MenuControlConditionValues(conditions, values);
        }).ToArray();
    }

    private async Task ImportScopedCalibrationAsync(IReadOnlyList<MenuControlConditionValues> sets, bool asCurrent, CancellationToken cancellationToken)
    {
        await UpdateSettingsAsync(settings =>
        {
            var updated = StoreActiveCondition(settings);
            foreach (var set in sets) updated = WriteScopedValues(updated, set.Values, !asCurrent, set.Conditions);
            return updated;
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            RestoreScopedConditionValues();
            _conditionProfileRevision++;
        }
        NotifyChanged();
    }

    private MenuControlTargetProfile ExportScopedCalibration(string name, bool currentValues)
    {
        var definition = _menuDefinition!;
        var snapshot = StoreScopedCondition(_settings, targets: null);
        var sets = (snapshot.MenuControlConditionBanks ?? []).Where(bank => bank.Scoped
                && bank.DefinitionId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase))
            .Select(bank => new MenuControlConditionValues(bank.Conditions, (currentValues ? bank.CurrentValues : bank.TargetValues)
                .Where(value => definition.Nodes.ContainsKey(value.NodeId) && MenuValueContext.Sources(definition, definition.GetRequiredNode(value.NodeId))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(bank.Conditions.Keys)).ToArray()))
            .Where(set => set.Values.Count > 0).ToArray();
        if (sets.Length == 0) throw new InvalidOperationException("No saved scoped values are available. Save entered current settings or targets first.");
        var document = new MenuControlTargetProfile(MenuControlTargetProfileSerializer.ScopedVersion, name, definition.Id, definition.Name, definition.Model, definition.Context, DateTimeOffset.UtcNow, [], sets);
        ValidateCalibrationConditions(document);
        return document;
    }
}
