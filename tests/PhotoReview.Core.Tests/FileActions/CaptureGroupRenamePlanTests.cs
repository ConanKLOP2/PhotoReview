using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class CaptureGroupRenamePlanTests
{
    private const string Dir = @"C:\Shots\";

    private static CaptureGroup Group(string jpg = "IMG.jpg", string raw = "IMG.cr2", string? xmp = null) =>
        new(Dir + jpg, Dir + raw, xmp is null ? null : Dir + xmp);

    [Fact]
    public void Build_JpegAndRawWithoutXmp_MapsBothMembers()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal(2, plan.FileNames.Count);
        Assert.Equal("new.jpg", plan.FileNames[Dir + "IMG.jpg"]);
        Assert.Equal("new.cr2", plan.FileNames[Dir + "IMG.cr2"]);
        Assert.Equal("new.jpg", plan.Check.NewFileName);
        Assert.False(plan.Check.IsUnchanged);
        Assert.False(plan.Check.DiffersOnlyByCase);
    }

    [Fact]
    public void Build_XmpNamedAfterBase_FollowsStem()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.xmp"), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal(3, plan.FileNames.Count);
        Assert.Equal("new.xmp", plan.FileNames[Dir + "IMG.xmp"]);
    }

    [Fact]
    public void Build_XmpNamedAfterRaw_FollowsRawFileName()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.cr2.xmp"), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("new.cr2.xmp", plan.FileNames[Dir + "IMG.cr2.xmp"]);
        Assert.Equal("new.cr2", plan.FileNames[Dir + "IMG.cr2"]);
    }

    [Fact]
    public void Build_XmpNamedAfterJpeg_FollowsJpegFileName()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.jpg.xmp"), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("new.jpg.xmp", plan.FileNames[Dir + "IMG.jpg.xmp"]);
    }

    [Fact]
    public void Build_XmpOwnerComparedIgnoringCase_IsMapped()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "img.CR2.XMP"), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("new.cr2.XMP", plan.FileNames[Dir + "img.CR2.XMP"]);
    }

    [Theory]
    [InlineData("other.xmp")]
    [InlineData("IMG.nef.xmp")]
    [InlineData("IMG_x.xmp")]
    public void Build_XmpNotMatchingAnyMember_ReturnsSidecarNotMappable(string xmp)
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: xmp), "new");

        Assert.Equal(RenameNameProblem.SidecarNotMappable, plan.Check.Problem);
        Assert.False(plan.Check.IsValid);
        Assert.Null(plan.FileNames);
    }

    [Fact]
    public void Build_UpperCaseExtensions_AreKept()
    {
        var plan = CaptureGroupRenamePlan.Build(Group("IMG.JPG", "IMG.CR2", "IMG.XMP"), "new");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("new.JPG", plan.FileNames[Dir + "IMG.JPG"]);
        Assert.Equal("new.CR2", plan.FileNames[Dir + "IMG.CR2"]);
        Assert.Equal("new.XMP", plan.FileNames[Dir + "IMG.XMP"]);
    }

    [Theory]
    [InlineData("new.jpg")]
    [InlineData("new.JPG")]
    [InlineData("  new.jpg ")]
    public void Build_TypedJpegExtension_IsStripped(string typed)
    {
        var plan = CaptureGroupRenamePlan.Build(Group(), typed);

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("new.jpg", plan.FileNames[Dir + "IMG.jpg"]);
        Assert.Equal("new.cr2", plan.FileNames[Dir + "IMG.cr2"]);
    }

    [Fact]
    public void Build_TypedRawExtension_StaysPartOfStemForWholeGroup()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.xmp"), "a.cr2");

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("a.cr2.jpg", plan.FileNames[Dir + "IMG.jpg"]);
        Assert.Equal("a.cr2.cr2", plan.FileNames[Dir + "IMG.cr2"]);
        Assert.Equal("a.cr2.xmp", plan.FileNames[Dir + "IMG.xmp"]);
    }

    [Theory]
    [InlineData("a:b", RenameNameProblem.InvalidCharacter)]
    [InlineData("", RenameNameProblem.Empty)]
    [InlineData(".jpg", RenameNameProblem.Empty)]
    [InlineData("a.", RenameNameProblem.TrailingDotOrSpace)]
    [InlineData("CON", RenameNameProblem.ReservedDeviceName)]
    public void Build_InvalidName_ReturnsProblemAndNoMap(string typed, RenameNameProblem expected)
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.xmp"), typed);

        Assert.False(plan.Check.IsValid);
        Assert.Equal(expected, plan.Check.Problem);
        Assert.Null(plan.FileNames);
    }

    [Fact]
    public void Build_OnlyCaseDiffers_IsCaseOnlyOnPlan()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.xmp"), "img");

        Assert.True(plan.Check.IsValid);
        Assert.True(plan.Check.DiffersOnlyByCase);
        Assert.False(plan.Check.IsUnchanged);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("img.jpg", plan.FileNames[Dir + "IMG.jpg"]);
        Assert.Equal("img.cr2", plan.FileNames[Dir + "IMG.cr2"]);
        Assert.Equal("img.xmp", plan.FileNames[Dir + "IMG.xmp"]);
    }

    [Fact]
    public void Build_SameName_IsUnchanged()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(xmp: "IMG.xmp"), "IMG");

        Assert.True(plan.Check.IsValid);
        Assert.True(plan.Check.IsUnchanged);
        Assert.False(plan.Check.DiffersOnlyByCase);
        Assert.NotNull(plan.FileNames);
        Assert.Equal("IMG.xmp", plan.FileNames[Dir + "IMG.xmp"]);
    }

    [Fact]
    public void Build_MapKeysCompareIgnoringCase()
    {
        var plan = CaptureGroupRenamePlan.Build(Group(), "new");

        Assert.NotNull(plan.FileNames);
        Assert.True(plan.FileNames.ContainsKey(Dir.ToUpperInvariant() + "IMG.JPG"));
        Assert.Equal("new.cr2", plan.FileNames[Dir + "img.CR2"]);
    }

    [Fact]
    public void Build_PathTooLongForRaw_ReturnsPathTooLong()
    {
        var folder = @"C:\" + new string('d', 197) + @"\"; // 200 chars
        var group = new CaptureGroup(folder + "a.jpg", folder + "a.cr2");
        var stem = new string('x', 56);

        var plan = CaptureGroupRenamePlan.Build(group, stem);

        Assert.Equal(RenameNameProblem.PathTooLong, plan.Check.Problem);
        Assert.Null(plan.FileNames);
    }

    [Fact]
    public void Build_LongPathAware_SkipsPathBudget()
    {
        var folder = @"C:\" + new string('d', 197) + @"\";
        var group = new CaptureGroup(folder + "a.jpg", folder + "a.cr2");
        var stem = new string('x', 56);

        var plan = CaptureGroupRenamePlan.Build(group, stem, longPathAware: true);

        Assert.True(plan.Check.IsValid);
        Assert.NotNull(plan.FileNames);
        Assert.Equal(2, plan.FileNames.Count);
    }

    [Fact]
    public void Build_NullGroup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CaptureGroupRenamePlan.Build(null!, "a"));
    }
}
