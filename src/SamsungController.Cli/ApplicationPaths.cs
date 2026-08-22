namespace SamsungController.Cli;

internal static class ApplicationPaths
{
    public static string GetDefaultConfigurationDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdgConfigurationHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDirectory = string.IsNullOrWhiteSpace(xdgConfigurationHome)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config")
                : xdgConfigurationHome;

            return Path.Combine(baseDirectory, "SamsungController");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SamsungController");
    }
}
