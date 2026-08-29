using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Web.Services;

public sealed class MenuTraversalRecorder
{
    private readonly List<MenuOperation> _operations = [];
    private readonly List<MenuOperation> _returnOperations = [];

    public bool IsRecording { get; private set; }

    public MenuRecordingRequest? Request { get; private set; }

    public MenuTimingProfile Timing { get; private set; } = new();

    public bool IsRecordingReturnToVideo { get; private set; }

    public IReadOnlyList<MenuOperation> Operations =>
        IsRecordingReturnToVideo ? _returnOperations : _operations;

    public IReadOnlyList<MenuOperation> ForwardOperations => _operations;

    public IReadOnlyList<MenuOperation> ReturnToVideoOperations => _returnOperations;

    public void Start(MenuRecordingRequest request, MenuTimingProfile timing)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timing);

        Request = request;
        Timing = timing;
        _operations.Clear();
        _returnOperations.Clear();
        IsRecordingReturnToVideo = false;
        IsRecording = true;
    }

    public void BeginReturnToVideo()
    {
        if (!IsRecording || Request?.RecordReturnToVideo != true)
        {
            throw new InvalidOperationException(
                "This recording does not include a return-to-video sequence.");
        }

        if (_operations.Count == 0)
        {
            throw new InvalidOperationException(
                "Record at least one traversal command before recording its return sequence.");
        }

        IsRecordingReturnToVideo = true;
    }

    public void Record(string key, RemoteKeyAction action)
    {
        if (!IsRecording)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = key.Trim();
        var operations = IsRecordingReturnToVideo ? _returnOperations : _operations;
        if (operations.Count > 0)
        {
            var previous = operations[^1];
            if (previous.Key.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase)
                && previous.Action == action
                && previous.DelayAfter is null
                && previous.Repeat < MenuDefinitionValidator.MaximumRepeat)
            {
                operations[^1] = previous with { Repeat = previous.Repeat + 1 };
                return;
            }
        }

        operations.Add(new MenuOperation(normalizedKey, action));
    }

    public void UndoLastCommand()
    {
        var operations = IsRecordingReturnToVideo ? _returnOperations : _operations;
        if (!IsRecording || operations.Count == 0)
        {
            return;
        }

        var previous = operations[^1];
        if (previous.Repeat > 1)
        {
            operations[^1] = previous with { Repeat = previous.Repeat - 1 };
        }
        else
        {
            operations.RemoveAt(operations.Count - 1);
        }
    }

    public void RemoveCommand(int index)
    {
        var operations = OperationsList;
        if (!IsRecording || index < 0 || index >= operations.Count)
        {
            return;
        }

        operations.RemoveAt(index);
    }

    public void ClearCommands()
    {
        if (IsRecording)
        {
            OperationsList.Clear();
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
        Timing = new MenuTimingProfile();
        IsRecordingReturnToVideo = false;
        _operations.Clear();
        _returnOperations.Clear();
    }

    private List<MenuOperation> OperationsList =>
        IsRecordingReturnToVideo ? _returnOperations : _operations;
}
