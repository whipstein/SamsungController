using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Web.Services;

public sealed class MenuTraversalRecorder
{
    private readonly List<MenuOperation> _operations = [];

    public bool IsRecording { get; private set; }

    public MenuRecordingRequest? Request { get; private set; }

    public IReadOnlyList<MenuOperation> Operations => _operations;

    public void Start(MenuRecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReplayDelayMilliseconds is < 50 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Replay delay must be between 50 and 30000 milliseconds.");
        }

        Request = request;
        _operations.Clear();
        IsRecording = true;
    }

    public void Record(string key, RemoteKeyAction action)
    {
        if (!IsRecording)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = key.Trim();
        var delay = TimeSpan.FromMilliseconds(Request!.ReplayDelayMilliseconds);
        if (_operations.Count > 0)
        {
            var previous = _operations[^1];
            if (previous.Key.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase)
                && previous.Action == action
                && previous.DelayAfter == delay
                && previous.Repeat < MenuDefinitionValidator.MaximumRepeat)
            {
                _operations[^1] = previous with { Repeat = previous.Repeat + 1 };
                return;
            }
        }

        _operations.Add(new MenuOperation(normalizedKey, action, DelayAfter: delay));
    }

    public void UndoLastCommand()
    {
        if (!IsRecording || _operations.Count == 0)
        {
            return;
        }

        var previous = _operations[^1];
        if (previous.Repeat > 1)
        {
            _operations[^1] = previous with { Repeat = previous.Repeat - 1 };
        }
        else
        {
            _operations.RemoveAt(_operations.Count - 1);
        }
    }

    public void ClearCommands()
    {
        if (IsRecording)
        {
            _operations.Clear();
        }
    }

    public void Stop()
    {
        if (!IsRecording)
        {
            throw new InvalidOperationException("No menu traversal recording is active.");
        }

        IsRecording = false;
    }

    public void Resume()
    {
        if (Request is null)
        {
            throw new InvalidOperationException("No menu traversal recording is available to resume.");
        }

        IsRecording = true;
    }

    public void Reset()
    {
        IsRecording = false;
        Request = null;
        _operations.Clear();
    }
}
