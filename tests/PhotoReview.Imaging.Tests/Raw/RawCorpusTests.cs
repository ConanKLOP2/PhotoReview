namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Unit tests of the shared RawCorpus strict/skip helper (independent of the real corpus).</summary>
public sealed class RawCorpusTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseStrict_RecognizesOneAndTrueOnly(string? value, bool expected) =>
        Assert.Equal(expected, RawCorpus.ParseStrict(value));

    [Fact]
    public void MissingDirectory_NonStrictSkips_StrictThrowsWithClearMessage()
    {
        using var root = new TempRoot("rawcorpus");
        var missing = root.Combine("nope");

        Assert.False(RawCorpus.RequireDirectory(missing, strict: false));
        var ex = Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.RequireDirectory(missing, strict: true));
        Assert.Contains(RawCorpus.StrictEnvironmentVariable, ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void ExistingDirectory_IsAvailableInBothModes()
    {
        using var root = new TempRoot("rawcorpus");
        Assert.True(RawCorpus.RequireDirectory(root.Path, strict: false));
        Assert.True(RawCorpus.RequireDirectory(root.Path, strict: true));
    }

    [Fact]
    public void TryGetFile_ReturnsPathWhenPresent_NullWhenMissingNonStrict_ThrowsWhenMissingStrict()
    {
        using var root = new TempRoot("rawcorpus");
        root.File("a.cr2", 1, 2, 3);

        Assert.Equal(root.Combine("a.cr2"), RawCorpus.TryGetFile(root.Path, "a.cr2", strict: true));
        Assert.Null(RawCorpus.TryGetFile(root.Path, "b.cr2", strict: false));
        var ex = Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.TryGetFile(root.Path, "b.cr2", strict: true));
        Assert.Contains("b.cr2", ex.Message);
        Assert.Null(RawCorpus.TryGetFile(root.Combine("missing-dir"), "a.cr2", strict: false));
        Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.TryGetFile(root.Combine("missing-dir"), "a.cr2", strict: true));
    }

    [Fact]
    public void TryGetFirst_MatchesByPatternAndNameFragment()
    {
        using var root = new TempRoot("rawcorpus");
        root.File("Fuji X100V.RAF", 1);
        root.File("Fuji X-E2S.RAF", 1);

        Assert.Equal(root.Combine("Fuji X100V.RAF"), RawCorpus.TryGetFirst(root.Path, "*.RAF", "X100V", strict: true));
        Assert.Null(RawCorpus.TryGetFirst(root.Path, "*.RAF", "GFX", strict: false));
        Assert.Null(RawCorpus.TryGetFirst(root.Combine("missing-dir"), "*.RAF", "X100V", strict: false));
        Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.TryGetFirst(root.Path, "*.RAF", "GFX", strict: true));
        Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.TryGetFirst(root.Combine("missing-dir"), "*.RAF", "X100V", strict: true));
    }

    [Fact]
    public void RequireNative_UnavailableSkipsOrThrowsByMode()
    {
        Assert.True(RawCorpus.RequireNative(true, null, strict: true));
        Assert.False(RawCorpus.RequireNative(false, "no dll", strict: false));
        var ex = Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.RequireNative(false, "no dll", strict: true));
        Assert.Contains("no dll", ex.Message);
    }

    [Fact]
    public void RequireNonEmpty_ZeroSkipsOrThrowsByMode()
    {
        Assert.True(RawCorpus.RequireNonEmpty(1, "x", strict: true));
        Assert.False(RawCorpus.RequireNonEmpty(0, "x", strict: false));
        Assert.Throws<RawCorpusRequiredException>(() => RawCorpus.RequireNonEmpty(0, "x", strict: true));
    }
}
