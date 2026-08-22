namespace SamsungController.Cli;

internal sealed class ConsoleCommandHistory
{
    private readonly object _sync = new();
    private readonly List<string> _entries = [];
    private readonly string? _path;
    private readonly int _capacity;

    public ConsoleCommandHistory(
        string? path,
        int capacity = 200,
        IEnumerable<string>? initialEntries = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _capacity = capacity;

        if (initialEntries is not null)
        {
            _entries.AddRange(initialEntries.Where(ShouldPersist));
        }
        else
        {
            Load();
        }

        TrimToCapacity();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Add(string command)
    {
        if (!ShouldPersist(command))
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.Count == 0
                || !string.Equals(_entries[^1], command, StringComparison.Ordinal))
            {
                _entries.Add(command);
                TrimToCapacity();
                Persist();
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            Persist();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _entries.AddRange(File.ReadLines(_path).Where(ShouldPersist));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a damaged or unreadable file cannot block the console.
        }
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllLines(temporaryPath, _entries);
            File.Move(temporaryPath, _path, overwrite: true);
            RestrictUnixPermissions(_path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Command execution remains available if history cannot be persisted.
        }
    }

    private void TrimToCapacity()
    {
        if (_entries.Count > _capacity)
        {
            _entries.RemoveRange(0, _entries.Count - _capacity);
        }
    }

    private static bool ShouldPersist(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t']);
        var verb = separator < 0 ? trimmed : trimmed[..separator];
        return !verb.Equals("raw", StringComparison.OrdinalIgnoreCase);
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
            // Best effort on filesystems that do not expose Unix permissions.
        }
    }
}
