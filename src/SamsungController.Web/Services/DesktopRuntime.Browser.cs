using System.Net;
using System.Text.Json;
using SamsungController.Desktop;

namespace SamsungController.Web.Services;

public sealed partial class DesktopRuntime
{
    private readonly DesktopBrowserTabs _browserTabs = new();

    private void MapBrowserEndpoints(WebApplication app)
    {
        app.MapGet("/_app/browser", ListenForBrowserReopenAsync);
        app.MapPost("/_app/browser/ack", AcknowledgeBrowserReopen);
        app.MapPost(DesktopFiles.ReopenRoute, RequestBrowserReopenAsync);
    }

    private bool IsLocalBrowser(HttpContext context, bool requireOrigin)
    {
        if (!Enabled || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return false;
        var origin = context.Request.Headers.Origin;
        return origin.Count == 0 ? !requireOrigin : origin.Count == 1 && origin[0] == $"{context.Request.Scheme}://{context.Request.Host}";
    }

    public async Task ListenForBrowserReopenAsync(HttpContext context)
    {
        if (!IsLocalBrowser(context, requireOrigin: false)) { context.Response.StatusCode = 403; return; }
        using var subscription = _browserTabs.Subscribe(context.Request.Query["tab"].ToString());
        if (subscription is null) { context.Response.StatusCode = 400; return; }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.ApplicationStopping);
        try
        {
            // Kept by the browser across server restarts, unlike the instance-scoped
            // quit stream. Reconnection alone never refreshes the UI or sends TV keys.
            await context.Response.WriteAsync("retry: 500\n: SamsungController tab reuse\n\n", canceled.Token);
            await context.Response.Body.FlushAsync(canceled.Token);
            await foreach (var request in subscription.Reader.ReadAllAsync(canceled.Token))
            {
                var data = JsonSerializer.Serialize(new { instance = Instance, request });
                await context.Response.WriteAsync($"event: reopen\ndata: {data}\n\n", canceled.Token);
                await context.Response.Body.FlushAsync(canceled.Token);
            }
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
    }

    public IResult AcknowledgeBrowserReopen(HttpContext context)
    {
        // Only the same-origin tab with this live subscription and fresh request
        // can claim reuse. No shutdown token is exposed to JavaScript.
        if (!IsLocalBrowser(context, requireOrigin: true) || context.Request.Query["instance"] != Instance)
            return Results.StatusCode(403);
        return _browserTabs.Acknowledge(context.Request.Query["tab"].ToString(), context.Request.Query["request"].ToString())
            ? Results.NoContent() : Results.StatusCode(409);
    }

    public async Task RequestBrowserReopenAsync(HttpContext context)
    {
        // Only the native launcher may ask a tab to focus/reload. A website cannot
        // trigger this endpoint via a form, fetch, image, or cross-origin navigation.
        if (!IsLocalBrowser(context, requireOrigin: false) || context.Request.Headers.ContainsKey("Origin")
            || !CanAuthorizeStop(context.Request.Headers.Authorization))
        { context.Response.StatusCode = 403; return; }
        if (!_ready || _quitRequested.Task.IsCompleted) { context.Response.StatusCode = 503; return; }
        context.Response.Headers.CacheControl = "no-store";
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.ApplicationStopping);
        var reused = await _browserTabs.ReopenAsync(TimeSpan.FromMilliseconds(1250), canceled.Token);
        await context.Response.WriteAsJsonAsync(new DesktopBrowserReuse(Instance, reused), canceled.Token);
    }
}

