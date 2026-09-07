using System.Text.Json;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;

namespace SamsungController.Web.Services;

public sealed partial class SamsungControllerService
{
    // Physical source setup is a per-run acknowledgement, never saved as menu
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

    private static string VerificationSignalSignature(MenuDefinitionVerificationCheck check,
        IReadOnlyList<MenuVerificationSignalRequirement> requirements) =>
        JsonSerializer.Serialize(new { check.Fingerprint, Requirements = requirements });

    private bool IsVerificationSignalSetupConfirmed(MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        var requirements = GetVerificationSignalRequirements(definition, check);
        return requirements.Count > 0 && requirements.All(item => item.Matches)
            && _verificationSignalConfirmations.GetValueOrDefault(check.Id) == VerificationSignalSignature(check, requirements);
    }

    public async Task ConfirmMenuVerificationSignalSetupAsync(string checkId, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("confirm the verification signal setup");
        EnsureNoMenuRecording("confirm the verification signal setup");
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
                _verificationSignalConfirmations[check.Id] = VerificationSignalSignature(check, requirements);
            }
        }
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
        _verificationSignalConfirmations.Remove(check.Id);
    }

    private static void EnsureVerificationSignalMatches(IReadOnlyList<MenuVerificationSignalRequirement> requirements)
    {
        var mismatches = requirements.Where(item => !item.Matches).ToArray();
        if (mismatches.Length > 0)
            throw new InvalidOperationException($"Required signal setup: {string.Join("; ", mismatches.Select(item => $"{item.Label} = {item.RequiredValue} (app currently: {item.SelectedValue})"))}. Set the external equipment and match the persistent app header before confirming or running this test.");
    }
}
