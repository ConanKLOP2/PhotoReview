using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Compare pair detection.</summary>
public sealed class ComparePairServiceTests : IDisposable
{
    private readonly TempRoot _root = new("compare");
    private readonly string _original;
    private readonly string _numbered;

    public ComparePairServiceTests()
    {
        _original = _root.Combine("CocCocSetup.jpg");
        _numbered = _root.Combine("CocCocSetup (1).jpg");
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Compare pair detection works from numbered filename")]
    public void ComparePairFromNumberedFilename()
    {
        var pair = ComparePairService.Find([_original, _numbered], _numbered);
        Assert.True(pair is not null && pair.Value.Left == _original && pair.Value.Right == _numbered);
    }

    [Fact(DisplayName = "Compare pair detection works from original filename")]
    public void ComparePairFromOriginalFilename()
    {
        var pair = ComparePairService.Find([_original, _numbered], _original);
        Assert.True(pair is not null && pair.Value.Left == _original && pair.Value.Right == _numbered);
    }

    [Fact(DisplayName = "Compare pair detection stays within selected folder")]
    public void ComparePairStaysWithinSelectedFolder()
    {
        var otherFolder = _root.Combine("other", "CocCocSetup.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(otherFolder)!);
        Assert.Null(ComparePairService.Find([_original, _numbered, otherFolder], otherFolder));
    }

    [Fact(DisplayName = "Compare pair detection rejects an incomplete pair")]
    public void ComparePairRejectsIncompletePair() =>
        Assert.Null(ComparePairService.Find([_original], _original));
}

