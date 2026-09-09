using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using SamsungController.Desktop;

return await DesktopLauncher.RunAsync(args);

internal static class DesktopLauncher
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var installationGuard = DesktopFiles.AcquireWindowsInstallationGuard();
        var showBrowser = !args.Contains("--no-browser", StringComparer.Ordinal);
        try
        {
            var port = 5050;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--port" && index + 1 < args.Length && int.TryParse(args[++index], out port)) continue;
                if (args[index] is "--no-browser" or "--stop" or "--wait-for-exit") continue;
                throw new ArgumentException("Usage: launcher [--port 5050] [--no-browser] [--stop]");
            }
            var address = DesktopFiles.Address(port);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
            if (args.Contains("--stop", StringComparer.Ordinal)) return await StopAsync(client, port);
            using var launchLock = await LockAsync(port);
            var existing = await ProbeAsync(client);
            if (existing is not null)
            {
                DesktopBrowser.OpenReady(existing, address, showBrowser, Open);
                return 0;
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "SamsungController.Web" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!File.Exists(executable)) throw new FileNotFoundException("The server executable is missing. Extract the complete application package.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--desktop-server");
            start.ArgumentList.Add("--desktop-port");
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var server = Process.Start(start) ?? throw new InvalidOperationException("The server could not start.");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (server.HasExited) throw new InvalidOperationException($"Server exited with code {server.ExitCode}. Port {port} may be in use. See desktop/server.log in your private application data folder.");
                var status = await ProbeAsync(client);
                if (status is not null)
                {
                    DesktopBrowser.OpenReady(status, address, showBrowser, Open, server.Id);
                    // The native Mac bundle stays alive as the server's responsible
                    // application. Release the startup lock before waiting so reopen
                    // and --stop can proceed. Other platforms retain detached startup.
                    launchLock.Dispose();
                    if (args.Contains("--wait-for-exit", StringComparer.Ordinal))
                    {
                        await server.WaitForExitAsync();
                        return server.ExitCode;
                    }
                    return 0;
                }
                await Task.Delay(150);
            }
            throw new TimeoutException($"Server startup timed out. It was not forcibly killed. Check desktop/server.log, then reopen the app or use --stop --port {port}.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or TimeoutException or HttpRequestException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            var message = "SamsungController could not complete the request.\n\n" + error.Message;
            var path = Path.Combine(DesktopFiles.DirectoryPath(), "last-launch-error.txt");
            using (var file = DesktopFiles.PrivateFile(path, FileMode.Create))
            using (var writer = new StreamWriter(file)) writer.WriteLine(message);
            Console.Error.WriteLine(message);
            if (showBrowser)
            {
                try { if (OperatingSystem.IsWindows()) _ = MessageBox(IntPtr.Zero, message, "SamsungController", 0x10); else Open(path); }
                catch (Exception displayError) when (displayError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return 1;
        }
    }
    private static async Task<FileStream> LockAsync(int port)
    {
        var path = Path.Combine(DesktopFiles.DirectoryPath(), $"launch-{port}.lock");
        for (var attempt = 0; ; attempt++)
        {
            try { return DesktopFiles.PrivateFile(path, FileMode.OpenOrCreate); }
            catch (IOException) when (attempt < 240) { await Task.Delay(250); }
        }
    }
    private static async Task<DesktopStatus?> ProbeAsync(HttpClient client)
    {
        try
        {
            using var response = await client.GetAsync(DesktopFiles.StatusRoute);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<DesktopStatus>();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { return null; }
    }
    private static async Task<int> StopAsync(HttpClient client, int port)
    {
        var status = await ProbeAsync(client);
        if (status is null) return 0;
        var instance = DesktopFiles.ReadInstance(port);
        if (status.Product != DesktopFiles.Product || !status.Managed || instance is null || instance.Instance != status.Instance || instance.ProcessId != status.ProcessId)
            throw new InvalidOperationException("This is not a background server owned by this application's private state. It was not stopped.");
        // Hold the verified process handle so successful --stop means it has exited,
        // including closing log files before an upgrade on Windows. Never kill it.
        using var server = Process.GetProcessById(status.ProcessId);
        using var request = new HttpRequestMessage(HttpMethod.Post, DesktopFiles.StopRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The server refused to quit. Stop any running TV operation first, then retry.");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var remaining = await ProbeAsync(client);
            if (remaining is null || remaining.Instance != instance.Instance)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await server.WaitForExitAsync(timeout.Token);
                return 0;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The server has not finished shutting down. Check its log before restarting.");
    }
    private static void Open(string target)
    {
        if (OperatingSystem.IsWindows()) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return; }
        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(target);
        Process.Start(start);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string message, string caption, uint type);
}
