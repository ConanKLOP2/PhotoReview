using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>Failure injection for log rotation: a rotation that cannot happen must not silence logging.</summary>
[Trait("Category", "HotPath")]
public sealed class FileLogRotationFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-FileLogRotation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // best effort temp cleanup
        }
    }

    [Fact(DisplayName = "A read-only rotation target does not stop new entries from being written")]
    public void ReadOnlyBackup_LoggingContinues()
    {
        Directory.CreateDirectory(_dir);
        var log = Path.Combine(_dir, "app.log");
        var backup = Path.Combine(_dir, "app.1.log");
        File.WriteAllBytes(log, new byte[4096]);
        File.WriteAllText(backup, "old backup");
        File.SetAttributes(backup, FileAttributes.ReadOnly);

        using var file = new FileLog(log, maxLogBytes: 1024) { Enabled = true };
        file.Info("still-logging-after-failed-rotation");
        file.Flush();

        Assert.Contains("still-logging-after-failed-rotation", File.ReadAllText(log));
    }

    [Fact(DisplayName = "A backup file held open by another process does not stop new entries from being written")]
    public void LockedBackup_LoggingContinues()
    {
        Directory.CreateDirectory(_dir);
        var log = Path.Combine(_dir, "app.log");
        var backup = Path.Combine(_dir, "app.1.log");
        File.WriteAllBytes(log, new byte[4096]);
        File.WriteAllText(backup, "old backup");
        using var holder = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None);

        using var file = new FileLog(log, maxLogBytes: 1024) { Enabled = true };
        file.Info("still-logging-with-locked-backup");
        file.Flush();

        Assert.Contains("still-logging-with-locked-backup", File.ReadAllText(log));
    }

    [Fact(DisplayName = "Rotation keeps exactly one backup and no entry is lost across a rotation")]
    public void Rotation_NoEntryLost()
    {
        Directory.CreateDirectory(_dir);
        var log = Path.Combine(_dir, "app.log");
        File.WriteAllBytes(log, new byte[2048]);

        using var file = new FileLog(log, maxLogBytes: 1024) { Enabled = true };
        for (var i = 0; i < 20; i++) file.Info("entry-" + i);
        file.Flush();

        var all = File.ReadAllText(log) + File.ReadAllText(Path.Combine(_dir, "app.1.log"));
        for (var i = 0; i < 20; i++) Assert.Contains("entry-" + i + "\r\n", all.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }
}
