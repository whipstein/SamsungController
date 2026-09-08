using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private MenuNudgeSession? _menuNudges;
    private const int MaximumQueuedNudges = 256;

    // Explicit numeric edits join this queue. Other operations continue to use
    // the normal gate and cannot interleave reads, modes, remote keys or writes.
    public string? MenuNudgeDisabledReason(IpMenuControl control)
    {
        lock (_sync)
        {
            if (control.Parameter.Kind != IpRemoteParameterKind.Integer || control.RequiresSeparateApply)
                return "Only numeric setting adjustments can be queued.";
            if (!_snapshot.Menu.Preferences.ApplyImmediately) return MenuControlDisabledReason(control);
            if (_menuNudges is not { } session)
                return MenuControlDisabledReason(control) ?? (_snapshot.Menu.Pending.Count > 0 ? "Apply or discard pending changes before queuing adjustments." : null);
            if (!session.Accepting || _operation?.IsCancellationRequested == true) return "The adjustment queue is stopping.";
            if (!_snapshot.Menu.Connected || _snapshot.Menu.SessionId != session.Baseline.SessionId
                || _snapshot.Menu.ValuesRevision != session.Baseline.ValuesRevision) return "The TV connection or context changed; the queue is stopping.";
            if (session.Entries.Count >= MaximumQueuedNudges) return "The adjustment queue is full. Wait for some changes to finish.";
            return MenuValueDisabledReason(session.Baseline, control);
        }
    }

    public Task QueueMenuNudgeAsync(string controlId, int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentException("A slider click must be +1 or -1.");
        var control = IpMenuCatalog.Get(controlId);
        return QueueMenuAdjustment(control, original => Math.Clamp(original + delta, control.Parameter.Minimum, control.Parameter.Maximum));
    }

    public Task QueueMenuValueAsync(string controlId, string text)
    {
        var control = IpMenuCatalog.Get(controlId);
        if (control.Parameter.Kind != IpRemoteParameterKind.Integer || control.RequiresSeparateApply)
            throw new ArgumentException("Only numeric setting adjustments can be queued.");
        var target = IpMenuCatalog.ParseTarget(control, text).GetValue<int>();
        return QueueMenuAdjustment(control, _ => target);
    }

    private Task QueueMenuAdjustment(IpMenuControl control, Func<int, int> requestedTarget)
    {
        var controlId = control.Id;
        MenuNudgeSession session;
        MenuNudgeEntry entry;
        var start = false;
        lock (_sync)
        {
            if (!_snapshot.Menu.Preferences.ApplyImmediately) throw new InvalidOperationException("Use Apply immediately to queue numeric adjustments.");
            if (MenuNudgeDisabledReason(control) is { } reason) throw new InvalidOperationException(reason);
            if (_menuNudges is null)
            {
                EnsureMenuWritesAllowed();
                _menuNudges = new(_snapshot.Menu);
                start = true;
            }
            session = _menuNudges;
            var original = session.Expected.GetValueOrDefault(controlId, session.Baseline.Value(control)!.GetValue<int>());
            var target = requestedTarget(original);
            if (target == original)
            {
                if (start) _menuNudges = null;
                return Task.CompletedTask;
            }
            entry = new(new(controlId, JsonValue.Create(target)!, JsonValue.Create(original)!,
                MenuPrerequisites(session.Baseline, control, forEditing: true), session.Baseline.Input!, session.Baseline.PictureMode!));
            session.Expected[controlId] = target;
            session.Entries.Add(entry);
            PublishNudgesLocked(session);
        }
        Changed?.Invoke();
        if (start) _ = RunMenuNudgesAsync(session);
        return entry.Completion.Task;
    }

    private async Task RunMenuNudgesAsync(MenuNudgeSession session)
    {
        Exception? failure = null;
        try
        {
            await RunMenuOperationAsync(async (profile, cancellation) =>
            {
                while (true)
                {
                    MenuNudgeEntry entry;
                    lock (_sync)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!session.Accepting) throw new OperationCanceledException("Adjustment queue stopped.");
                        if (session.Entries.Count == 0) { session.Accepting = false; break; }
                        entry = session.Entries[0];
                    }
                    // Each explicit target has the same fresh preflight,
                    // per-write journal, readback and selector protection as Apply.
                    await ApplyMenuCoreAsync(profile, cancellation, [entry.Draft]).ConfigureAwait(false);
                    lock (_sync)
                    {
                        session.Entries.RemoveAt(0);
                        PublishNudgesLocked(session);
                    }
                    Changed?.Invoke();
                    entry.Completion.TrySetResult();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Settle every awaiting edit; never let a detached queue worker
            // leave a UI event waiting forever after an unexpected failure.
            failure = error;
        }
        finally
        {
            MenuNudgeEntry[] remaining;
            lock (_sync)
            {
                session.Accepting = false;
                remaining = session.Entries.ToArray();
                if (ReferenceEquals(_menuNudges, session))
                {
                    _menuNudges = null;
                    _snapshot = _snapshot with { Menu = _snapshot.Menu with
                    {
                        NudgeQueue = null,
                        Status = failure is null ? _snapshot.Menu.Status
                            : failure is OperationCanceledException ? "Adjustment queue stopped. Unsent adjustments were discarded; delivered changes were not undone."
                            : failure.Message + " Remaining queued adjustments were discarded; nothing will resume automatically."
                    } };
                }
            }
            Changed?.Invoke();
            foreach (var entry in remaining)
                entry.Completion.TrySetException(failure ?? new OperationCanceledException("Adjustment queue stopped."));
        }
    }

    // Caller holds _sync. Snapshot collections never expose the mutable queue.
    private void PublishNudgesLocked(MenuNudgeSession session) => _snapshot = _snapshot with
    {
        Menu = _snapshot.Menu with { NudgeQueue = new(session.Entries.Select(entry =>
            new IpMenuQueuedValue(entry.Draft.ControlId, entry.Draft.Target.GetValue<int>())).ToArray(), session.Accepting) }
    };

    private sealed class MenuNudgeSession(IpMenuSnapshot baseline)
    {
        public IpMenuSnapshot Baseline { get; } = baseline;
        public bool Accepting { get; set; } = true;
        public Dictionary<string, int> Expected { get; } = new(StringComparer.Ordinal);
        public List<MenuNudgeEntry> Entries { get; } = [];
    }
    private sealed class MenuNudgeEntry(IpMenuDraft draft)
    {
        public IpMenuDraft Draft { get; } = draft;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
