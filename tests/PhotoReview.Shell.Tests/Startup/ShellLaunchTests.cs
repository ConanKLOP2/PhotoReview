using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>WP-20: đối số dòng lệnh của shell, cùng luật với <c>App.StartupCoreAsync</c> (LaunchArguments + file đầu tiên tồn tại).</summary>
[Trait("Category", "HotPath")]
public sealed class ShellLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-launch-" + Guid.NewGuid().ToString("N")[..8]);

    public ShellLaunchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public void Parse_NoArguments_HasNothingToOpen()
    {
        var launch = ShellLaunch.Parse([]);

        Assert.Null(launch.InitialFile);
        Assert.Null(launch.InitialFolder);
        Assert.Null(launch.LaunchFolder);
        Assert.Null(launch.InitialPath);
        Assert.False(launch.Smoke);
        Assert.Empty(launch.ExistingPaths);
    }

    [Fact]
    public void Parse_ExistingFile_IsTheInitialFileAndItsFolderIsTheLaunchFolder()
    {
        var file = CreateFile("a.jpg");

        var launch = ShellLaunch.Parse([file]);

        Assert.Equal(file, launch.InitialFile);
        Assert.Equal(file, launch.InitialPath);
        Assert.Equal(_root, launch.LaunchFolder);
        Assert.Null(launch.InitialFolder);
        Assert.Equal([file], launch.ExistingPaths);
    }

    [Fact]
    public void Parse_FolderArgument_IsCanonicalAndBecomesTheLaunchFolder()
    {
        var launch = ShellLaunch.Parse([_root + Path.DirectorySeparatorChar]);

        Assert.Equal(_root, launch.InitialFolder);
        Assert.Equal(_root, launch.LaunchFolder);
        Assert.Null(launch.InitialFile);
        Assert.Equal(_root, launch.InitialPath);
    }

    [Fact]
    public void Parse_FileAndFolder_PrefersTheFile()
    {
        var file = CreateFile("a.jpg");
        var other = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;

        var launch = ShellLaunch.Parse([other, file]);

        Assert.Equal(file, launch.InitialPath);
        Assert.Equal(_root, launch.LaunchFolder); // thư mục của FILE, không phải thư mục đối số đầu
        Assert.Equal(other, launch.InitialFolder);
    }

    [Fact]
    public void Parse_QuotedDriveRoot_IsRepaired()
    {
        // Ổ gốc trong dấu nháy tới tiến trình là D:" (dấu gạch ngược escape dấu nháy đóng): LaunchArguments sửa lại.
        var drive = Path.GetPathRoot(_root)!;
        var mangled = drive.TrimEnd('\\') + "\"";

        var launch = ShellLaunch.Parse([mangled]);

        Assert.Equal(drive, launch.InitialFolder);
    }

    [Fact]
    public void Parse_SmokeFlag_IsExtractedAndNotTreatedAsAPath()
    {
        var file = CreateFile("a.jpg");

        var launch = ShellLaunch.Parse(["--smoke", file]);

        Assert.True(launch.Smoke);
        Assert.Equal(file, launch.InitialFile);
        Assert.DoesNotContain("--smoke", launch.Arguments);
    }

    [Fact]
    public void Parse_MissingPath_IsNotOpenedNorForwarded()
    {
        var missing = Path.Combine(_root, "nope.jpg");

        var launch = ShellLaunch.Parse([missing]);

        Assert.Null(launch.InitialPath);
        Assert.Empty(launch.ExistingPaths);
        Assert.Contains(missing, launch.Arguments);
    }

    [Fact]
    public void Parse_RelativeFile_YieldsTheDirectoryOfItsFullPathNotAnEmptyName()
    {
        var file = CreateFile("rel.jpg");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _root;

            var launch = ShellLaunch.Parse(["rel.jpg"]);

            Assert.Equal(_root, launch.LaunchFolder); // R2-F-08: Path.GetDirectoryName("rel.jpg") là chuỗi rỗng
            Assert.Equal([file], launch.ExistingPaths);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void InstancePrefix_IsNotThePrefixOfTheWpfApp()
    {
        // NE-6: hai bản chạy song song không được forward nhầm cho nhau.
        Assert.NotEqual(PhotoReview.Platform.Windows.InstanceKeys.DefaultPrefix, ShellInstanceNames.Prefix);
        Assert.Equal("PhotoReviewShell", ShellInstanceNames.Prefix);
    }
}
