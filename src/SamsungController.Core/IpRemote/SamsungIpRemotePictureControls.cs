namespace SamsungController.Core.IpRemote;

/// <summary>Protocol candidates, not claims of support or actual slider ranges on a display.</summary>
public sealed class SamsungIpRemotePictureControl
{
    private SamsungIpRemotePictureControl(string id, string name, string method)
    { Id = id; Name = name; Method = method; }

    public string Id { get; }
    public string Name { get; }
    public string Method { get; }
    public static IReadOnlyList<SamsungIpRemotePictureControl> All { get; } = Array.AsReadOnly(new[]
    {
        new SamsungIpRemotePictureControl("contrast", "Contrast", "contrastControl"),
        new SamsungIpRemotePictureControl("color", "Color", "colorControl"),
        new SamsungIpRemotePictureControl("sharpness", "Sharpness", "sharpnessControl")
    });

    public static bool IsSupported(string? id) => All.Any(item => item.Id == id);
    public static SamsungIpRemotePictureControl Get(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("Only Contrast, Color, and Sharpness picture experiments are enabled. No arbitrary setter is permitted.");

    public static int TestTarget(int original) => original is < 0 or > 100
        ? throw new ArgumentOutOfRangeException(nameof(original)) : original == 0 ? 1 : original - 1;
}
