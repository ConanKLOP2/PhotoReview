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
        var expected = InstanceKeys.MutexNameFor(@"C:\Photos");

        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"C:\Photos\"));
        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"c:\photos"));
        Assert.Equal(expected, InstanceKeys.MutexNameFor(@"C:\Photos\sub\.."));
        Assert.NotEqual(expected, InstanceKeys.MutexNameFor(@"C:\Photos2"));
    }

    [Fact(DisplayName = "A relative path does not throw and equals its absolute form")]
    public void MutexNameFor_RelativePath_ResolvesAgainstCurrentDirectory()
    {
        Assert.Equal(
            InstanceKeys.MutexNameFor(Path.GetFullPath("relative-folder")),
            InstanceKeys.MutexNameFor("relative-folder"));
    }

    [Fact(DisplayName = "The no-folder launch uses one constant name")]
    public void MutexNameFor_NoFolder_IsConstant()
    {
        Assert.Equal(InstanceKeys.MutexNameFor(null), InstanceKeys.MutexNameFor(""));
        Assert.NotEqual(InstanceKeys.MutexNameFor(null), InstanceKeys.MutexNameFor(Path.GetFullPath("PhotoReview")));
    }

    [Fact(DisplayName = "A folder text that is not a usable path still yields a stable name instead of throwing")]
    public void MutexNameFor_UnusablePath_DoesNotThrow()
    {
        var bad = "C:\\Photos\0bad";

        var name = InstanceKeys.MutexNameFor(bad);

        Assert.Equal(name, InstanceKeys.MutexNameFor(bad));
        Assert.NotEqual(name, InstanceKeys.MutexNameFor(@"C:\Photos"));
        Assert.Equal(name, InstanceKeys.For(InstanceMode.PerFolder, bad).MutexName);
    }
}
