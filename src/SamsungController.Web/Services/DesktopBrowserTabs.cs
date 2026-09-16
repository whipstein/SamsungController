using System.Threading.Channels;

namespace SamsungController.Web.Services;

// A tab is reused only after a fresh acknowledgement, never from a saved flag,
// tab count, stale heartbeat or browser-profile inspection. No display commands.
public sealed class DesktopBrowserTabs : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Dictionary<string, Subscription> _tabs = new(StringComparer.Ordinal);
    private Pending? _pending;
    private bool _disposed;
    private sealed record Pending(string Id, TaskCompletionSource<bool> Completion);

    public Subscription? Subscribe(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        lock (_gate)
        {
            if (_disposed || _tabs.Count >= 32 || _tabs.ContainsKey(id)) return null;
            var subscription = new Subscription(this, id);
            _tabs.Add(id, subscription);
            if (_pending is { } pending) subscription.Events.Writer.TryWrite(pending.Id);
            return subscription;
        }
    }

    public async Task<bool> ReopenAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        await _requests.WaitAsync(cancellation);
        Pending? request = null;
        try
        {
            lock (_gate)
            {
                if (_disposed) return false;
                request = new(Guid.NewGuid().ToString("N"), new(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending = request;
                foreach (var tab in _tabs.Values) tab.Events.Writer.TryWrite(request.Id);
            }
            try { return await request.Completion.Task.WaitAsync(timeout, cancellation); }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    // Resolve a deadline/acknowledgement race under the same lock:
                    // never open a duplicate after telling a tab that it won.
                    if (_pending == request) _pending = null;
                    return request.Completion.Task.IsCompletedSuccessfully && request.Completion.Task.Result;
                }
            }
        }
        finally
        {
            lock (_gate) { if (_pending == request) _pending = null; }
            _requests.Release();
        }
    }

    public bool Acknowledge(string tabId, string requestId)
    {
        lock (_gate)
            return !_disposed && _tabs.ContainsKey(tabId) && _pending is { } pending
                && pending.Id == requestId && pending.Completion.TrySetResult(true);
    }

    private void Unsubscribe(Subscription tab)
    {
        lock (_gate)
        {
            if (_tabs.GetValueOrDefault(tab.Id) == tab) _tabs.Remove(tab.Id);
            tab.Events.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Completion.TrySetResult(false);
            foreach (var tab in _tabs.Values) tab.Events.Writer.TryComplete();
            _tabs.Clear();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly DesktopBrowserTabs _owner;
        internal string Id { get; }
        internal Channel<string> Events { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public ChannelReader<string> Reader => Events.Reader;
        internal Subscription(DesktopBrowserTabs owner, string id) { _owner = owner; Id = id; }
        public void Dispose() => _owner.Unsubscribe(this);
    }
}
