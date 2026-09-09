using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using SamsungController.Desktop;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Components.Shared;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class DesktopRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(65536)]
    public void InvalidPortsAreRejected(int port) => Assert.Throws<ArgumentOutOfRangeException>(() => DesktopFiles.Address(port));

    [Fact]
    public void AddressIsAlwaysLoopbackAndCustomFlagsAreRemovedBeforeHostConfiguration()
    {
        var options = DesktopRuntime.ParseArguments(["--desktop-server", "--desktop-port", "55123", "--environment", "Production"]);
        Assert.True(options.Enabled);
        Assert.Equal(55123, options.Port);
        Assert.Equal("http://127.0.0.1:55123/", DesktopFiles.Address(options.Port).ToString());
        Assert.Equal(new[] { "--environment", "Production" }, options.Arguments);
        Assert.False(DesktopRuntime.ParseArguments([]).Enabled);
        Assert.Throws<ArgumentException>(() => DesktopRuntime.ParseArguments(["--desktop-port"]));
        Assert.Throws<ArgumentException>(() => DesktopRuntime.ParseArguments(["--desktop-port", "invalid"]));
    }

    [Fact]
    public void PrivateInstanceFilesUseFreshCredentialsAndOnlyTheirOwnerRemovesThem()
    {
        using var folder = new TemporaryFolder();
        var first = DesktopFiles.CreateInstance(55123);
        var second = DesktopFiles.CreateInstance(55123);
        Assert.NotEqual(first.Instance, second.Instance);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(64, first.Token.Length);
        DesktopFiles.WriteInstance(first, folder.Path);
        Assert.Equal(first, DesktopFiles.ReadInstance(55123, folder.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DesktopFiles.InstancePath(55123, folder.Path)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DesktopFiles.DirectoryPath(folder.Path)));
        }
        DesktopFiles.WriteInstance(second, folder.Path);
        DesktopFiles.RemoveInstance(first, folder.Path);
        Assert.Equal(second, DesktopFiles.ReadInstance(55123, folder.Path));
        DesktopFiles.RemoveInstance(second, folder.Path);
        Assert.Null(DesktopFiles.ReadInstance(55123, folder.Path));
    }

    [Fact]
    public void BackgroundMetadataAppearsOnlyAfterStartupAndIsRemovedOnDispose()
    {
        using var folder = new TemporaryFolder();
        using var lifetime = new TestLifetime();
        using (var runtime = new DesktopRuntime(true, 55123, folder.Path, lifetime))
        {
            Assert.Null(DesktopFiles.ReadInstance(55123, folder.Path));
            lifetime.Started.Cancel();
            var instance = Assert.IsType<DesktopInstance>(DesktopFiles.ReadInstance(55123, folder.Path));
            Assert.True(runtime.CanAuthorizeStop("Bearer " + instance.Token));
            Assert.False(runtime.CanAuthorizeStop(null));
            Assert.False(runtime.CanAuthorizeStop("Bearer wrong"));
            Assert.False(runtime.CanAuthorizeStop("Basic " + instance.Token));
        }
        Assert.Null(DesktopFiles.ReadInstance(55123, folder.Path));
    }

    [Fact]
    public async Task ForegroundServerCannotBeStoppedByDesktopControls()
    {
        using var folder = new TemporaryFolder();
        using var lifetime = new TestLifetime();
        using var fixture = await MenuFixture.CreateAsync();
        using var runtime = new DesktopRuntime(false, 55123, folder.Path, lifetime);
        lifetime.Started.Cancel();
        Assert.False(runtime.CanAuthorizeStop("Bearer anything"));
        Assert.Null(DesktopFiles.ReadInstance(55123, folder.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.QuitAsync(fixture.Service));
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task QuitStopsBackgroundHostWithoutSendingTVCommands()
    {
        using var folder = new TemporaryFolder();
        using var lifetime = new TestLifetime();
        using var fixture = await MenuFixture.CreateAsync();
        using var runtime = new DesktopRuntime(true, 55123, folder.Path, lifetime);
        lifetime.Started.Cancel();
        await runtime.QuitAsync(fixture.Service);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("menu")]
    [InlineData("diagnostics")]
    public async Task DesktopQuitIsInTheTopmostGlobalHeaderOnEveryPage(string route)
    {
        using var folder = new TemporaryFolder();
        using var lifetime = new TestLifetime();
        using var fixture = await MenuFixture.CreateAsync();
        using var runtime = new DesktopRuntime(true, 55123, folder.Path, lifetime);
        await using var services = PageServices(fixture, runtime, route);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString());
        var firstRow = html[..html.IndexOf("direct-header-context", StringComparison.Ordinal)];
        Assert.Contains(">Quit app</button>", firstRow);
        Assert.True(firstRow.IndexOf(">Stop</button>", StringComparison.Ordinal) < firstRow.IndexOf(">Quit app</button>", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, ">Quit app</button>"));
        var display = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<DisplaySetup>()).ToHtmlString());
        Assert.DoesNotContain(">Quit app</button>", display);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task ForegroundHeaderDoesNotOfferQuitAndDisconnectedDesktopButtonStillWorks()
    {
        using var folder = new TemporaryFolder();
        using var lifetime = new TestLifetime();
        using var fixture = await MenuFixture.CreateAsync();
        using var foreground = new DesktopRuntime(false, 55123, folder.Path, lifetime);
        await using (var services = PageServices(fixture, foreground, ""))
        await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString());
            Assert.DoesNotContain(">Quit app</button>", html);
        }
        using var background = new DesktopRuntime(true, 55123, folder.Path, lifetime);
        await using var desktopServices = PageServices(fixture, background, "diagnostics");
        await using var page = new IpRemotePageTests.IpPageRenderer(desktopServices, typeof(DesktopAppControls));
        await page.StartAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.Connected);
        await page.AssertDisabledAsync("Quit app", false);
        await page.ClickAsync("Quit app");
        await page.AssertDisabledAsync("Quit app", true);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Empty(fixture.Display.Requests);
    }

    private static ServiceProvider PageServices(MenuFixture fixture, DesktopRuntime runtime, string route) => new ServiceCollection()
        .AddLogging().AddSingleton(fixture.Service).AddSingleton(runtime).AddSingleton<IJSRuntime>(new NoJavaScript())
        .AddSingleton<NavigationManager>(new PageNavigation(route)).BuildServiceProvider();
    private sealed class PageNavigation : NavigationManager
    {
        public PageNavigation(string route) => Initialize("http://localhost/", "http://localhost/" + route);
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).ToString(); NotifyLocationChanged(false); }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "samsung-desktop-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        public CancellationTokenSource Started { get; } = new();
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopping.Token;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() { Started.Dispose(); _stopping.Dispose(); }
    }
}
