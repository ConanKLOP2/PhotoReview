using System.Globalization;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>shell-errors.log: lỗi menu (và tương tự) ghi được dù log chung tắt; nhỏ, cắt đuôi, không bao giờ ném.</summary>
[Trait("Category", "HotPath")]
public sealed class ShellErrorLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pr-shellerr-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        ShellErrorLog.Configure(null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static InvalidOperationException Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Format_HasUtcTimestampContextTypeHResultAndMessage()
    {
        var text = ShellErrorLog.Format(new DateTime(2026, 10, 13, 6, 7, 8, DateTimeKind.Utc), "ToolsMenu", Thrown("hộp thoại hỏng"));

        Assert.StartsWith("2026-10-13T06:07:08Z [ToolsMenu] System.InvalidOperationException 0x80131509: hộp thoại hỏng", text,
            StringComparison.Ordinal);
        Assert.Contains("    at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CutsLongMessagesAndLimitsStackFrames_AndNeverBreaksTheLine()
    {
        var text = ShellErrorLog.Format(DateTime.UtcNow, "c", Thrown(new string('x', 1000) + "\r\nsecond line"));
        var first = text.Split(Environment.NewLine)[0];

        Assert.True(first.Length < 400);
        Assert.DoesNotContain("second line", first, StringComparison.Ordinal);
        Assert.True(text.Split(Environment.NewLine).Count(l => l.StartsWith("    ", StringComparison.Ordinal)) <= ShellErrorLog.MaxStackFrames);
    }

    private static void Recurse(int depth)
    {
        if (depth == 0)
        {
            throw new InvalidOperationException("deep");
        }

        Recurse(depth - 1);
        GC.KeepAlive(depth);
    }

    [Fact]
    public void Format_KeepsExactlyTheFirstTwelveStackFrames_OfADeepException()
    {
        var deep = Assert.Throws<InvalidOperationException>(() => Recurse(40));

        var text = ShellErrorLog.Format(DateTime.UtcNow, "deep", deep);

        var frames = text.Split(Environment.NewLine).Count(l => l.StartsWith("    at ", StringComparison.Ordinal));
        Assert.Equal(ShellErrorLog.MaxStackFrames, frames);
    }
    [Fact]
    public void Format_IncludesTheInnerExceptionBriefly()
    {
        var outer = new InvalidOperationException("outer", new IOException("disk gone"));

        var text = ShellErrorLog.Format(DateTime.UtcNow, "c", outer);

        Assert.Contains("inner: System.IO.IOException: disk gone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WritesToTheConfiguredFile_AndCreatesItsDirectory()
    {
        var path = Path.Combine(_dir, "sub", ShellErrorLog.FileName);
        ShellErrorLog.Configure(path);

        ShellErrorLog.Report("ContextMenu", Thrown("one"));
        ShellErrorLog.Report("ToolsMenu", Thrown("two"));

        var lines = File.ReadAllText(path);
        Assert.Contains("[ContextMenu]", lines, StringComparison.Ordinal);
        Assert.Contains("[ToolsMenu]", lines, StringComparison.Ordinal);
        Assert.Equal(path, ShellErrorLog.CurrentPath);
    }

    [Fact]
    public void Report_WhenNotConfigured_DoesNothingAndDoesNotThrow()
    {
        ShellErrorLog.Configure(null);

        ShellErrorLog.Report("x", Thrown("y"));

        Assert.Null(ShellErrorLog.CurrentPath);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Append_TruncatesTheFileToItsNewestHalfOnALineBoundary_WhenOverTheLimit()
    {
        var path = Path.Combine(_dir, ShellErrorLog.FileName);
        Directory.CreateDirectory(_dir);
        var old = string.Concat(Enumerable.Range(0, 200).Select(i => "OLD-" + i.ToString("D3", CultureInfo.InvariantCulture) + new string('.', 42) + "\n"));
        File.WriteAllText(path, old);
        var limit = 2002L; // 200 dòng x 50 byte; nửa cuối = 1001 byte nên điểm cắt rơi GIỮA một dòng

        ShellErrorLog.Append(path, "after", Thrown("fresh"), limit);

        var text = File.ReadAllText(path);
        Assert.True(text.Length < limit, "tệp không được cắt");
        Assert.Contains("[after]", text, StringComparison.Ordinal);
        Assert.Contains("OLD-199", text, StringComparison.Ordinal); // giữ phần mới nhất
        Assert.DoesNotContain("OLD-000", text, StringComparison.Ordinal);
        Assert.StartsWith("OLD-", text, StringComparison.Ordinal); // bắt đầu ở đầu dòng
    }

    [Fact]
    public void Append_WithAnUnusablePath_NeverThrows()
    {
        var failure = Record.Exception(() =>
        {
            ShellErrorLog.Append("Z:\\no\\such\\drive\\<>|\\shell-errors.log", "ctx", Thrown("e"));
            ShellErrorLog.Append(new string('a', 5000), "ctx", Thrown("e"));
        });

        Assert.Null(failure);
    }
}