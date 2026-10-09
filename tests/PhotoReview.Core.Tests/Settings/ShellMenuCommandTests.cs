using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>The exact registry values of Explorer's "Browse with PhotoReview" and how a stored pair is classified.</summary>
[Trait("Category", "HotPath")]
public sealed class ShellMenuCommandTests
{
    private const string Exe = @"C:\Apps\Photo Review\PhotoReview.App.exe";

    [Fact]
    public void Commands_quote_the_exe_and_the_folder_placeholder()
    {
        Assert.Equal("\"C:\\Apps\\Photo Review\\PhotoReview.App.exe\" \"%1\"", ShellMenuCommand.ForFolder(Exe));
        Assert.Equal("\"C:\\Apps\\Photo Review\\PhotoReview.App.exe\" \"%V\"", ShellMenuCommand.ForBackground(Exe));
        Assert.Equal("\"C:\\Apps\\Photo Review\\PhotoReview.App.exe\",0", ShellMenuCommand.Icon(Exe));
        Assert.Equal("Browse with PhotoReview", ShellMenuCommand.MenuText);
    }

    [Fact]
    public void Evaluate_nothing_stored_is_not_registered()
    {
        Assert.Equal(ShellMenuState.NotRegistered, ShellMenuCommand.Evaluate(null, null, Exe));
    }

    [Fact]
    public void Evaluate_both_commands_for_this_exe_is_registered_ignoring_case()
    {
        var state = ShellMenuCommand.Evaluate(ShellMenuCommand.ForFolder(Exe).ToUpperInvariant(), ShellMenuCommand.ForBackground(Exe), Exe);

        Assert.Equal(ShellMenuState.Registered, state);
    }

    [Fact]
    public void Evaluate_a_command_for_another_exe_is_registered_elsewhere()
    {
        const string moved = @"D:\New\PhotoReview.App.exe";

        Assert.Equal(ShellMenuState.RegisteredElsewhere,
            ShellMenuCommand.Evaluate(ShellMenuCommand.ForFolder(moved), ShellMenuCommand.ForBackground(moved), Exe));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Evaluate_only_one_of_the_two_commands_is_registered_elsewhere(bool folder, bool background)
    {
        var state = ShellMenuCommand.Evaluate(folder ? ShellMenuCommand.ForFolder(Exe) : null, background ? ShellMenuCommand.ForBackground(Exe) : null, Exe);

        Assert.Equal(ShellMenuState.RegisteredElsewhere, state);
    }

    [Theory]
    [InlineData(@"C:\Apps\PhotoReview.App.exe", true)]
    [InlineData(@"\\server\share\PhotoReview.App.exe", true)]
    [InlineData("PhotoReview.App.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("C:\\Apps\\bad\"name.exe", false)]
    public void IsValidExecutablePath_requires_a_rooted_path_without_quotes(string? path, bool expected)
    {
        Assert.Equal(expected, ShellMenuCommand.IsValidExecutablePath(path));
    }
}
