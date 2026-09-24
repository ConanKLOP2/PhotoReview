using System.IO;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>R2-F-08: the instance lock must key on the canonical folder, not on how the caller spelled it.</summary>
[Trait("Category", "Integration")]
public sealed class InstanceLockKeyTests
{
    [Fact(DisplayName = "Different spellings of one folder map to the same mutex name")]
    public void MutexNameFor_SpellingVariants_AreEqual()
    {
        var expected = InstanceLock.MutexNameFor(@"C:\Photos");

        Assert.Equal(expected, InstanceLock.MutexNameFor(@"C:\Photos\"));
        Assert.Equal(expected, InstanceLock.MutexNameFor(@"c:\photos"));
        Assert.Equal(expected, InstanceLock.MutexNameFor(@"C:\Photos\sub\.."));
        Assert.NotEqual(expected, InstanceLock.MutexNameFor(@"C:\Photos2"));
    }

    [Fact(DisplayName = "A relative path does not throw and equals its absolute form")]
    public void MutexNameFor_RelativePath_ResolvesAgainstCurrentDirectory()
    {
        Assert.Equal(
            InstanceLock.MutexNameFor(Path.GetFullPath("relative-folder")),
            InstanceLock.MutexNameFor("relative-folder"));
    }

    [Fact(DisplayName = "The no-folder launch uses one constant name")]
    public void MutexNameFor_NoFolder_IsConstant()
    {
        Assert.Equal(InstanceLock.MutexNameFor(null), InstanceLock.MutexNameFor(""));
        Assert.NotEqual(InstanceLock.MutexNameFor(null), InstanceLock.MutexNameFor(Path.GetFullPath("PhotoReview")));
    }

    [Fact(DisplayName = "A second instance on the same folder spelled differently is refused")]
    public void SecondInstance_SameFolderDifferentSpelling_IsNotOwner()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PhotoReviewLock_" + Guid.NewGuid().ToString("N"));
        using var first = new InstanceLock(folder);
        using var second = new InstanceLock(folder.ToLowerInvariant() + Path.DirectorySeparatorChar);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
    }
}
