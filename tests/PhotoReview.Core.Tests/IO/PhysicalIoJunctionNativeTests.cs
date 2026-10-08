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

    [Fact]
    public void ResolveRealPath_ChainOfJunctions_ResolvesToTheFinalTarget()
    {
        var target = _root.Dir("real");
        var inner = MakeJunction(_root.Combine("inner"), target);
        var outer = MakeJunction(_root.Combine("outer"), inner);
        try
        {
            // returnFinalTarget: outer -> inner -> real must come back as real, not as the intermediate junction.
            Assert.Equal(target, _fs.ResolveRealPath(outer), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(outer);
            Directory.Delete(inner);
        }
    }

    [Fact]
    public void ResolveRealPath_DanglingJunction_StillResolvesToItsMissingTarget()
    {
        var target = _root.Dir("gone");
        var link = MakeJunction(_root.Combine("link"), target);
        try
        {
            Directory.Delete(target); // the junction now points at nothing, but it is still a reparse point: containment checks must see where it leads
            var path = Path.Combine(link, "photo.jpg");

            Assert.Equal(Path.Combine(target, "photo.jpg"), _fs.ResolveRealPath(path), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ResolveRealPath_RegularFolderAndFileBesideAJunction_AreNotRewritten()
    {
        var folder = _root.Dir("plain");
        var file = _root.File(Path.Combine("plain", "a.jpg"), 1);
        var link = MakeJunction(_root.Combine("link"), _root.Dir("real"));
        try
        {
            Assert.Equal(folder, _fs.ResolveRealPath(folder), ignoreCase: true);
            Assert.Equal(file, _fs.ResolveRealPath(file), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
