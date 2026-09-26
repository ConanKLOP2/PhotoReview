using System.IO;

namespace PhotoReview.App.Tests.Services;

public sealed class WindowPlacementServiceTests
{
    [Theory(DisplayName = "Only normal and maximized show commands survive a placement restore")]
    [InlineData(0, 1)]   // SW_HIDE from a damaged file
    [InlineData(1, 1)]
    [InlineData(2, 1)]   // minimized
    [InlineData(3, 3)]
    [InlineData(7, 1)]   // minimize-no-activate
    [InlineData(-5, 1)]
    public void NormalizeShowCommand_AcceptsOnlyNormalAndMaximized(int input, int expected)
        => Assert.Equal(expected, WindowPlacementService.NormalizeShowCommand(input));

    [Theory(DisplayName = "R7-10: closing in fullscreen saves the pre-fullscreen state, not the fullscreen Maximized")]
    [InlineData(System.Windows.WindowState.Normal, 1)]
    [InlineData(System.Windows.WindowState.Maximized, 3)]
    public void ResolveShowCommand_InFullscreen_UsesStateBeforeFullscreen(System.Windows.WindowState before, int expected)
        => Assert.Equal(expected, WindowPlacementService.ResolveShowCommand(3, before));

    [Theory(DisplayName = "R7-10: outside fullscreen the window's own show command is kept (normalized)")]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(2, 1)]
    public void ResolveShowCommand_NotFullscreen_NormalizesCurrent(int current, int expected)
        => Assert.Equal(expected, WindowPlacementService.ResolveShowCommand(current, null));

    [Fact(DisplayName = "Placement save does not depend on a shared fixed .tmp name (another window may hold it)")]
    public void WriteAtomically_SharedLegacyTempLocked_StillWrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "placement.json");
            // Simulates a second window mid-write on the old shared temp name.
            using var other = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

            WindowPlacementService.WriteAtomically(path, "{\"ok\":1}");

            Assert.Equal("{\"ok\":1}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
