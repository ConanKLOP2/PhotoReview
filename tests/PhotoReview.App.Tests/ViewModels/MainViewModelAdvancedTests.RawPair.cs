using System.Threading.Tasks;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
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
        Assert.Equal(Tr.MainCapturePairBadgeJpeg, vm.CapturePairBadge);

        var badgeChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CapturePairBadge)) badgeChanges++; };

        await vm.ToggleCaptureGroupMemberAsync();

        Assert.True(badgeChanges > 0);
        Assert.Equal(rawMemberFixture, vm.Presenter.CurrentPresentedPath);
        Assert.Equal(Tr.MainCapturePairBadgeRaw, vm.CapturePairBadge);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.Equal(1, vm.TotalFiles);

        await vm.ToggleCaptureGroupMemberAsync();

        Assert.Equal(jpeg, vm.Presenter.CurrentPresentedPath);
        Assert.Equal(Tr.MainCapturePairBadgeJpeg, vm.CapturePairBadge);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.Equal(1, vm.TotalFiles);
    }

    [Fact]
    public void CapturePairBadgeToolTip_WithoutABoundKey_SaysWhereToAssignOne()
    {
        var (vm, _) = CreateViewModel();

        Assert.Equal(string.Empty, _settings.Shortcuts.ToggleCaptureMember); // no default key (owner decision)
        Assert.Equal(Tr.MainCapturePairBadgeTooltipUnbound, vm.CapturePairBadgeToolTip);
    }

    [Theory]
    [InlineData("F4")]
    [InlineData(" J ")]
    public void CapturePairBadgeToolTip_WithABoundKey_NamesTheKey(string configured)
    {
        _settings.Shortcuts.ToggleCaptureMember = configured;
        var (vm, _) = CreateViewModel();

        Assert.Equal(Tr.MainCapturePairBadgeTooltipBound(configured.Trim()), vm.CapturePairBadgeToolTip);
        Assert.Contains(configured.Trim(), vm.CapturePairBadgeToolTip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("NotAKey")]
    public void CapturePairBadgeToolTip_WithAnUnusableKey_IsTheUnboundHint(string configured)
    {
        _settings.Shortcuts.ToggleCaptureMember = configured;
        var (vm, _) = CreateViewModel();

        Assert.Equal(Tr.MainCapturePairBadgeTooltipUnbound, vm.CapturePairBadgeToolTip);
    }

    [Fact]
    public void CapturePairBadgeToolTip_AfterSettingsAssignTheKey_IsRefreshed()
    {
        var (vm, _) = CreateViewModel();
        var before = vm.CapturePairBadgeToolTip;
        var changed = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CapturePairBadgeToolTip)) changed++; };
        _dialogService.OnShowSettings = () =>
        {
            _settings.Shortcuts.ToggleCaptureMember = "F4";
            _settingsStore.Save(_settings);
        };

        vm.ShowSettings();

        Assert.True(changed > 0);
        Assert.NotEqual(before, vm.CapturePairBadgeToolTip);
        Assert.Equal(Tr.MainCapturePairBadgeTooltipBound("F4"), vm.CapturePairBadgeToolTip);
    }

    [Fact]
    public void CapturePairBadge_WithoutCapturePair_IsEmpty()
    {
        var plain = CreateImageFile(_tempDir, "plain.jpg");
        _catalog.Reset([plain]);
        var (vm, _) = CreateViewModel();

        Assert.Equal(string.Empty, vm.CapturePairBadge);
    }

    [Fact]
    public async Task ToggleCompare_WhenPresentedPathBelongsToAnotherEntry_ComparesTheCurrentEntryWithoutThrowing()
    {
        var jpegA = CreateImageFile(_tempDir, "a.jpg");
        var rawA = CreateImageFile(_tempDir, "a-member.jpg", DifferentPngBytes);
        var jpegB = CreateImageFile(_tempDir, "b.jpg");
        var rawB = CreateImageFile(_tempDir, "b-member.jpg", DifferentPngBytes);
        _catalog.Reset(
        [
            new CatalogEntry(jpegA) { CaptureGroup = new CaptureGroup(jpegA, rawA) },
            new CatalogEntry(jpegB) { CaptureGroup = new CaptureGroup(jpegB, rawB) },
        ], RawPairMode.Separate);
        var (vm, _) = CreateViewModel();
        await vm.Presenter.PresentAsync(0);
        Assert.Equal(jpegA, vm.Presenter.CurrentPresentedPath);

        // The catalog moved on (probe/undo/etc.) while the presenter still reports entry A's path.
        _catalog.SetCurrent(1);
        vm.ToggleCompare();
        await vm.CompareToggleTask;

        Assert.True(vm.Compare.IsVisible);
        Assert.Equal(jpegB, vm.Compare.LeftPath);
        Assert.Equal(rawB, vm.Compare.RightPath);
    }
}
