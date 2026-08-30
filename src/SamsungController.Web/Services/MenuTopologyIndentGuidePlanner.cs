namespace SamsungController.Web.Services;

internal static class MenuTopologyIndentGuidePlanner
{
    public static IReadOnlyList<int> CreateLineDepths(string? outline)
    {
        var lines = (outline ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var depths = new int[lines.Length];
        var previousItemDepth = 0;
        var lastItemIndex = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                depths[index] = previousItemDepth;
                continue;
            }

            var leadingSpaces = line.Length - line.TrimStart(' ').Length;
            previousItemDepth = leadingSpaces / 2;
            depths[index] = previousItemDepth;
            lastItemIndex = index;
        }

        for (var index = lastItemIndex + 1; index < depths.Length; index++)
        {
            depths[index] = 0;
        }

        return depths;
    }
}
