using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed partial class FolderLoadCoordinatorTests
{
    // Q-FMT-WEBP-HEIC: the switch decides which files a folder load lists (decoding is the router's job, not the scan's).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_WebpHeicSwitch_DecidesWhetherWebpAndHeicFilesAreListed(bool enabled)
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        _fs.WriteAllTextAtomic(@"C:\photos\b.webp", "webp");
        _fs.WriteAllTextAtomic(@"C:\photos\c.HEIC", "heic");
        _fs.WriteAllTextAtomic(@"C:\photos\d.heif", "heif");
        _fs.WriteAllTextAtomic(@"C:\photos\e.avif", "avif"); // AVIF/JXL are deferred: never listed
        _fs.WriteAllTextAtomic(@"C:\photos\f.jxl", "jxl");
        _settingsStore.Current.WebpHeicSupportEnabled = enabled;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        var listed = _catalog.EntriesSnapshot().Select(entry => entry.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] expected = enabled
            ? [@"C:\photos\a.jpg", @"C:\photos\b.webp", @"C:\photos\c.HEIC", @"C:\photos\d.heif"]
            : [@"C:\photos\a.jpg"];
        Assert.Equal(expected, listed);
    }

    [Fact]
    public async Task LoadAsync_WebpHeicSwitchChangedWhileScanning_UsesTheSnapshotTakenAtLoadStart()
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        _fs.WriteAllTextAtomic(@"C:\photos\b.webp", "webp");
        _settingsStore.Current.WebpHeicSupportEnabled = true;
        _fs.OnEnumerateFiles = () => _settingsStore.Current.WebpHeicSupportEnabled = false;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(2, _catalog.Count);
    }
}
