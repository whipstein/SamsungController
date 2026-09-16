using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class DesktopBrowserTabsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static string Id() => Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAcknowledgementReusesExistingOrReconnectingTab(bool reconnecting)
    {
        using var tabs = new DesktopBrowserTabs();
        var id = Id();
        using var existing = reconnecting ? null : tabs.Subscribe(id);
        var reopen = tabs.ReopenAsync(Deadline);
        using var arriving = reconnecting ? tabs.Subscribe(id) : null;
        var subscription = existing ?? arriving!;
        var request = await subscription.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.False(tabs.Acknowledge(Id(), request));
        Assert.False(tabs.Acknowledge(id, Id()));
        Assert.True(tabs.Acknowledge(id, request));
        Assert.True(await reopen.WaitAsync(Deadline));
        Assert.False(tabs.Acknowledge(id, request));
    }

    [Fact]
    public async Task OnlyOneTabCanWinAndLaterLaunchesNeedANewAcknowledgement()
    {
        using var tabs = new DesktopBrowserTabs();
        var firstId = Id(); var secondId = Id();
        using var first = tabs.Subscribe(firstId)!;
        using var second = tabs.Subscribe(secondId)!;
        var reopen = tabs.ReopenAsync(Deadline);
        var request = await first.Reader.ReadAsync();
        Assert.Equal(request, await second.Reader.ReadAsync());
        var queued = tabs.ReopenAsync(Deadline);
        Assert.True(tabs.Acknowledge(firstId, request));
        Assert.False(tabs.Acknowledge(secondId, request));
        Assert.True(await reopen.WaitAsync(Deadline));
        var next = await second.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.NotEqual(request, next);
        Assert.False(tabs.Acknowledge(firstId, request));
        Assert.True(tabs.Acknowledge(secondId, next));
        Assert.True(await queued.WaitAsync(Deadline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnresponsiveTabFallsBackAndCannotAcknowledgeAfterDeadline(bool hasTab)
    {
        using var tabs = new DesktopBrowserTabs();
        var id = Id();
        using var tab = hasTab ? tabs.Subscribe(id) : null;
        Assert.False(await tabs.ReopenAsync(TimeSpan.FromMilliseconds(25)).WaitAsync(Deadline));
        if (tab is not null) Assert.False(tabs.Acknowledge(id, await tab.Reader.ReadAsync()));
    }

    [Fact]
    public async Task ClosedTabCannotClaimReuseAndCanReconnectWithSameId()
    {
        using var tabs = new DesktopBrowserTabs();
        var id = Id();
        var first = tabs.Subscribe(id)!;
        var reopen = tabs.ReopenAsync(Deadline);
        var request = await first.Reader.ReadAsync();
        first.Dispose();
        Assert.False(tabs.Acknowledge(id, request));
        using var second = tabs.Subscribe(id)!;
        first.Dispose(); // A stale request cannot remove the replacement subscription.
        Assert.Equal(request, await second.Reader.ReadAsync());
        Assert.True(tabs.Acknowledge(id, request));
        Assert.True(await reopen);
    }

    [Fact]
    public async Task CancellationAndDisposalDoNotLeavePendingRequestsOrBlockNextLaunch()
    {
        using var tabs = new DesktopBrowserTabs();
        var id = Id();
        using var tab = tabs.Subscribe(id)!;
        using var canceled = new CancellationTokenSource();
        var reopen = tabs.ReopenAsync(Deadline, canceled.Token);
        var request = await tab.Reader.ReadAsync();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopen);
        Assert.False(tabs.Acknowledge(id, request));
        var next = tabs.ReopenAsync(Deadline);
        tabs.Dispose();
        Assert.False(await next.WaitAsync(Deadline));
        Assert.False(await tabs.ReopenAsync(Deadline));
        Assert.Null(tabs.Subscribe(Id()));
    }

    [Fact]
    public void SubscriptionsAreValidatedUniqueAndBounded()
    {
        using var tabs = new DesktopBrowserTabs();
        Assert.Null(tabs.Subscribe(""));
        Assert.Null(tabs.Subscribe("not-a-tab"));
        var id = Id();
        using var first = tabs.Subscribe(id);
        Assert.NotNull(first);
        Assert.Null(tabs.Subscribe(id));
        for (var index = 1; index < 32; index++) Assert.NotNull(tabs.Subscribe(Id()));
        Assert.Null(tabs.Subscribe(Id()));
        first.Dispose();
        Assert.NotNull(tabs.Subscribe(Id()));
    }
}
