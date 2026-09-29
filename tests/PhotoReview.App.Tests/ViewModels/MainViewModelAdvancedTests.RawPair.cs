using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed partial class MainViewModelAdvancedTests
{
    [Fact]
    public async Task ToggleCaptureGroupMemberAsync_SwitchesDisplayedPathWithoutSplittingCatalogEntry()
    {
        var jpeg = CreateImageFile(_tempDir, "pair.jpg");
        var rawMemberFixture = CreateImageFile(_tempDir, "pair-member.jpg", DifferentPngBytes);
        var group = new CaptureGroup(jpeg, rawMemberFixture);
        _catalog.Reset([new CatalogEntry(jpeg) { CaptureGroup = group }], RawPairMode.Separate);
        var (vm, _) = CreateViewModel();

        Assert.True(vm.CurrentHasCapturePair);
        Assert.Equal("JPG+RAW", vm.CapturePairBadge);

        await vm.ToggleCaptureGroupMemberAsync();

        Assert.Equal(rawMemberFixture, vm.Presenter.CurrentPresentedPath);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.Equal(1, vm.TotalFiles);

        await vm.ToggleCaptureGroupMemberAsync();

        Assert.Equal(jpeg, vm.Presenter.CurrentPresentedPath);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.Equal(1, vm.TotalFiles);
    }
}
