using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Mutation-testing gaps in DragDropInputService, SiblingFolderService, ImageFileTypes and ExplorerSnapshotValidator.</summary>
public sealed class CatalogMutationGapTests
{
    [Theory]
    [InlineData(DragDropInputKind.Folder, "C:\\x", true)]
    [InlineData(DragDropInputKind.Image, "C:\\x", true)]
    [InlineData(DragDropInputKind.Folder, null, false)]
    [InlineData(DragDropInputKind.Image, null, false)]
    [InlineData(DragDropInputKind.Invalid, "C:\\x", false)]
    [InlineData(DragDropInputKind.Invalid, null, false)]
    public void DragDropResult_IsValid_NeedsAKindOtherThanInvalidAndAFolder(DragDropInputKind kind, string? folder, bool expected)
    {
        Assert.Equal(expected, new DragDropInputResult(kind, folder, null, 0, null).IsValid);
    }

    [Fact]
    public void DragDropParse_SingleFolder_HasNoWarningAndNothingIgnored()
    {
        using var root = new TempRoot("dragdrop-gap");
        var folder = root.Dir("only");

        var result = DragDropInputService.Parse([folder]);

        Assert.Equal(DragDropInputKind.Folder, result.Kind);
        Assert.Equal(0, result.IgnoredPathCount);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void DragDropParse_SameFolderTwiceInDifferentCase_IsNotCountedAsIgnored()
    {
        using var root = new TempRoot("dragdrop-gap");
        var folder = root.Dir("Photos");

        var result = DragDropInputService.Parse([folder, folder.ToUpperInvariant()]);

        Assert.Equal(0, result.IgnoredPathCount);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void DragDropParse_FolderPlusOtherPaths_IgnoresThemAndWarnsOnlyTheFirstFolderIsUsed()
    {
        using var root = new TempRoot("dragdrop-gap");
        var first = root.Dir("first");
        var second = root.Dir("second");
        var file = root.File(Path.Combine("first", "a.jpg"), 1);

        var result = DragDropInputService.Parse([first, second, file]);

        Assert.Equal(DragDropInputKind.Folder, result.Kind);
        Assert.Equal(first, result.FolderPath);
        Assert.Equal(2, result.IgnoredPathCount);
        Assert.Equal(Tr.CoreDragDropOnlyFirstFolder, result.Warning);
    }

    [Fact]
    public void DragDropParse_SeveralImagesOfOneFolder_AcceptsThemAllWithoutWarning()
    {
        using var root = new TempRoot("dragdrop-gap");
        var a = root.File(Path.Combine("shots", "a.jpg"), 1);
        var b = root.File(Path.Combine("shots", "b.png"), 1);

        var result = DragDropInputService.Parse([a, b]);

        Assert.Equal(DragDropInputKind.Image, result.Kind);
        Assert.Equal(a, result.InitialImagePath);
        Assert.Equal(0, result.IgnoredPathCount);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void DragDropParse_ImageAndUnsupportedFile_CountsTheUnsupportedOneAsIgnoredAndWarns()
    {
        using var root = new TempRoot("dragdrop-gap");
        var a = root.File(Path.Combine("shots", "a.jpg"), 1);
        var note = root.File(Path.Combine("shots", "notes.txt"), 1);
        var missing = root.Combine("shots", "gone.jpg");

        var result = DragDropInputService.Parse([a, note, missing]);

        Assert.Equal(DragDropInputKind.Image, result.Kind);
        Assert.Equal(2, result.IgnoredPathCount);
        Assert.Equal(Tr.CoreDragDropUnsupportedIgnored, result.Warning);
    }

    [Fact]
    public void DragDropParse_ImagesFromTwoFolders_KeepsTheFirstFolderAndWarnsAboutMixedFolders()
    {
        using var root = new TempRoot("dragdrop-gap");
        var a = root.File(Path.Combine("one", "a.jpg"), 1);
        var b = root.File(Path.Combine("two", "b.jpg"), 1);

        var result = DragDropInputService.Parse([a, b]);

        Assert.Equal(DragDropInputKind.Image, result.Kind);
        Assert.Equal(Path.GetDirectoryName(a), result.FolderPath);
        Assert.Equal(1, result.IgnoredPathCount);
        Assert.Equal(Tr.CoreDragDropMixedFolders, result.Warning);
    }

    [Fact]
    public void SiblingFolders_ParentCannotBeListed_ReturnsEmptyAndLogsTheError()
    {
        using var root = new TempRoot("sibling-gap");
        var log = new MutationRecordingLog();
        var current = root.Combine("missing-parent", "child");

        var siblings = SiblingFolderService.GetSorted(current, log);

        Assert.Empty(siblings);
        var error = Assert.Single(log.Errors);
        Assert.Contains(root.Combine("missing-parent"), error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(error.Exception);
    }

    [Fact]
    public void SiblingFolders_ParentCannotBeListedAndNoLogGiven_StillReturnsEmpty()
    {
        using var root = new TempRoot("sibling-gap");

        Assert.Empty(SiblingFolderService.GetSorted(root.Combine("missing-parent", "child")));
    }

    [Fact]
    public void IsRawPath_StringOverload_HandlesNullAndRawAndNonRawNames()
    {
        string? none = null;
        string raw = "x.cr2";
        string jpg = "x.jpg";

        Assert.False(ImageFileTypes.IsRawPath(none));
        Assert.True(ImageFileTypes.IsRawPath(raw));
        Assert.True(ImageFileTypes.IsRawPath(@"C:\shots\IMG_1.DNG"));
        Assert.False(ImageFileTypes.IsRawPath(jpg));
        Assert.False(ImageFileTypes.IsRawPath(""));
    }

    [Fact]
    public void ExplorerSnapshotValidator_NullSnapshot_IsAnIncompleteSnapshotNotACrash()
    {
        var valid = ExplorerSnapshotValidator.TryValidate(null!, [], out var ordered, out var reason);

        Assert.False(valid);
        Assert.Empty(ordered);
        Assert.Equal(ExplorerReason.IncompleteSnapshot, reason);
    }

    [Fact]
    public void ExplorerSnapshotValidator_NullScannedFiles_IsAnIncompleteSnapshotNotACrash()
    {
        var snapshot = new ExplorerViewSnapshot(@"C:\photos", [], [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, null!, out var ordered, out var reason);

        Assert.False(valid);
        Assert.Empty(ordered);
        Assert.Equal(ExplorerReason.IncompleteSnapshot, reason);
    }

    [Theory]
    [InlineData(@"\\?\C:", "C:")]
    [InlineData(@"\\?\C:\photos\a.jpg", @"C:\photos\a.jpg")]
    [InlineData(@"\\?\UNC\srv\share\a.jpg", @"\\srv\share\a.jpg")]
    [InlineData(@"\\?\x", @"\\?\x")]
    [InlineData(@"\\?\Volume{1}\a.jpg", @"\\?\Volume{1}\a.jpg")]
    [InlineData(@"C:\ab:c", @"C:\ab:c")]
    [InlineData(@"C:\photos\a.jpg", @"C:\photos\a.jpg")]
    public void NormalizePath_StripsTheExtendedPrefixOnlyFromDriveLetterPaths(string input, string expected)
    {
        Assert.Equal(expected, ExplorerSnapshotValidator.NormalizePath(input));
    }
}