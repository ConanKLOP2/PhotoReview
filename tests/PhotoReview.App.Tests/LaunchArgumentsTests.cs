using System.IO;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>Folder arguments as Explorer's "Browse with PhotoReview" hands them over.</summary>
public sealed class LaunchArgumentsTests
{
    [Fact]
    public void An_existing_folder_loses_a_trailing_separator()
    {
        using var temp = new TempRoot("launch-args");

        Assert.Equal(temp.Path, LaunchArguments.NormalizeOne(temp.Path + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_folder_with_a_dot_segment_is_made_canonical()
    {
        using var temp = new TempRoot("launch-args");

        Assert.Equal(temp.Path, LaunchArguments.NormalizeOne(Path.Combine(temp.Path, ".")));
    }

    [Fact]
    public void A_quoted_folder_ending_in_a_backslash_is_repaired()
    {
        using var temp = new TempRoot("launch-args");

        // "C:\x\" reaches the process as C:\x" (the backslash escapes the closing quote).
        Assert.Equal(temp.Path, LaunchArguments.NormalizeOne(temp.Path + "\""));
    }

    [Fact]
    public void A_drive_root_keeps_its_separator_and_a_quoted_root_is_repaired()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Equal(root, LaunchArguments.NormalizeOne(root));
        Assert.Equal(root, LaunchArguments.NormalizeOne(root.TrimEnd((char)92) + "\""));
    }

    [Fact]
    public void A_file_and_a_missing_path_are_returned_unchanged()
    {
        using var temp = new TempRoot("launch-args");
        var file = temp.File("a.jpg", 1);
        var missing = Path.Combine(temp.Path, "missing") + Path.DirectorySeparatorChar;

        Assert.Equal(file, LaunchArguments.NormalizeOne(file));
        Assert.Equal(missing, LaunchArguments.NormalizeOne(missing));
        Assert.Equal("", LaunchArguments.NormalizeOne(""));
    }

    [Fact]
    public void Normalize_maps_every_argument_and_keeps_the_order()
    {
        using var temp = new TempRoot("launch-args");
        var file = temp.File("a.jpg", 1);

        Assert.Equal([file, temp.Path, "--i18n-keys"], LaunchArguments.Normalize([file, temp.Path + Path.DirectorySeparatorChar, "--i18n-keys"]));
    }
}
