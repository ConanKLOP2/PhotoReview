using System.Threading;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PlatformPrimitivesTests
{
    [Fact(DisplayName = "WindowsMemoryProbe reports realistic OS memory metrics")]
    public void WindowsMemoryProbeReportsRealisticMetrics()
    {
        var probe = WindowsMemoryProbe.Instance;
        var snapshot = probe.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Value.LoadPercent <= 100);
        Assert.True(snapshot.Value.AvailableBytes > 0);
        Assert.True(probe.HasHeadroom(1.0, 0));
    }

    [Fact(DisplayName = "WindowsNaturalComparer orders strings naturally with numbers")]
    public void WindowsNaturalComparerOrdersNaturally()
    {
        var comparer = WindowsNaturalComparer.Instance;

        Assert.True(comparer.Compare("photo1.jpg", "photo2.jpg") < 0);
        Assert.True(comparer.Compare("photo2.jpg", "photo10.jpg") < 0);
        Assert.True(comparer.Compare("photo10.jpg", "photo2.jpg") > 0);
        Assert.Equal(0, comparer.Compare("PHOTO1.JPG", "photo1.jpg"));
        Assert.True(comparer.Compare(null, "a") < 0);
        Assert.True(comparer.Compare("a", null) > 0);
        Assert.Equal(0, comparer.Compare(null, null));
    }

    [Fact(DisplayName = "WindowsRecycleBin rejects invalid arguments")]
    public void WindowsRecycleBinRejectsInvalidArguments()
    {
        var bin = WindowsRecycleBin.Instance;

        Assert.Throws<ArgumentNullException>(() => bin.SendToRecycleBin(null!));
        Assert.Throws<ArgumentException>(() => bin.SendToRecycleBin(""));
        Assert.Throws<ArgumentNullException>(() => bin.TryRestore(null!, 0, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => bin.TryRestore("", 0, DateTime.UtcNow));
    }

    [Fact(DisplayName = "WindowsDarkTitleBar rejects a null handle and accepts a real window handle")]
    public void WindowsDarkTitleBarEnablesDarkModeOnARealHandle()
    {
        Assert.False(WindowsDarkTitleBar.TryEnableDarkMode(IntPtr.Zero));
        Assert.False(WindowsDarkTitleBar.TrySetCaptionColor(IntPtr.Zero, 0));

        Exception? threadException = null;
        bool darkModeAccepted = false, captionColorAccepted = false;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window { Width = 1, Height = 1, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                try
                {
                    // Forces native HWND creation (SourceInitialized) without ever showing the window.
                    var handle = new WindowInteropHelper(window).EnsureHandle();
                    darkModeAccepted = WindowsDarkTitleBar.TryEnableDarkMode(handle);
                    captionColorAccepted = WindowsDarkTitleBar.TrySetCaptionColor(handle, 0x00171717);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Window handle creation timed out.");
        Assert.Null(threadException);

        // Both DWM attributes are supported on the CI/dev machines this suite runs on (Windows 10
        // 20H1+/Windows 11); an older Windows build is expected to make DwmSetWindowAttribute fail,
        // which TryEnableDarkMode/TrySetCaptionColor turn into `false` instead of throwing.
        Assert.True(darkModeAccepted, "DWMWA_USE_IMMERSIVE_DARK_MODE was rejected by DWM on this machine.");
        Assert.True(captionColorAccepted, "DWMWA_CAPTION_COLOR was rejected by DWM on this machine.");
    }

    // Split from the argument checks: a miss makes TryRestore enumerate the user's real Recycle Bin through
    // Shell COM, so its cost scales with the bin (measured 5-10 s) and it belongs with the Native tests.
    [Trait("Category", "Native")]
    [Fact(DisplayName = "WindowsRecycleBin.TryRestore returns false when nothing in the real bin matches")]
    public void WindowsRecycleBinTryRestoreReturnsFalseWhenNothingMatches()
    {
        var bin = WindowsRecycleBin.Instance;
        var fakePath = "C:\\nonexistent-folder-xyz\\nonexistent-file-123.jpg";
        var restored = bin.TryRestore(fakePath, 1234, DateTime.UtcNow);
        Assert.False(restored);
    }
}
