using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class WindowsNaturalComparerTests
{
    private static readonly WindowsNaturalComparer Comparer = WindowsNaturalComparer.Instance;

    [Fact(DisplayName = "The same reference and a double null compare equal")]
    public void SameReference_IsZero()
    {
        var s = new string('x', 3);
        Assert.Equal(0, Comparer.Compare(s, s));
        Assert.Equal(0, Comparer.Compare(null, null));
    }

    [Fact(DisplayName = "Null sorts before any string and any string after null")]
    public void Null_SortsFirst()
    {
        Assert.True(Comparer.Compare(null, "") < 0);
        Assert.True(Comparer.Compare("", null) > 0);
    }

    [Fact(DisplayName = "Digit runs compare by value: a2 < a10 and a10 > a2")]
    public void NaturalOrder()
    {
        Assert.True(Comparer.Compare("a2", "a10") < 0);
        Assert.True(Comparer.Compare("a10", "a2") > 0);
    }

    [Fact(DisplayName = "When StrCmpLogicalW reports equality for different strings the ordinal-ignore-case tie-break orders them (sharp s vs ss)")]
    public void LogicalTie_IsBrokenByOrdinalIgnoreCase()
    {
        const string sharpS = "ß";
        // StrCmpLogicalW treats these as equal on Windows; OrdinalIgnoreCase does not, so the result must be non-zero and antisymmetric.
        Assert.True(Comparer.Compare(sharpS, "ss") > 0);
        Assert.True(Comparer.Compare("ss", sharpS) < 0);
    }

    [Fact(DisplayName = "Strings differing only by case are equal")]
    public void CaseOnly_IsZero() => Assert.Equal(0, Comparer.Compare("IMG_1.JPG", "img_1.jpg"));
}
