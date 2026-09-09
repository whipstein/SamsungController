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
}
