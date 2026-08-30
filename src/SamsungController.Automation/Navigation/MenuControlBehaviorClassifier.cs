namespace SamsungController.Automation.Navigation;

public static class MenuControlBehaviorClassifier
{
    public static MenuControlType GetEffectiveControlType(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return GetEffectiveControlType(
            node.ControlType,
            node.Label,
            node.SelectionOptions);
    }

    public static MenuControlType GetEffectiveControlType(
        MenuControlType declaredType,
        string label,
        IReadOnlyList<string>? options)
    {
        if (declaredType != MenuControlType.Selection)
        {
            return declaredType;
        }

        var choices = options ?? [];
        var isImplicitIndexedSelection = (label.Equals(
                "Interval",
                StringComparison.OrdinalIgnoreCase)
            && choices.Count > 1
            && choices.All(option => option.EndsWith('%')))
            || (label.Equals("Color", StringComparison.OrdinalIgnoreCase)
            && new[] { "Red", "Green", "Blue" }.All(required => choices.Contains(
                required,
                StringComparer.OrdinalIgnoreCase)));
        return isImplicitIndexedSelection
            ? MenuControlType.IndexedSelection
            : declaredType;
    }
}
