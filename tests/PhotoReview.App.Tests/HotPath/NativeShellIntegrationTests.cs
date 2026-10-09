using Microsoft.Win32;
using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// The real registry behind Explorer's "Browse with PhotoReview", but on a PRIVATE key under HKCU\Software\PhotoReviewTests\&lt;guid&gt;
/// (never the user's real <c>Software\Classes</c>): the whole private tree is deleted in <see cref="Dispose"/>.
/// </summary>
[Trait("Category", "Native")]
public sealed class NativeShellIntegrationTests : IDisposable
{
    private const string Exe = @"C:\Apps\Photo Review\PhotoReview.App.exe";
    private readonly string _testRoot = @"Software\PhotoReviewTests\" + Guid.NewGuid().ToString("N");
    private readonly string _classes;
    private readonly WindowsShellIntegration _shell;

    public NativeShellIntegrationTests()
    {
        _classes = _testRoot + @"\Classes";
        _shell = new WindowsShellIntegration(_classes);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_testRoot, throwOnMissingSubKey: false);

    private string? Read(string verbPath, string? valueName = null, bool command = false)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_classes + "\\" + verbPath + (command ? "\\command" : ""));
        return key?.GetValue(valueName) as string;
    }

    [Fact]
    public void Nothing_is_registered_on_a_fresh_key()
    {
        Assert.Equal(ShellMenuState.NotRegistered, _shell.GetState(Exe));
    }

    [Fact]
    public void Register_writes_the_folder_and_background_verbs_with_text_icon_and_command()
    {
        _shell.Register(Exe);

        Assert.Equal(ShellMenuState.Registered, _shell.GetState(Exe));
        Assert.Equal("Browse with PhotoReview", Read(@"Directory\shell\PhotoReview"));
        Assert.Equal("Browse with PhotoReview", Read(@"Directory\Background\shell\PhotoReview"));
        Assert.Equal("\"" + Exe + "\",0", Read(@"Directory\shell\PhotoReview", "Icon"));
        Assert.Equal("\"" + Exe + "\" \"%1\"", Read(@"Directory\shell\PhotoReview", command: true));
        Assert.Equal("\"" + Exe + "\" \"%V\"", Read(@"Directory\Background\shell\PhotoReview", command: true));
    }

    [Fact]
    public void Register_again_for_a_moved_exe_repairs_the_path()
    {
        const string moved = @"D:\Moved\PhotoReview.App.exe";
        _shell.Register(Exe);
        Assert.Equal(ShellMenuState.RegisteredElsewhere, _shell.GetState(moved));

        _shell.Register(moved);

        Assert.Equal(ShellMenuState.Registered, _shell.GetState(moved));
        Assert.Equal(ShellMenuState.RegisteredElsewhere, _shell.GetState(Exe));
    }

    [Fact]
    public void Unregister_removes_only_the_two_PhotoReview_verbs()
    {
        using (var other = Registry.CurrentUser.CreateSubKey(_classes + @"\Directory\shell\OtherTool\command"))
            other.SetValue(null, "other.exe \"%1\"");
        _shell.Register(Exe);

        _shell.Unregister();

        Assert.Equal(ShellMenuState.NotRegistered, _shell.GetState(Exe));
        Assert.Null(Read(@"Directory\shell\PhotoReview"));
        Assert.Null(Read(@"Directory\Background\shell\PhotoReview"));
        Assert.Equal("other.exe \"%1\"", Read(@"Directory\shell\OtherTool", command: true)); // a neighbour verb survives
    }

    [Fact]
    public void Unregister_when_nothing_is_registered_is_a_no_op()
    {
        _shell.Unregister();

        Assert.Equal(ShellMenuState.NotRegistered, _shell.GetState(Exe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData("C:\\bad\"quote.exe")]
    public void Register_rejects_an_unusable_path_and_writes_nothing(string exe)
    {
        Assert.ThrowsAny<ArgumentException>(() => _shell.Register(exe));

        Assert.Equal(ShellMenuState.NotRegistered, _shell.GetState(Exe));
    }
}
