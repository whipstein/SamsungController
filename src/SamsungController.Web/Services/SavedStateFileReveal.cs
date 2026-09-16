using System.ComponentModel;
using System.Diagnostics;

namespace SamsungController.Web.Services;

public static class SavedStateFileReveal
{
    public static string Label => OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in File Explorer" : "Show in file manager";
    public static ProcessStartInfo CreateStartInfo(string path, string platform)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A full saved-state path is required.");
        var start = new ProcessStartInfo { UseShellExecute = false };
        switch (platform)
        {
            case "macos": start.FileName = "/usr/bin/open"; start.ArgumentList.Add("-R"); start.ArgumentList.Add(path); break;
            case "windows": start.FileName = "explorer.exe"; start.ArgumentList.Add("/select," + path); break;
            case "linux": start.FileName = "xdg-open"; start.ArgumentList.Add(Path.GetDirectoryName(path)!); break;
            default: throw new PlatformNotSupportedException("Open the displayed saved-states folder in your file manager.");
        }
        return start;
    }
    public static void Open(string path)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(path, OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux"));
            if (process is null) throw new InvalidOperationException("The file manager could not be opened. Use the displayed file path.");
        }
        catch (Win32Exception error) { throw new InvalidOperationException("The file manager could not be opened on the server computer. Use the displayed file path.", error); }
    }
}
