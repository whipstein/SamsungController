using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Services;

public sealed partial class SamsungControllerService
{
    private readonly Dictionary<string, MenuControlProfileValue> _indexedConditionValues = new(StringComparer.OrdinalIgnoreCase);
    private int _conditionProfileRevision;

    private string ConditionDisplayKey => _settings.DisplayDefinitionPath ?? _settings.Host ?? "unassigned-display";

    private Dictionary<string, string> ActiveConditions() => (_menuDefinition?.ExternalStates.Values ?? [])
        .ToDictionary(state => state.Id, state => _menuExternalStateValues.GetValueOrDefault(state.Id, state.DefaultValue), StringComparer.OrdinalIgnoreCase);

    private string ActiveConditionKey() => $"{ConditionDisplayKey}\u001f{_menuDefinition?.Id}\u001f{MenuControlTargetProfileSerializer.ConditionKey(ActiveConditions())}";

    private IReadOnlyList<MenuControlConditionBank> DisplayConditionBanks() => (_settings.MenuControlConditionBanks ?? [])
        .Where(bank => bank.DefinitionId.Equals(_menuDefinition?.Id, StringComparison.OrdinalIgnoreCase)
            && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase)).ToArray();

    private MenuControlConditionBank? ActiveConditionBank() => DisplayConditionBanks().FirstOrDefault(bank =>
        MenuControlTargetProfileSerializer.ConditionKey(bank.Conditions) == MenuControlTargetProfileSerializer.ConditionKey(ActiveConditions()));

    // Older unscoped targets are used only until the first bank is saved, then bound
    // to that combination. They must not silently follow every new signal selection.
    private IReadOnlyList<MenuControlProfileValue> LegacyTargetValues() => DisplayConditionBanks().Count == 0
        && _menuDefinition?.Id.Equals(_settings.MenuControlProfileDefinitionId, StringComparison.OrdinalIgnoreCase) == true
            ? _settings.MenuControlProfileValues ?? [] : [];

    private IReadOnlyList<MenuControlProfileValue> CaptureCurrentConditionValues() => _menuControlValues
        .Where(pair => _explicitMenuControlValues.Contains(pair.Key))
        .Select(pair => new MenuControlProfileValue(pair.Key, pair.Value))
        .Concat(_indexedConditionValues.Values).ToArray();

    private SamsungWebSettings StoreActiveCondition(SamsungWebSettings settings,
        IReadOnlyList<MenuControlProfileValue>? targets = null)
    {
        if (_menuDefinition is null)
        {
            return settings;
        }
        var conditions = ActiveConditions();
        var bank = new MenuControlConditionBank(_menuDefinition.Id, ConditionDisplayKey, conditions,
            CaptureCurrentConditionValues(), targets ?? ActiveConditionBank()?.TargetValues ?? LegacyTargetValues());
        return UpsertConditionBank(settings, bank);
    }

    private static SamsungWebSettings UpsertConditionBank(SamsungWebSettings settings, MenuControlConditionBank bank)
    {
        var key = MenuControlTargetProfileSerializer.ConditionKey(bank.Conditions);
        var banks = (settings.MenuControlConditionBanks ?? []).Where(existing =>
            !existing.DefinitionId.Equals(bank.DefinitionId, StringComparison.OrdinalIgnoreCase)
            || !existing.DisplayKey.Equals(bank.DisplayKey, StringComparison.OrdinalIgnoreCase)
            || MenuControlTargetProfileSerializer.ConditionKey(existing.Conditions) != key).Append(bank).ToArray();
        return settings with
        {
            MenuControlConditionBanks = banks,
            MenuControlProfileDefinitionId = settings.MenuControlProfileDefinitionId?.Equals(bank.DefinitionId, StringComparison.OrdinalIgnoreCase) == true ? null : settings.MenuControlProfileDefinitionId,
            MenuControlProfileValues = settings.MenuControlProfileDefinitionId?.Equals(bank.DefinitionId, StringComparison.OrdinalIgnoreCase) == true ? null : settings.MenuControlProfileValues
        };
    }

    private bool SavedStateMatchesActiveConditions(SavedMenuControlState state) =>
        (state.DisplayKey is null || state.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase))
        && (state.Conditions is null || MenuControlTargetProfileSerializer.ConditionKey(state.Conditions)
            == MenuControlTargetProfileSerializer.ConditionKey(ActiveConditions()));

    private Task PersistActiveConditionAsync(CancellationToken cancellationToken = default) =>
        UpdateSettingsAsync(settings => StoreActiveCondition(settings), cancellationToken);

    private void RestoreActiveConditionValues()
    {
        if (_menuDefinition is not { } definition || ActiveConditionBank() is not { } bank)
        {
            return;
        }
        foreach (var value in bank.CurrentValues)
        {
            try
            {
                var normalized = NormalizeMenuControlProfileValue(definition, value);
                if (string.IsNullOrWhiteSpace(normalized.SelectorNodeId))
                {
                    _menuControlValues[normalized.NodeId] = normalized.Value;
                    _explicitMenuControlValues.Add(normalized.NodeId);
                }
                else
                {
                    _indexedConditionValues[ConditionValueKey(normalized)] = normalized;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                // An edited menu can remove a control or narrow its range. Do not
                // turn a stale stored value into a prediction for the new control.
            }
        }
    }

    private static string ConditionValueKey(MenuControlProfileValue value) =>
        $"{value.SelectorNodeId}\u001f{value.SelectorValue}\u001f{value.NodeId}";

    public IReadOnlyList<MenuControlConditionValues> ValidateCalibrationConditions(MenuControlTargetProfile document)
    {
        // Validate structure before touching any local settings, including calls
        // from outside the file-upload component.
        MenuControlTargetProfileSerializer.Serialize(document);
        lock (_sync)
        {
            var definition = _menuDefinition ?? throw new InvalidOperationException("No menu definition is loaded.");
            if (!definition.Id.Equals(document.DefinitionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Calibration '{document.Name}' belongs to menu definition '{document.DefinitionId}', not '{definition.Id}'. Load the matching menu first.");
            }
            var sets = document.ConditionValues is { Count: > 0 } specified
                ? specified : [new MenuControlConditionValues(ActiveConditions(), document.Values)];
            return sets.Select((set, index) =>
            {
                var conditions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, value) in set.Conditions)
                {
                    if (!definition.ExternalStates.TryGetValue(id, out var state))
                    {
                        throw new InvalidOperationException($"Combination {index + 1}: external state '{id}' is not defined by this menu.");
                    }
                    var normalized = state.Options.FirstOrDefault(option => option.Equals(value, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Combination {index + 1}: '{value}' is not available for {state.Label}.");
                    conditions.Add(state.Id, normalized);
                }
                var missing = definition.ExternalStates.Keys.Where(id => !conditions.ContainsKey(id)).ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidOperationException($"Combination {index + 1}: specify all input conditions. Missing: {string.Join(", ", missing)}.");
                }
                try
                {
                    return new MenuControlConditionValues(conditions, NormalizeMenuControlProfileValues(definition, set.Values));
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
                {
                    throw new InvalidOperationException($"Combination {index + 1}: {exception.Message}", exception);
                }
            }).ToArray();
        }
    }

    public async Task ImportCalibrationConditionsAsync(MenuControlTargetProfile document, bool asCurrent,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("load calibration values");
        EnsureNoMenuRecording("load calibration values");
        var sets = ValidateCalibrationConditions(document);
        await UpdateSettingsAsync(settings =>
        {
            var updated = StoreActiveCondition(settings);
            foreach (var set in sets)
            {
                var key = MenuControlTargetProfileSerializer.ConditionKey(set.Conditions);
                var existing = updated.MenuControlConditionBanks!.FirstOrDefault(bank =>
                    bank.DefinitionId.Equals(document.DefinitionId, StringComparison.OrdinalIgnoreCase)
                    && bank.DisplayKey.Equals(ConditionDisplayKey, StringComparison.OrdinalIgnoreCase)
                    && MenuControlTargetProfileSerializer.ConditionKey(bank.Conditions) == key);
                updated = UpsertConditionBank(updated, new MenuControlConditionBank(document.DefinitionId, ConditionDisplayKey, set.Conditions,
                    asCurrent ? set.Values : existing?.CurrentValues ?? [],
                    asCurrent ? existing?.TargetValues ?? [] : set.Values));
            }
            return updated;
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ResetMenuControlValues(_menuDefinition);
            RestoreActiveConditionValues();
            _conditionProfileRevision++;
        }
        NotifyChanged();
    }

    public MenuControlTargetProfile ExportCalibrationConditions(string name, bool currentValues)
    {
        lock (_sync)
        {
            var definition = _menuDefinition ?? throw new InvalidOperationException("No menu definition is loaded.");
            var banks = DisplayConditionBanks().ToList();
            var active = ActiveConditionBank();
            if (active is not null)
            {
                banks.Remove(active);
            }
            banks.Add(new MenuControlConditionBank(definition.Id, ConditionDisplayKey, ActiveConditions(),
                CaptureCurrentConditionValues(), active?.TargetValues ?? LegacyTargetValues()));
            var sets = banks.Select(bank => new MenuControlConditionValues(bank.Conditions,
                currentValues ? bank.CurrentValues : bank.TargetValues)).Where(set => set.Values.Count > 0).ToArray();
            if (sets.Length == 0)
            {
                throw new InvalidOperationException(currentValues
                    ? "Save entered current values for at least one input combination before downloading. Assumed defaults are not exported as confirmed settings."
                    : "Save target values for at least one input combination before downloading.");
            }
            var document = new MenuControlTargetProfile(MenuControlTargetProfileSerializer.CurrentVersion,
                name, definition.Id, definition.Name, definition.Model, definition.Context, DateTimeOffset.UtcNow, [], sets);
            ValidateCalibrationConditions(document);
            return document;
        }
    }
}
