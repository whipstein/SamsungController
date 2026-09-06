namespace SamsungController.Automation.Navigation;

public static class MenuDefaultValueResolver
{
    // Rule order is significant; every condition in the first matching rule
    // must match. Undeclared selections fall back to the state's file default.
    public static string? Resolve(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string>? externalStateValues = null)
    {
        foreach (var rule in node.DefaultValueWhen ?? [])
        {
            if (rule.When.Count > 0 && rule.When.All(condition =>
                    definition.ExternalStates.TryGetValue(condition.Key, out var state)
                    && condition.Value.Equals(
                        externalStateValues?.GetValueOrDefault(state.Id) ?? state.DefaultValue,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return rule.Value;
            }
        }

        return node.DefaultValue;
    }
}
