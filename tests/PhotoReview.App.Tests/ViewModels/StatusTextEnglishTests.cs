using System;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// I18N L04: the status texts render from the English catalog when English is the UI language.
/// The assembly pins Vietnamese (LocalizationModuleInit); switching the process-wide localizer here
/// must not race with the Vietnamese assertions, hence the non-parallel "GlobalState" collection.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class StatusTextEnglishTests
{
    [Fact]
    public void ImageStatusMessages_RenderInEnglish()
    {
        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal("1/10", StatusFormatter.IndexOnly(0, 10));
            Assert.Equal("1/10 · 1.5 MB · Loading", StatusFormatter.Loading(0, 10, 1572864));
            Assert.Equal("1/10 · 1.5 MB · Loading full resolution", StatusFormatter.LoadingFullRes(0, 10, 1572864));
            Assert.Equal("1/10 · 1.5 MB · 1920×1080 · photo.jpg", StatusFormatter.WithDimensions(0, 10, 1572864, 1920, 1080, "photo.jpg"));
            Assert.Equal("1/10 · 1.5 MB · Zoom 1.25x", StatusFormatter.Zoom(0, 10, 1572864, 1.25));
            Assert.Equal("Image error: photo.jpg — File is corrupt", StatusFormatter.ImageError("photo.jpg", "File is corrupt"));
        }
    }

    [Fact]
    public void FolderAndActionMessages_RenderInEnglish()
    {
        using (TestLocalization.Use(TestLocalization.English))
        {
            Assert.Equal("No supported images were found in this folder.", StatusFormatter.NoSupportedImages());
            Assert.Equal("Could not open the folder: Access denied", StatusFormatter.FolderOpenFailed("Access denied"));
            Assert.Equal("Batch finished: 5 succeeded, 0 failed.", StatusFormatter.BatchDone(5, 0));
            Assert.Equal("Already at the last folder on this level.", StatusFormatter.SiblingFolderBoundary(1));
            Assert.Equal("Already at the first folder on this level.", StatusFormatter.SiblingFolderBoundary(-1));
            Assert.Equal("Could not run Move to Keep: Disk full", StatusFormatter.ActionFailed("Move to Keep", "Disk full"));
            Assert.Equal("Could not run Move to Keep: invalid operation.", StatusFormatter.ActionInvalidOperation("Move to Keep"));
        }
    }

    [Fact]
    public async Task CompareStatus_RendersInEnglish()
    {
        using (TestLocalization.Use(TestLocalization.English))
        {
            var vm = new CompareViewModel();
            Assert.Equal(" | hash off", vm.HashText);

            var loaded = await vm.LoadAsync(
                (@"C:\photos\left.jpg", @"C:\photos\right.jpg"),
                token: 1,
                isTokenCurrent: _ => true,
                loadImageAsync: _ => Task.FromResult<object?>(null),
                getHashAsync: path => Task.FromResult(path.EndsWith("left.jpg", StringComparison.Ordinal) ? "aa" : "bb"),
                compareSizeEnabled: true,
                compareHashEnabled: true,
                currentIndex: 0,
                totalFiles: 2,
                getFileSize: _ => 1048576);

            Assert.True(loaded);
            Assert.Equal(" | hash DIFFERENT", vm.HashText);
            Assert.Equal(
                "1/2 | Compare | left.jpg (1,048,576 B) ↔ right.jpg (1,048,576 B) | hash DIFFERENT | click to select",
                vm.StatusText);
        }
    }
}
