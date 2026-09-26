using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Hostile relative destinations (traversal spellings, reserved device names, trailing dot/space, streams, wildcards,
/// over-long names): whatever the run-time policy answers, no mkdir/Move/Copy may target a path outside the photo folder.
/// </summary>
public sealed class DestinationEscapeTests
{
    private const string PhotoFolder = @"C:\photos";
    private const string Source = @"C:\photos\a.jpg";
    private static readonly AppPaths JournalPaths = new(@"C:\Users\test\AppData\Local");

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 26, 1, 0, 0, DateTimeKind.Utc);
    }

    private sealed class NoBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    public static TheoryData<string> Hostile() =>
    [
        "..", @"..\x", "../x", @".. \x", @"..\ ", @"sub\..\..", @"a\..\..\b", "...", ".. .", @"sub\.. ", @"sub\...",
        "CON", "NUL", "aux", "COM1", "LPT9", "con.txt", @"sub\CON", @"CON\sub", "sub.", "sub ", "sub. .", ". ", ".",
        "a:b", "a::$DATA", "sub:stream", "C:x", "C:", "x\0y", "*", "?", "a*b", "a?b", "<", "a\"b",
        "\u202egpj.evil", "%TEMP%", "~", "$Recycle.Bin", new string('a', 300), string.Join(@"\", Enumerable.Repeat("d", 200)),
        @"sub\" + new string('b', 259), "e\u0301", "\uFF0E\uFF0E", "\uFF0E\uFF0E" + @"\x",
    ];

    [Theory(DisplayName = "No hostile relative destination ever makes Move/Copy touch a path outside the photo folder")]
    [MemberData(nameof(Hostile))]
    public async Task HostileRelativeDestination_NeverLeavesPhotoFolder(string destination)
    {
        foreach (var type in new[] { FileOperationType.Move, FileOperationType.Copy })
        {
            var disk = new InMemoryFileSystem();
            disk.AddFile(Source, "photo");
            var fs = new CrashPointFileSystem(disk);
            var journal = new OperationJournal(JournalPaths, fs, new Clock());
            var service = new FileActionService(journal, fs, new Clock(), new NoBin());

            var result = await service.ExecuteAsync(new FileActionRequest(Source, type, destination));

            var journalDir = Path.GetDirectoryName(JournalPaths.JournalFile)!;
            foreach (var entry in fs.Log)
            {
                var kind = entry[..entry.IndexOf('|')];
                var target = entry[(entry.IndexOf('|') + 1)..];
                if (kind == "mkdir" && string.Equals(target, journalDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (kind is "append" or "write") continue;
                var touched = kind is "move" or "copy" ? target[(target.IndexOf('>') + 1)..] : target;
                var full = Path.GetFullPath(touched);
                Assert.True(
                    full.StartsWith(PhotoFolder + @"\", StringComparison.OrdinalIgnoreCase),
                    $"{type} '{destination}' -> {kind} touched '{full}' outside {PhotoFolder}");
            }

            Assert.True(disk.FileExists(Source) || result.Succeeded && type == FileOperationType.Move, "the source photo vanished without a successful Move");
        }
    }
}
