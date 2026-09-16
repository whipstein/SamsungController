using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SamsungController.Desktop;

public static class DesktopBrowser
{
    // The launcher calls this only after a successful readiness probe. A repeat
    // launch uses the same path, opening the existing server instead of a second.
    public static void OpenReady(DesktopStatus status, Uri address, bool showBrowser, Action<string> open, int? expectedProcessId = null)
    {
        if (status.Product != DesktopFiles.Product || !status.Managed || status.Version != DesktopFiles.Version)
            throw new InvalidOperationException("A different or foreground server is already using this address. Close it before starting this application. No process was stopped.");
        if (expectedProcessId is not null && status.ProcessId != expectedProcessId)
            throw new InvalidOperationException("Another server acquired this port. No processes were stopped. Quit the existing server and retry.");
        if (showBrowser) open(address.ToString());
    }

    public static async Task OpenReadyAsync(DesktopStatus status, Uri address, bool showBrowser, Action<string> open,
        Func<Task<bool>> tryReuse, int? expectedProcessId = null)
    {
        OpenReady(status, address, false, open, expectedProcessId);
        if (showBrowser && !await tryReuse()) open(address.ToString());
    }

    public static async Task<bool> TryReuseAsync(HttpClient client, DesktopStatus status, DesktopInstance? instance)
    {
        if (instance is null || instance.Instance != status.Instance || instance.ProcessId != status.ProcessId
            || client.BaseAddress != DesktopFiles.Address(instance.Port)) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DesktopFiles.ReopenRoute);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            var result = await response.Content.ReadFromJsonAsync<DesktopBrowserReuse>();
            return result is { Reused: true } && result.Instance == status.Instance;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return false; }
    }
}
