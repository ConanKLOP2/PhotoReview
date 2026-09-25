using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>The folder info block must never stay on its "searching" placeholder, whatever the sibling search throws.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // shares the ambient Localizer with the other overlay tests
public sealed class InfoOverlayFaultTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task SiblingSearchFault_ShowsTheFolderWithoutSiblings_NotAStuckPlaceholder(Type exceptionType)
    {
        var settings = new AppSettings { ShowInfoOverlay = true, ShowFolderInfo = true };
        var overlay = new InfoOverlayViewModel(
            () => settings,
            (_, _) => throw (Exception)Activator.CreateInstance(exceptionType, "boom")!);

        overlay.SetFolder(@"C:\photos\trip");
        Assert.Contains(Tr.MainFolderInfoPending, overlay.FolderInfoText, StringComparison.Ordinal); // placeholder while searching
        await overlay.PendingSiblings.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(Tr.MainFolderInfoPending, overlay.FolderInfoText, StringComparison.Ordinal);
        Assert.Equal(Tr.MainFolderInfoCurrent("trip"), overlay.FolderInfoText);
    }
}
