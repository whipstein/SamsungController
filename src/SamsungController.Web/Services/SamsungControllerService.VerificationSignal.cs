using System.Text.Json;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;

namespace SamsungController.Web.Services;

public sealed partial class SamsungControllerService
{
    // Setup stays acknowledged until it changes, but is never saved as menu
    // evidence or inferred merely from a persisted app selector.
    private readonly Dictionary<string, string> _verificationSignalConfirmations = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<MenuVerificationSignalRequirement> GetVerificationSignalRequirements(
        MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        if (MenuDefinitionVerificationPlanner.IsCurrent(definition, check))
            return [];

        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(check.ExternalStateId) && !string.IsNullOrWhiteSpace(check.ExternalStateValue))
        {
            required[check.ExternalStateId] = check.ExternalStateValue;
        }
        else if (check.Id is "condition:hidden-behavior" or "condition:disabled-behavior")
        {
            // Menu-controlled effects also have signal prerequisites. For
            // example, hiding BT.1886 by selecting Gamma 2.2 needs the 8-bit
            // Gamma variant AND a signal under which BT.1886 can first appear.
            var rule = GetMenuConditionalVerificationRule(definition, check);
            var blocked = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Include(MenuNode node)
            {
                if (!visited.Add(node.Id))
                    return;
                if (node.ParentId is { } parentId)
                    Include(definition.GetRequiredNode(parentId));
                var conditions = (node.HiddenWhen ?? []).Select(item => (item.SourceId, item.SourceKind, item.EqualsValue))
                    .Concat((node.DisabledWhen ?? []).Select(item => (item.SourceId, item.SourceKind, item.EqualsValue)));
                foreach (var condition in conditions.Where(item => item.SourceKind == MenuConditionSourceKind.ExternalState))
                {
                    if (!blocked.TryGetValue(condition.SourceId, out var values))
                        blocked[condition.SourceId] = values = new(StringComparer.OrdinalIgnoreCase);
                    values.Add(condition.EqualsValue);
                }
            }
            Include(definition.GetRequiredNode(rule.SourceId));
            Include(rule.Node);
            foreach (var (stateId, excluded) in blocked)
            {
                var state = definition.ExternalStates[stateId];
                var selected = _menuExternalStateValues.GetValueOrDefault(state.Id, state.DefaultValue);
                var available = state.Options.Where(value => !excluded.Contains(value)).ToArray();
                required[stateId] = available.FirstOrDefault(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault() ?? "No compatible signal value — check the menu conditions";
            }
        }
        if (required.Count == 0)
            return [];

        // Confirm the other source settings too: format and bit depth are independent.
        return definition.ExternalStates.Values.Select(state =>
        {
            var selected = _menuExternalStateValues.GetValueOrDefault(state.Id, state.DefaultValue);
            return new MenuVerificationSignalRequirement(state.Id, state.Label,
                required.GetValueOrDefault(state.Id, selected), selected);
        }).ToArray();
    }

    private MenuVerificationStartingValue? GetVerificationStartingValue(
        MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        if (check.Id is not ("condition:hidden-behavior" or "condition:disabled-behavior")
            || GetVerificationSignalRequirements(definition, check).Count == 0)
            return null;
        var rule = GetMenuConditionalVerificationRule(definition, check);
        var node = definition.GetRequiredNode(rule.SourceId);
        var blocked = (check.Id == "condition:hidden-behavior"
                ? (rule.Node.HiddenWhen ?? []).Select(item => (item.SourceId, item.SourceKind, item.EqualsValue))
                : (rule.Node.DisabledWhen ?? []).Select(item => (item.SourceId, item.SourceKind, item.EqualsValue)))
            .Where(item => item.SourceKind == MenuConditionSourceKind.MenuSetting
                && item.SourceId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EqualsValue).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultValue = MenuDefaultValueResolver.Resolve(definition, node, _menuExternalStateValues, _menuControlValues);
        string baseline;
        try
        {
            baseline = defaultValue is not null && !blocked.Contains(defaultValue)
                ? defaultValue
                : SelectMenuControlValueOutside(node, rule.EqualsValue, blocked);
        }
        catch (InvalidOperationException)
        {
            // The existing guided-test validation reports controls with no
            // alternate value; don't prevent the rest of the page from loading.
            return null;
        }
        baseline = NormalizePictureControlValue(definition, node, baseline);
        return new(node.Id, node.Label, baseline, _menuControlValues.GetValueOrDefault(node.Id, defaultValue ?? baseline));
    }

    private string VerificationSignalSignature(MenuDefinition definition, MenuDefinitionVerificationCheck check,
        IReadOnlyList<MenuVerificationSignalRequirement> requirements)
    {
        var starting = GetVerificationStartingValue(definition, check);
        // The test changes the predicted value itself. That must not clear the
        // user's confirmation of its starting value while commands are running.
        return JsonSerializer.Serialize(new
        {
            check.Fingerprint,
            Requirements = requirements,
            StartingNode = starting?.NodeId,
            StartingValue = starting?.RequiredValue
        });
    }

    private bool IsVerificationSignalSetupConfirmed(MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        var requirements = GetVerificationSignalRequirements(definition, check);
        return requirements.Count > 0 && requirements.All(item => item.Matches)
            && _verificationSignalConfirmations.GetValueOrDefault(check.Id) == VerificationSignalSignature(definition, check, requirements);
    }

    public async Task ConfirmMenuVerificationSignalSetupAsync(string checkId, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("confirm the verification signal setup");
        EnsureNoMenuRecording("confirm the verification signal setup");
        var recordStartingValue = false;
        MenuControlProfileValue? confirmedContext = null;
        lock (_sync)
        {
            var definition = _menuDefinition ?? throw new InvalidOperationException("No menu definition is loaded.");
            var check = GetMenuDefinitionVerificationPlan(definition).Checks.FirstOrDefault(item =>
                item.Id.Equals(checkId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Verification check '{checkId}' is no longer available.");
            _verificationSignalConfirmations.Remove(check.Id);
            if (MenuDefinitionVerificationPlanner.IsCurrent(definition, check))
                return;
            if (confirmed)
            {
                if (_client.State != SamsungConnectionState.Connected)
                    throw new InvalidOperationException("Connect to the TV before confirming the physical signal setup.");
                var requirements = GetVerificationSignalRequirements(definition, check);
                if (requirements.Count == 0)
                    throw new InvalidOperationException("This check does not require external signal setup.");
                EnsureVerificationSignalMatches(requirements);
                if (GetVerificationStartingValue(definition, check) is { } starting)
                {
                    // The checkbox explicitly confirms this value on the TV.
                    // Correct only that current-value record; send no keys.
                    if (IsValueContextSelector(starting.NodeId))
                        confirmedContext = new(starting.NodeId, starting.RequiredValue);
                    else
                    {
                        _menuControlValues[starting.NodeId] = starting.RequiredValue;
                        _explicitMenuControlValues.Add(starting.NodeId);
                        recordStartingValue = true;
                    }
                }
                _verificationSignalConfirmations[check.Id] = VerificationSignalSignature(definition, check, requirements);
            }
        }
        if (confirmedContext is not null)
            await RecordAppliedControlValueAsync(confirmedContext.NodeId, confirmedContext.Value).ConfigureAwait(false);
        else if (recordStartingValue)
            await PersistActiveConditionAsync(cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    private void RequireVerificationSignalSetup(MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        var requirements = GetVerificationSignalRequirements(definition, check);
        if (requirements.Count == 0)
            return;
        EnsureVerificationSignalMatches(requirements);
        if (!IsVerificationSignalSetupConfirmed(definition, check))
            throw new InvalidOperationException("Set the physical HDMI source to the required signal values, match the app's External HDMI Signal selectors, and confirm the signal-setup checkbox on this verification line before running the test.");
        if (GetVerificationStartingValue(definition, check) is { Matches: false } starting)
            throw new InvalidOperationException($"This test starts with {starting.Label} = {starting.RequiredValue}, but the app currently records {starting.PredictedValue}. Finish the previous test to restore its value, or set the TV to {starting.RequiredValue} and uncheck/reconfirm the starting conditions. No preliminary reset commands were sent.");
    }

    private static void EnsureVerificationSignalMatches(IReadOnlyList<MenuVerificationSignalRequirement> requirements)
    {
        var mismatches = requirements.Where(item => !item.Matches).ToArray();
        if (mismatches.Length > 0)
            throw new InvalidOperationException($"Required signal setup: {string.Join("; ", mismatches.Select(item => $"{item.Label} = {item.RequiredValue} (app currently: {item.SelectedValue})"))}. Set the external equipment and match the persistent app header before confirming or running this test.");
    }
}
