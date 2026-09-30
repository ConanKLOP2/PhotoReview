using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Pure managed LibRaw interop logic: needs neither libraw.dll nor the corpus, so it runs in the default CI category.</summary>
public sealed class LibRawManagedLogicTests
{
    [Theory]
    [InlineData("0.22.2", true)]
    [InlineData("0.22.2-Release", true)]
    [InlineData("0.22.1", false)]
    [InlineData("0.22.3", false)]
    [InlineData("0.22.9-Release", false)]
    [InlineData("0.22.0", false)]
    [InlineData("0.22", false)]
    [InlineData("0.23.2", false)]
    [InlineData("1.22.2", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CheckExactVersion_OnlyTheTestedPatchReleasePasses(string? version, bool accepted)
    {
        Assert.Equal(accepted, LibRawAvailability.CheckExactVersion(version));
    }
}
