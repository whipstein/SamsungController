using SamsungController.Core.Devices;

namespace SamsungController.Core.IpRemote;

/// <summary>Separate endpoint-keyed credentials, never the WebSocket token file.</summary>
public sealed class PrivateIpRemoteTokenStore : ISamsungTokenStore
{
    private readonly JsonFileSamsungTokenStore _store;
    private readonly string _directory;

    public PrivateIpRemoteTokenStore(string privateDirectory)
    {
        _directory = Path.GetFullPath(privateDirectory);
        _store = new JsonFileSamsungTokenStore(Path.Combine(_directory, "tokens.json"));
    }

    public Task<string?> LoadAsync(string host, CancellationToken cancellationToken = default)
    {
        Prepare();
        return _store.LoadAsync(host, cancellationToken);
    }

    public Task SaveAsync(string host, string token, CancellationToken cancellationToken = default)
    {
        Prepare();
        return _store.SaveAsync(host, token, cancellationToken);
    }

    public Task RemoveAsync(string host, CancellationToken cancellationToken = default)
    {
        Prepare();
        return _store.RemoveAsync(host, cancellationToken);
    }

    private void Prepare()
    {
        EnsurePrivateDirectory(_directory);
        var path = Path.Combine(_directory, "tokens.json");
        if (File.Exists(path) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // A private parent also protects temporary files during atomic replacement.
    // Windows uses the user's configuration-directory ACL, not a credential vault.
    public static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, mode);
            File.SetUnixFileMode(directory, mode);
        }
    }
}
