using SamsungController.Web.Components;
using SamsungController.Web.Services;
using SamsungController.Desktop;

var desktop = DesktopRuntime.ParseArguments(args);
if (desktop.Enabled) DesktopRuntime.PrepareBackgroundProcess(desktop.Port);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = desktop.Arguments,
    ContentRootPath = desktop.Enabled ? AppContext.BaseDirectory : null
});
if (desktop.Enabled) builder.WebHost.UseUrls(DesktopFiles.Address(desktop.Port).ToString());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<SamsungControllerService>();
builder.Services.AddSingleton<SamsungIpRemoteService>();
builder.Services.AddSingleton(services => new DesktopRuntime(desktop.Enabled, desktop.Port,
    builder.Configuration["SamsungController:ConfigurationDirectory"] ?? WebApplicationPaths.GetDefaultConfigurationDirectory(),
    services.GetRequiredService<IHostApplicationLifetime>()));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/_app"), branch =>
    branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/guide-assets/{**name}", (string name) => UserGuide.Image(name) is { } bytes
    ? Results.File(bytes, "image/png") : Results.NotFound());
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Services.GetRequiredService<DesktopRuntime>().MapEndpoints(app);

app.Run();
