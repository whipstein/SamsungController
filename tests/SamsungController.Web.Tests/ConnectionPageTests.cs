using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

// Dispatch real component events without a browser or a TV connection.
#pragma warning disable BL0006
public sealed class ConnectionPageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SamsungController.ConnectionPage-{Guid.NewGuid():N}");
    private SamsungControllerService _controller = null!;
    private ServiceProvider _services = null!;
    private PageRenderer _renderer = null!;
    private readonly TestNavigationManager _navigation = new();
    private string MenuPath => Path.Combine(_directory, "menu-definitions", "test.yaml");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MenuPath)!);
        await File.WriteAllTextAsync(MenuPath, """
            version: 1
            id: connection-test
            name: Connection Test Menu
            model: Test TV
            configurations:
              - id: standard
                name: Standard
              - id: game
                name: Game mode
            nodes:
              - id: normal-video
                label: Normal video
            """);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SamsungController:ConfigurationDirectory"] = _directory
        }).Build();
        _controller = new SamsungControllerService(configuration);
        await _controller.InitializeAsync();
        _services = new ServiceCollection().AddLogging().AddSingleton(_controller)
            .AddSingleton<NavigationManager>(_navigation).BuildServiceProvider();
        _renderer = new PageRenderer(_services);
    }

    [Fact]
    public async Task DefaultViewShowsSavedChoicesWithoutSetupFields()
    {
        await SeedDisplayAsync();
        await _renderer.StartAsync();

        Assert.Contains("Saved combinations", _renderer.Text);
        Assert.Contains("Office TV", _renderer.Text);
        Assert.DoesNotContain("Advanced connection settings", _renderer.Text);
        Assert.Empty(_renderer.Elements("input"));
        Assert.Single(_renderer.Elements("select"));
    }

    [Fact]
    public async Task FirstUseStaysOnSavedViewUntilCreationIsChosen()
    {
        await _renderer.StartAsync();

        Assert.Contains("No saved combinations yet.", _renderer.Text);
        Assert.Empty(_renderer.Elements("input"));
        await _renderer.ClickAsync("Create new combination");
        Assert.Contains("Advanced connection settings", _renderer.Text);
        Assert.NotEmpty(_renderer.Elements("input"));
        await _renderer.ClickAsync("Cancel");
        Assert.Empty(_renderer.Elements("input"));
        Assert.False(Directory.Exists(Path.Combine(_directory, "display-definitions")));
    }

    [Fact]
    public async Task EditingMenuAndCancellingDoesNotChangeActiveCombinationOrFiles()
    {
        var displayPath = await SeedDisplayAsync();
        var savedDisplay = await File.ReadAllTextAsync(displayPath);
        var savedMenu = await File.ReadAllTextAsync(MenuPath);
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Edit combination");
        await _renderer.ChangeAsync("Display name", "Unsaved name");
        await _renderer.ChangeAsync("Menu layout", "game");

        Assert.Equal("standard", _controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
        Assert.Equal("Office TV", _controller.GetSnapshot().DisplayName);
        await _renderer.ClickAsync("Cancel");
        Assert.Empty(_renderer.Elements("input"));
        Assert.Equal(savedDisplay, await File.ReadAllTextAsync(displayPath));
        Assert.Equal(savedMenu, await File.ReadAllTextAsync(MenuPath));
    }

    [Fact]
    public async Task SavingAnotherLayoutAddsACombinationAndPickerLoadsTheChosenLayout()
    {
        var displayPath = await SeedDisplayAsync();
        var savedMenu = await File.ReadAllTextAsync(MenuPath);
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Edit combination");
        await _renderer.ChangeAsync("Menu layout", "game");
        await _renderer.ClickAsync("Save changes");

        Assert.Equal("game", _controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
        Assert.Empty(_renderer.Elements("input"));
        var display = Assert.Single(await _controller.DiscoverDisplayDefinitionsAsync(), item => item.Path == displayPath);
        Assert.Equal(2, display.Menus.Count);
        var standard = Assert.Single(display.Menus, item => item.MenuConfigurationId == "standard");
        await _renderer.ChangeAsync("Display / menu combination", $"{displayPath}|{standard.Id}");
        Assert.Equal("standard", _controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
        Assert.Equal(savedMenu, await File.ReadAllTextAsync(MenuPath));
    }

    [Fact]
    public async Task CreatingCombinationUsesExistingMenuAndRequiresConfirmationForDuplicateDisplay()
    {
        var displayPath = await SeedDisplayAsync();
        var savedDisplay = await File.ReadAllTextAsync(displayPath);
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Create new combination");
        await _renderer.ChangeAsync("Display name", "Office TV");
        await _renderer.ChangeAsync("IP address", "192.0.2.55");
        await _renderer.ChangeAsync("Menu definition", MenuPath);
        await _renderer.ClickAsync("Save combination");

        Assert.Contains("A display with this ID already exists.", _renderer.Text);
        Assert.Equal(savedDisplay, await File.ReadAllTextAsync(displayPath));
        await _renderer.ClickAsync("Replace and save");
        Assert.Equal("192.0.2.55", _controller.GetSnapshot().Host);
        Assert.Empty(_renderer.Elements("input"));
    }

    [Fact]
    public async Task CreatingNewMenuSavesCombinationAndOpensMenuBuilder()
    {
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Create new combination");
        await _renderer.ChangeAsync("Display name", "New TV");
        await _renderer.ChangeAsync("IP address", "192.0.2.60");
        await _renderer.ChangeAsync("Define a new menu", true);
        await _renderer.ChangeAsync("TV model number", "TestModel");
        await _renderer.ChangeAsync("Firmware version", "1234");
        await _renderer.ClickAsync("Save & define menu");

        Assert.EndsWith("/menu/build", _navigation.Uri);
        var selected = Assert.Single(await _controller.DiscoverDisplayDefinitionsAsync(), item => item.IsActive);
        Assert.Equal("New TV", selected.Name);
        Assert.Equal("testmodel-1234", selected.MenuDefinitionId);
        Assert.EndsWith(".yaml", selected.ResolvedMenuDefinitionPath);
        Assert.Equal(selected.ResolvedMenuDefinitionPath, _controller.GetMenuNavigationSnapshot().DefinitionPath);
    }

    [Fact]
    public async Task SavedReadinessSettingsAreRestoredWhenSwitchingDisplays()
    {
        var first = await SeedDisplayAsync();
        var request = new DisplayDefinitionEditRequest("second-tv", "Second TV", "192.0.2.61", true, null, true,
            MenuPath, "connection-test", "game", 30, 5, 2800, 600);
        var second = await _controller.SaveDisplayDefinitionAsync(request);
        await _controller.SetDisplayDefinitionAsync(first);
        Assert.Equal(1500, _controller.GetSnapshot().PostConnectWarmupMilliseconds);

        await _controller.SetDisplayDefinitionAsync(second);

        var snapshot = _controller.GetSnapshot();
        Assert.Equal(30, snapshot.KeepAliveIntervalSeconds);
        Assert.Equal(5, snapshot.KeepAliveTimeoutSeconds);
        Assert.Equal(2800, snapshot.PostConnectWarmupMilliseconds);
        Assert.Equal(600, snapshot.ReconnectAfterIdleSeconds);
    }

    [Fact]
    public async Task MenuWithoutNamedLayoutsCanBeSavedAndLoaded()
    {
        await File.WriteAllTextAsync(MenuPath, """
            version: 1
            id: connection-test
            name: Simple Menu
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
            """);
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Create new combination");
        await _renderer.ChangeAsync("Display name", "Simple TV");
        await _renderer.ChangeAsync("IP address", "192.0.2.62");
        await _renderer.ChangeAsync("Menu definition", MenuPath);
        await _renderer.ClickAsync("Save combination");

        Assert.Empty(_renderer.Elements("input"));
        Assert.Contains("Connect to display", _renderer.Text);
        Assert.Equal("Simple Menu", _controller.GetMenuNavigationSnapshot().DefinitionName);
        Assert.Null(_controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
    }

    [Fact]
    public async Task InvalidSetupShowsErrorAndKeepsTheDraftForCorrection()
    {
        await _renderer.StartAsync();
        await _renderer.ClickAsync("Create new combination");
        await _renderer.ChangeAsync("Display name", "Incomplete TV");
        await _renderer.ChangeAsync("IP address", string.Empty);
        await _renderer.ClickAsync("Save combination");

        Assert.Contains("Enter a display name and IP address in step 1.", _renderer.Text);
        Assert.NotEmpty(_renderer.Elements("input"));
        Assert.Null(_controller.GetSnapshot().DisplayDefinitionPath);
        Assert.False(Directory.Exists(Path.Combine(_directory, "display-definitions")));
    }

    private async Task<string> SeedDisplayAsync()
    {
        var path = await _controller.SaveDisplayDefinitionAsync(new DisplayDefinitionEditRequest(
            "office-tv", "Office TV", "192.0.2.50", true, null, true, MenuPath, "connection-test", "standard",
            20, 10, 1500, 300));
        await _controller.SetDisplayDefinitionAsync(path);
        return path;
    }

    public async Task DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _controller.DisposeAsync();
        await _services.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class PageRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int _rootId;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames
        {
            get
            {
                var frames = GetCurrentRenderTreeFrames(_rootId);
                return frames.Array.Take(frames.Count).ToArray();
            }
        }
        public string Text => FrameText(Frames);
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            _rootId = AssignRootComponentId(InstantiateComponent(typeof(Home)));
            await RenderRootComponentAsync(_rootId);
        });
        public IEnumerable<RenderTreeFrame[]> Elements(string tag) => Frames.Select((frame, index) => (frame, index))
            .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == tag)
            .Select(item => Frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray());
        public Task ClickAsync(string button) => DispatchAsync(
            Elements("button").First(frames => FrameText(frames).Trim().StartsWith(button, StringComparison.Ordinal)),
            "onclick", new MouseEventArgs());
        public Task ChangeAsync(string label, object value) => DispatchAsync(
            Elements("label").First(frames => FrameText(frames).Trim().StartsWith(label, StringComparison.Ordinal)),
            "onchange", new ChangeEventArgs { Value = value });
        private Task DispatchAsync(RenderTreeFrame[] frames, string eventName, EventArgs args) => Dispatcher.InvokeAsync(async () =>
        {
            var handler = frames.First(frame => frame.FrameType == RenderTreeFrameType.Attribute
                && frame.AttributeName == eventName).AttributeEventHandlerId;
            await DispatchEventAsync(handler, null, args);
        });
        private static string FrameText(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(frame.MarkupContent, "<[^>]+>", string.Empty)),
            _ => string.Empty
        }));
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Connection page render failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
#pragma warning restore BL0006
