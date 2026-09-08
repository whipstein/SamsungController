using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SamsungController.Desktop;

public sealed record DesktopStatus(string Product, string Version, bool Managed, string Instance, int ProcessId);
public sealed record DesktopInstance(string Instance, string Token, int ProcessId, int Port);

public static class DesktopFiles
{
    public const string Product = "SamsungController";
    public const string StatusRoute = "/_app/status";
    public const string StopRoute = "/_app/stop";
    public static string Version => typeof(DesktopFiles).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static Uri Address(int port) => port is >= 1024 and <= 65535 ? new($"http://127.0.0.1:{port}") : throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1024 to 65535.");
    public static string ConfigurationDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SamsungController__ConfigurationDirectory");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settings))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(settings));
                    if (json.RootElement.TryGetProperty("SamsungController", out var controller) && controller.ValueKind == JsonValueKind.Object &&
                        controller.TryGetProperty("ConfigurationDirectory", out var directory) && directory.ValueKind == JsonValueKind.String)
                        configured = directory.GetString();
                }
                catch (JsonException) { /* The host reports malformed settings; startup diagnostics use the default directory. */ }
            }
        }
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            root = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        }
        return Path.Combine(root, Product);
    }
    public static string DirectoryPath(string? configurationDirectory = null)
    {
        var directory = Path.Combine(configurationDirectory ?? ConfigurationDirectory(), "desktop");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }
    public static string InstancePath(int port, string? configurationDirectory = null) => Path.Combine(DirectoryPath(configurationDirectory), $"server-{port}.json");
    public static FileStream PrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
    public static DesktopInstance CreateInstance(int port) => new(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Environment.ProcessId, port);
    public static void WriteInstance(DesktopInstance instance, string? configurationDirectory = null)
    {
        var destination = InstancePath(instance.Port, configurationDirectory);
        var temporary = destination + "." + instance.Instance + ".tmp";
        try
        {
            using (var file = PrivateFile(temporary, FileMode.CreateNew)) JsonSerializer.Serialize(file, instance);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static DesktopInstance? ReadInstance(int port, string? configurationDirectory = null)
    {
        try { return JsonSerializer.Deserialize<DesktopInstance>(File.ReadAllText(InstancePath(port, configurationDirectory))); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }
    public static void RemoveInstance(DesktopInstance instance, string? configurationDirectory = null)
    {
        if (ReadInstance(instance.Port, configurationDirectory)?.Instance == instance.Instance) File.Delete(InstancePath(instance.Port, configurationDirectory));
    }
}
