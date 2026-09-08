using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private MenuNudgeSession? _menuNudges;
    private const int MaximumQueuedNudges = 256;
    private const int MaximumIndexedBatchWrites = 256;

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
            if (session.Entries.Count >= MaximumQueuedNudges && !session.Entries.Any(entry => !entry.Sent && entry.Draft.ControlId == control.Id))
                return "The adjustment queue is full. Wait for some changes to finish.";
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
            entry = session.Entries.FirstOrDefault(item => !item.Sent && item.Draft.ControlId == controlId)!;
            if (entry is null)
            {
                entry = new(new(controlId, JsonValue.Create(target)!, JsonValue.Create(original)!,
                    MenuPrerequisites(session.Baseline, control, forEditing: true), session.Baseline.Input!, session.Baseline.PictureMode!));
                session.Entries.Add(entry);
            }
            else entry.Draft = entry.Draft with { Target = JsonValue.Create(target)! };
            session.Expected[controlId] = target;
            PublishNudgesLocked(session);
        }
        Changed?.Invoke();
        if (start) _ = RunMenuNudgesAsync(session);
        return entry.Completion.Task;
    }

    private async Task RunMenuNudgesAsync(MenuNudgeSession session)
    {
        Exception? failure = null;
        MenuNudgeBatch? batch = null;
        try
        {
            await RunMenuOperationAsync(async (profile, cancellation) =>
            {
                while (true)
                {
                    lock (_sync)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!session.Accepting) throw new OperationCanceledException("Adjustment queue stopped.");
                        if (session.Entries.Count == 0) { session.Accepting = false; break; }
                        batch = new(session, session.Entries[0]);
                    }
                    // Pending targets remain replaceable through preflight. Indexed
                    // RGB targets share a selector session, not an invented bulk RPC.
                    await ApplyMenuCoreAsync(profile, cancellation, queuedBatch: batch).ConfigureAwait(false);
                    foreach (var entry in batch.Completed) entry.Completion.TrySetResult();
                    batch = null;
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
                remaining = session.Entries.Concat(batch?.Completed ?? []).ToArray();
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

    private static string MenuNudgeGroup(IpMenuControl control) => control.IsIndexed ? "row:" + control.Section + "/" + control.IndexValue : "control:" + control.Id;

    private MenuNudgeEntry? NextMenuNudge(MenuNudgeBatch batch)
    {
        lock (_sync) return batch.Closed ? null : batch.Session.Entries.FirstOrDefault(entry => MenuNudgeGroup(IpMenuCatalog.Get(entry.Draft.ControlId)) == batch.Group);
    }

    private IpMenuDraft ClaimMenuNudge(MenuNudgeBatch batch, MenuNudgeEntry entry)
    {
        lock (_sync)
        {
            if (!batch.Session.Accepting || _operation?.IsCancellationRequested == true) throw new OperationCanceledException("Adjustment queue stopped.");
            // This is the handoff to the durable write journal. Later edits must
            // not mutate the target we are about to send or its expected readback.
            entry.Sent = true;
            return entry.Draft;
        }
    }

    private void CompleteMenuNudge(MenuNudgeBatch batch, MenuNudgeEntry entry)
    {
        lock (_sync)
        {
            batch.Session.Entries.Remove(entry);
            batch.Completed.Add(entry);
            // Bound the journal even if edits arrive continuously for one row.
            batch.Closed = !batch.IsIndexed || batch.Completed.Count >= MaximumIndexedBatchWrites
                || !batch.Session.Entries.Any(item => MenuNudgeGroup(IpMenuCatalog.Get(item.Draft.ControlId)) == batch.Group);
            PublishNudgesLocked(batch.Session);
        }
        Changed?.Invoke();
        // Earlier confirmed channels can finish while the group continues. The
        // last channel waits for final row/context verification and the journal.
        if (!batch.Closed) entry.Completion.TrySetResult();
    }

    private sealed class MenuNudgeSession(IpMenuSnapshot baseline)
    {
        public IpMenuSnapshot Baseline { get; } = baseline;
        public bool Accepting { get; set; } = true;
        public Dictionary<string, int> Expected { get; } = new(StringComparer.Ordinal);
        public List<MenuNudgeEntry> Entries { get; } = [];
    }
    private sealed class MenuNudgeEntry(IpMenuDraft draft)
    {
        public IpMenuDraft Draft { get; set; } = draft;
        public bool Sent { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class MenuNudgeBatch(MenuNudgeSession session, MenuNudgeEntry first)
    {
        public MenuNudgeSession Session { get; } = session;
        public string Group { get; } = MenuNudgeGroup(IpMenuCatalog.Get(first.Draft.ControlId));
        public bool IsIndexed { get; } = IpMenuCatalog.Get(first.Draft.ControlId).IsIndexed;
        public bool Closed { get; set; }
        public List<MenuNudgeEntry> Completed { get; } = [];
    }
}
