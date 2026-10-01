using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>RV-T11 gap: nested numbering equivalence between Find and BuildIndex, and competing .xmp case variants.</summary>
public sealed class CompareAndCaptureGapTests
{
    [Fact]
    public void BuildIndex_NestedNumbering_EqualsFindForEveryPath()
    {
        var files = new List<string>
        {
            @"C:\Photos\a.JPG",
            @"C:\Photos\a (1).JPG",
            @"C:\Photos\a (1) (2).JPG",
            @"C:\Photos\a (2).JPG",
        };

        var index = ComparePairService.BuildIndex(files);

        foreach (var path in files)
        {
            var expected = ComparePairService.Find(files, path);
            var found = index.TryGetValue(path, out var pair);
            Assert.Equal(expected is not null, found);
            if (expected is null) continue;
            Assert.Equal(expected.Value.Left, pair.Left);
            Assert.Equal(expected.Value.Right, pair.Right);
        }
        Assert.Equal(@"C:\Photos\a (1).JPG", index[@"C:\Photos\a (1) (2).JPG"].Left); // "a (1)" is the original of "a (1) (2)"
    }

    [Fact]
    public void Build_TwoBaseNamedXmpCaseVariants_NoSidecarIsClaimedEvenWithASingleFileExtensionSidecar()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2"],
            [@"C:\photos\a.xmp", @"C:\photos\A.XMP", @"C:\photos\a.cr2.xmp"]);

        var group = Assert.Single(groups);
        Assert.Null(group.XmpPath);
        Assert.Equal(2, group.Paths.Count);
    }
}
