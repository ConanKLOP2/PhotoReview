using System.Diagnostics;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

/// <summary>
/// SEC-01 junction cases of <see cref="PhysicalFileSystem.ResolveRealPath"/>. Spawns <c>cmd /c mklink /J</c> inside a private temp
/// directory (the junction is removed in a finally block, never the target), so it is Native, not part of the default filter.
/// </summary>
[Trait("Category", "Native")]
public sealed class PhysicalIoJunctionNativeTests : IDisposable
{
    private readonly TempRoot _root = new("physical-io-junction");
    private readonly PhysicalFileSystem _fs = new();

    public void Dispose() => _root.Dispose();

    private static string MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "mklink did not finish");
        Assert.True(process.ExitCode == 0, "could not create a junction: " + output);
        return link;
    }

    [Fact]
    public void ResolveRealPath_JunctionInsideThePath_ResolvesToTheRealTarget()
    {
        var target = _root.Dir("real");
        var link = MakeJunction(_root.Combine("link"), target);
        try
        {
            var resolved = _fs.ResolveRealPath(Path.Combine(link, "child", "photo.jpg"));

            Assert.Equal(Path.Combine(target, "child", "photo.jpg"), resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link); // removes the junction only, never the target
        }
    }

    [Fact]
    public void ResolveRealPath_JunctionAsTheFinalSegment_ResolvesToTheRealFolder()
    {
        var target = _root.Dir("real");
        var link = MakeJunction(_root.Combine("link"), target);
        try
        {
            Assert.Equal(target, _fs.ResolveRealPath(link), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
