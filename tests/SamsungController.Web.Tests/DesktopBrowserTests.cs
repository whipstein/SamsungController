using SamsungController.Desktop;

namespace SamsungController.Web.Tests;

public sealed class DesktopBrowserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    public void NewAndExistingReadyServersOpenTheWebpageExactlyOnce(int? expectedProcessId)
    {
        var opened = new List<string>();
        DesktopBrowser.OpenReady(Ready(), DesktopFiles.Address(55123), true, opened.Add, expectedProcessId);
        Assert.Equal(new[] { "http://127.0.0.1:55123/" }, opened);
    }

    [Fact]
    public void ExplicitNoBrowserRetainsHeadlessAndCommandLineOperation()
    {
        var opened = new List<string>();
        DesktopBrowser.OpenReady(Ready(), DesktopFiles.Address(55123), false, opened.Add, 123);
        Assert.Empty(opened);
    }

    [Theory]
    [InlineData("Wrong product", true, true, 123)]
    [InlineData("SamsungController", false, true, 123)]
    [InlineData("SamsungController", true, false, 123)]
    [InlineData("SamsungController", true, true, 456)]
    public void UnrelatedForegroundDifferentVersionAndRacingServersNeverOpenBrowser(string product, bool managed, bool sameVersion, int processId)
    {
        var opened = new List<string>();
        var status = Ready() with { Product = product, Managed = managed, Version = sameVersion ? DesktopFiles.Version : "different", ProcessId = processId };
        Assert.Throws<InvalidOperationException>(() => DesktopBrowser.OpenReady(status, DesktopFiles.Address(55123), true, opened.Add, 123));
        Assert.Empty(opened);
    }

    [Fact]
    public void InstallerGuardAllowsMultipleServerHandlesAndIsWindowsOnly()
    {
        using var first = DesktopFiles.AcquireWindowsInstallationGuard();
        using var second = DesktopFiles.AcquireWindowsInstallationGuard();
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.True(Mutex.TryOpenExisting(DesktopFiles.WindowsServerMutex, out var existing));
            existing?.Dispose();
        }
        else { Assert.Null(first); Assert.Null(second); }
    }

    private static DesktopStatus Ready() => new(DesktopFiles.Product, DesktopFiles.Version, true, "instance", 123);
}
