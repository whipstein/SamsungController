using System.Text.Json;

namespace SamsungController.Core.Devices;

public sealed class JsonFileSamsungTokenStore(string path) : ISamsungTokenStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A token file path is required.", nameof(path))
            : path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> LoadAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeHost(host);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return tokens.GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string host,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var key = NormalizeHost(host);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await ReadAsync(cancellationToken).ConfigureAwait(false);
            tokens[key] = token;
            await WriteAsync(tokens, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeHost(host);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (tokens.Remove(key))
            {
                await WriteAsync(tokens, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(_path);
        var tokens = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);

        return tokens is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteAsync(
        Dictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    tokens,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, _path, overwrite: true);
        RestrictUnixPermissions(_path);
    }

    private static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return host.Trim().ToLowerInvariant();
    }

    private static void RestrictUnixPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: the per-user configuration directory is still the boundary.
        }
    }
}
