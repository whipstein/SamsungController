namespace SamsungController.Web.Services;

public sealed record IpExpertGroup(string Id, string Name, IReadOnlyList<IpMenuControl> Controls);

/// <summary>Presentation-only ordering. Dependencies move with their prerequisite, including hidden controls.</summary>
public static class IpExpertLayout
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<IpExpertGroup>> SectionGroups =
        new[] { "expert", "sound", "system" }.ToDictionary(section => section, BuildGroups, StringComparer.Ordinal);
    public static bool SupportsSection(string section) => SectionGroups.ContainsKey(section);
    public static IReadOnlyList<IpExpertGroup> Groups => ForSection("expert");
    public static IReadOnlyList<IpExpertGroup> ForSection(string section) => SectionGroups.TryGetValue(section, out var groups)
        ? groups : throw new ArgumentException("This section does not support arranging boxes.");

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? saved, string section = "expert") => Array.AsReadOnly(
        (saved ?? []).Where(id => ForSection(section).Any(group => group.Id == id)).Distinct(StringComparer.Ordinal)
            .Concat(ForSection(section).Select(group => group.Id)).Distinct(StringComparer.Ordinal).ToArray());

    public static IEnumerable<IpExpertGroup> Ordered(IEnumerable<string>? saved, string section = "expert") =>
        Normalize(saved, section).Select(id => ForSection(section).Single(group => group.Id == id));

    public static IReadOnlyList<string> Move(IEnumerable<string>? saved, string source, string target, bool after, string section = "expert")
    {
        var order = Normalize(saved, section).ToList();
        if (!order.Contains(source, StringComparer.Ordinal) || !order.Contains(target, StringComparer.Ordinal))
            throw new ArgumentException("Choose a box or linked group from the current section to move.");
        if (source == target) return order.AsReadOnly();
        order.Remove(source);
        order.Insert(order.IndexOf(target) + (after ? 1 : 0), source);
        return order.AsReadOnly();
    }

    private static IReadOnlyList<IpExpertGroup> BuildGroups(string section)
    {
        var controls = IpMenuCatalog.ForSection(section).ToArray();
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
