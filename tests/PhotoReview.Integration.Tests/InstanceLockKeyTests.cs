using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>R2-F-08: the instance lock must key on the canonical folder, not on how the caller spelled it.</summary>
[Trait("Category", "Integration")]
public sealed class InstanceLockKeyTests
{
    [Fact(DisplayName = "Different spellings of one folder map to the same mutex name")]
    public void MutexNameFor_SpellingVariants_AreEqual()
    {
        var expected = InstanceKeys.MutexNameFor(@"C:\Photos", InstanceKeys.DefaultPrefix);

        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"C:\Photos\", InstanceKeys.DefaultPrefix));
        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"c:\photos", InstanceKeys.DefaultPrefix));
        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"C:\Photos\sub\..", InstanceKeys.DefaultPrefix));
        Assert.NotEqual(expected, InstanceKeys.MutexNameFor(@"C:\Photos2", InstanceKeys.DefaultPrefix));
    }

    [Fact(DisplayName = "A relative path does not throw and equals its absolute form")]
    public void MutexNameFor_RelativePath_ResolvesAgainstCurrentDirectory()
    {
        Assert.Equal(
            InstanceKeys.MutexNameFor(Path.GetFullPath("relative-folder"), InstanceKeys.DefaultPrefix),
            InstanceKeys.MutexNameFor("relative-folder", InstanceKeys.DefaultPrefix));
    }

    [Fact(DisplayName = "The no-folder launch uses one constant name")]
    public void MutexNameFor_NoFolder_IsConstant()
    {
        Assert.Equal(InstanceKeys.MutexNameFor(null, InstanceKeys.DefaultPrefix), InstanceKeys.MutexNameFor("", InstanceKeys.DefaultPrefix));
        Assert.NotEqual(InstanceKeys.MutexNameFor(null, InstanceKeys.DefaultPrefix), InstanceKeys.MutexNameFor(Path.GetFullPath("PhotoReview"), InstanceKeys.DefaultPrefix));
    }

    [Fact(DisplayName = "A folder text that is not a usable path still yields a stable name instead of throwing")]
    public void MutexNameFor_UnusablePath_DoesNotThrow()
    {
        var bad = "C:\\Photos\0bad";

        var name = InstanceKeys.MutexNameFor(bad, InstanceKeys.DefaultPrefix);

        Assert.Equal(name, InstanceKeys.MutexNameFor(bad, InstanceKeys.DefaultPrefix));
        Assert.NotEqual(name, InstanceKeys.MutexNameFor(@"C:\Photos", InstanceKeys.DefaultPrefix));
        Assert.Equal(name, InstanceKeys.For(InstanceMode.PerFolder, bad).MutexName);
    }
}
