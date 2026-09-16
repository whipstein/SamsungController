using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SamsungController.Desktop;

namespace SamsungController.Web.Services;

public sealed partial class DesktopRuntime : IDisposable
{
    private readonly DesktopInstance _instance;
    private readonly string _directory;
    private readonly IHostApplicationLifetime _lifetime;
    private volatile bool _ready;
    private readonly TaskCompletionSource _quitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Enabled { get; }
    public string Instance => _instance.Instance;
    public DesktopRuntime(bool enabled, int port, string directory, IHostApplicationLifetime lifetime)
    {
        Enabled = enabled; _instance = DesktopFiles.CreateInstance(port); _directory = directory; _lifetime = lifetime;
        if (enabled) lifetime.ApplicationStarted.Register(() => { DesktopFiles.WriteInstance(_instance, _directory); _ready = true; });
    }
    public static (bool Enabled, int Port, string[] Arguments) ParseArguments(string[] args)
    {
        var enabled = false; var port = 5050; var remaining = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--desktop-server") enabled = true;
            else if (args[index] == "--desktop-port")
            {
                if (++index >= args.Length || !int.TryParse(args[index], out port)) throw new ArgumentException("Invalid desktop port.");
            }
            else remaining.Add(args[index]);
        }
        _ = DesktopFiles.Address(port);
        return (enabled, port, remaining.ToArray());
    }
    public static void PrepareBackgroundProcess(int port)
    {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsMacOS() &&
            Environment.GetEnvironmentVariable("SAMSUNG_CONTROLLER_BUNDLED_HOST") == "1")) _ = CreateSession();
        var path = Path.Combine(DesktopFiles.DirectoryPath(), port == 5050 ? "server.log" : $"server-{port}.log");
        // Only the background process writes this log; no pipe to the short-lived launcher.
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, options);
        var writer = TextWriter.Synchronized(new StreamWriter(file) { AutoFlush = true });
        Console.SetOut(writer); Console.SetError(writer); Console.SetIn(TextReader.Null);
        Console.WriteLine($"{DateTimeOffset.Now:u} SamsungController {DesktopFiles.Version} background server starting on port {port}.");
    }
    public bool CanAuthorizeStop(string? authorization)
    {
        const string prefix = "Bearer ";
        if (!Enabled || authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization[prefix.Length..]), Encoding.UTF8.GetBytes(_instance.Token));
    }
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet(DesktopFiles.StatusRoute, () => Enabled && !_ready ? Results.StatusCode(503) : Results.Json(
            new DesktopStatus(DesktopFiles.Product, DesktopFiles.Version, Enabled, _instance.Instance, Environment.ProcessId)));
        if (!Enabled) return;
        MapBrowserEndpoints(app);
        app.MapGet("/_app/events", NotifyBrowserOnQuitAsync);
        app.MapPost(DesktopFiles.StopRoute, (HttpContext context, SamsungIpRemoteService controller) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                context.Request.Headers.ContainsKey("Origin") || !CanAuthorizeStop(context.Request.Headers.Authorization)) return Results.StatusCode(403);
            if (controller.GetSnapshot().IsBusy) return Results.Conflict("Stop the running TV operation first.");
            context.Response.OnCompleted(StopWithBrowserNotificationAsync);
            return Results.Ok(new { stopped = true });
        });
    }
    public async Task QuitAsync(SamsungIpRemoteService controller)
    {
        if (!Enabled) throw new InvalidOperationException("This is a foreground server; stop it in its terminal.");
        if (controller.GetSnapshot().IsBusy) throw new InvalidOperationException("Stop the running TV operation before quitting the app.");
        await StopWithBrowserNotificationAsync();
    }
    // This read-only stream is scoped to this local server instance. It contains
    // no credentials and cannot initiate shutdown. Only an explicit authorized
    // Quit (UI, native app, or launcher --stop) emits an event, never a crash.
    public async Task NotifyBrowserOnQuitAsync(HttpContext context)
    {
        if (!Enabled || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || context.Request.Query["instance"] != Instance
            || context.Request.Headers.Origin is { Count: > 0 } origin && origin != $"{context.Request.Scheme}://{context.Request.Host}")
        { context.Response.StatusCode = 403; return; }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.ApplicationStopping);
        try
        {
            await context.Response.StartAsync(canceled.Token);
            await context.Response.WriteAsync(": SamsungController quit notification\n\n", canceled.Token);
            await context.Response.Body.FlushAsync(canceled.Token);
            await _quitRequested.Task.WaitAsync(canceled.Token);
            await context.Response.WriteAsync($"event: quit\ndata: {Instance}\n\n", canceled.Token);
            await context.Response.Body.FlushAsync(canceled.Token);
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
    }
    private async Task StopWithBrowserNotificationAsync()
    {
        _quitRequested.TrySetResult();
        // Give active tabs time to receive the explicit quit event before the
        // server/circuit disconnects. A blocked tab close never blocks quitting.
        await Task.Delay(300);
        _lifetime.StopApplication();
    }
    public void Dispose()
    {
        _browserTabs.Dispose();
        if (!Enabled || !_ready) return;
        try { DesktopFiles.RemoveInstance(_instance, _directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)] private static extern int CreateSession();
}
