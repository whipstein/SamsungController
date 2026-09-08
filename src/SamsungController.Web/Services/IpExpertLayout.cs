namespace SamsungController.Web.Services;

public sealed record IpExpertGroup(string Id, string Name, IReadOnlyList<IpMenuControl> Controls);

/// <summary>Presentation-only ordering. Dependencies move with their prerequisite, including hidden controls.</summary>
public static class IpExpertLayout
{
    public static IReadOnlyList<IpExpertGroup> Groups { get; } = BuildGroups();

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? saved) => Array.AsReadOnly(
        (saved ?? []).Where(id => Groups.Any(group => group.Id == id)).Distinct(StringComparer.Ordinal)
            .Concat(Groups.Select(group => group.Id)).Distinct(StringComparer.Ordinal).ToArray());

    public static IEnumerable<IpExpertGroup> Ordered(IEnumerable<string>? saved) =>
        Normalize(saved).Select(id => Groups.Single(group => group.Id == id));

    public static IReadOnlyList<string> Move(IEnumerable<string>? saved, string source, string target, bool after)
    {
        var order = Normalize(saved).ToList();
        if (!order.Contains(source, StringComparer.Ordinal) || !order.Contains(target, StringComparer.Ordinal))
            throw new ArgumentException("Choose an Expert settings box or linked group to move.");
        if (source == target) return order.AsReadOnly();
        order.Remove(source);
        order.Insert(order.IndexOf(target) + (after ? 1 : 0), source);
        return order.AsReadOnly();
    }

    private static IReadOnlyList<IpExpertGroup> BuildGroups()
    {
        var controls = IpMenuCatalog.ForSection("expert").ToArray();
        var neighbors = controls.ToDictionary(control => control.Id, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var control in controls)
            foreach (var requirement in control.Command.Requirements)
                foreach (var parent in controls.Where(item => item.Method == requirement.Method && item.Field == requirement.Field))
                {
                    neighbors[control.Id].Add(parent.Id);
                    neighbors[parent.Id].Add(control.Id);
                }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<IpExpertGroup>();
        foreach (var control in controls)
        {
            if (visited.Contains(control.Id)) continue;
            var linked = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(control.Id);
            while (pending.TryPop(out var id))
            {
                if (!linked.Add(id)) continue;
                foreach (var neighbor in neighbors[id]) pending.Push(neighbor);
            }
            visited.UnionWith(linked);
            var members = controls.Where(item => linked.Contains(item.Id)).ToArray();
            var leader = members.FirstOrDefault(item => !item.Command.Requirements.Any(requirement =>
                members.Any(parent => parent.Method == requirement.Method && parent.Field == requirement.Field))) ?? members[0];
            groups.Add(new(leader.Id, leader.Name, Array.AsReadOnly(members.OrderBy(item => item.Id == leader.Id ? 0 : 1).ToArray())));
        }
        return groups.AsReadOnly();
    }
}
