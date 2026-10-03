using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Notification and guard pins for <see cref="CompareViewModel"/> found by Stryker (mutation gaps).</summary>
[Trait("Category", "HotPath")]
public sealed class CompareViewModelMutationGapTests
{
    private static readonly (string Left, string Right) Pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");

    private static List<string?> Track(CompareViewModel vm)
    {
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return changed;
    }

    [Fact]
    public void LeftPath_WhenSet_AlsoRefreshesTheLeftSelectionFlag()
    {
        var vm = new CompareViewModel { SelectedPath = @"C:\a.jpg" };
        var changed = Track(vm);

        vm.LeftPath = @"C:\a.jpg";

        Assert.Contains(nameof(CompareViewModel.IsLeftSelected), changed);
        Assert.True(vm.IsLeftSelected);
    }

    [Fact]
    public void RightPath_WhenSet_AlsoRefreshesTheRightSelectionFlag()
    {
        var vm = new CompareViewModel { SelectedPath = @"C:\b.jpg" };
        var changed = Track(vm);

        vm.RightPath = @"C:\b.jpg";

        Assert.Contains(nameof(CompareViewModel.IsRightSelected), changed);
        Assert.True(vm.IsRightSelected);
    }

    [Fact]
    public void LeftSizeText_WhenSet_AlsoRefreshesThePairedSizeText()
    {
        var vm = new CompareViewModel();
        var changed = Track(vm);

        vm.LeftSizeText = " (1 byte)";

        Assert.Contains(nameof(CompareViewModel.SizeText), changed);
    }

    [Fact]
    public void RightSizeText_WhenSet_AlsoRefreshesThePairedSizeText()
    {
        var vm = new CompareViewModel();
        var changed = Track(vm);

        vm.RightSizeText = " (1 byte)";

        Assert.Contains(nameof(CompareViewModel.SizeText), changed);
    }

    [Fact]
    public async Task LoadAsync_WithAnInitialSelection_SelectsThatPathInsteadOfTheLeftOne()
    {
        var vm = new CompareViewModel();

        await vm.LoadAsync(Pair, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()), initialSelectedPath: Pair.Right);

        Assert.Equal(Pair.Right, vm.SelectedPath);
        Assert.True(vm.IsRightSelected);
        Assert.False(vm.IsLeftSelected);
    }

    [Fact]
    public async Task LoadAsync_WithHashDisabled_NeverCallsTheHashFunction()
    {
        var vm = new CompareViewModel();
        var hashed = new List<string>();

        await vm.LoadAsync(Pair, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: path => { hashed.Add(path); return Task.FromResult("h"); },
            compareHashEnabled: false);

        Assert.Empty(hashed);
        Assert.Equal(StatusFormatter.CompareHashText(null), vm.HashText);
    }

    [Fact]
    public async Task LoadAsync_WithHashEnabledButNoHashFunction_LeavesTheHashUndetermined()
    {
        var vm = new CompareViewModel();

        var success = await vm.LoadAsync(Pair, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: null, compareHashEnabled: true);

        Assert.True(success);
        Assert.Equal(StatusFormatter.CompareHashText(null), vm.HashText);
    }

    [Fact]
    public async Task LoadAsync_WhenTheSizeLookupThrows_OnlyLosesTheCosmeticSuffix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview_CmpSizeBad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(dir, "good.jpg");
            File.WriteAllBytes(good, new byte[10]);
            var vm = new CompareViewModel();

            // A NUL character makes FileInfo throw ArgumentException before any I/O.
            var success = await vm.LoadAsync((good, Path.Combine(dir, "bad\0name.jpg")), token: 1, isTokenCurrent: _ => true,
                loadImageAsync: _ => Task.FromResult<object?>(new object()), compareSizeEnabled: true);

            Assert.True(success);
            Assert.Equal(" (10 byte)", vm.LeftSizeText);
            Assert.Equal(string.Empty, vm.RightSizeText);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
