using System.IO;
using PhotoReview.App;
using PhotoReview.Core.Model;

namespace PhotoReview.Integration.Tests;

/// <summary>Action-profiles editor: duplicate shortcuts by parsed key, next free shortcut, and import error handling.</summary>
[Collection("GlobalState")]
public sealed class ActionProfilesValidationTests
{
    private static ReviewAction Act(string shortcut, string name = "a") =>
        new() { Name = name, Shortcut = shortcut, Operation = FileOperationType.Move, Destination = "Out" };

    [Theory]
    [InlineData("Return", "Enter")]
    [InlineData("Prior", "PageUp")]
    [InlineData("Next", "PageDown")]
    [InlineData("f6", "F6")]
    public void HasInvalidActions_AliasedShortcuts_AreDuplicates(string first, string second)
    {
        Assert.True(ActionProfilesWindow.HasInvalidActions([Act(first, "a"), Act(second, "b")]));
    }

    [Fact]
    public void HasInvalidActions_DistinctValidActions_AreAccepted()
    {
        Assert.False(ActionProfilesWindow.HasInvalidActions([Act("F6", "a"), Act("F7", "b")]));
    }

    [Fact]
    public void HasInvalidActions_EmptyBlankNameBadShortcut_AreRejected()
    {
        Assert.True(ActionProfilesWindow.HasInvalidActions([]));
        Assert.True(ActionProfilesWindow.HasInvalidActions([Act("F6", " ")]));
        Assert.True(ActionProfilesWindow.HasInvalidActions([Act("Escape")]));
    }

    [Fact]
    public void NextFreeShortcut_SkipsUsedKeysIncludingAliasesOfThem()
    {
        Assert.Equal("F6", ActionProfilesWindow.NextFreeShortcut([]));
        Assert.Equal("F7", ActionProfilesWindow.NextFreeShortcut([Act("F6")]));
        Assert.Equal("F8", ActionProfilesWindow.NextFreeShortcut([Act("f6"), Act("F7")]));
    }

    [Fact]
    public void TryImport_MalformedFile_GivesInvalidMessageWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "pr-import-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "[null]");
            Assert.Null(ActionProfilesWindow.TryImport(path, out var error));
            Assert.False(string.IsNullOrEmpty(error));
            File.WriteAllText(path, "not json");
            Assert.Null(ActionProfilesWindow.TryImport(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryImport_MissingFile_ReportsTheIoReason()
    {
        var path = Path.Combine(Path.GetTempPath(), "pr-import-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Null(ActionProfilesWindow.TryImport(path, out var error));
        Assert.Contains(Path.GetFileName(path), error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryImport_ValidFile_ReturnsClones()
    {
        var path = Path.Combine(Path.GetTempPath(), "pr-import-ok-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { Act("F6", "x") }));
            var result = ActionProfilesWindow.TryImport(path, out _);
            Assert.NotNull(result);
            Assert.Equal("x", Assert.Single(result).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
