using System.Text.Json;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// FEAT-RENAME-DESIGN, PR1 (Core only): journaled same-folder rename of one file (<see cref="FileActionService.RenameAsync"/>) and of a
/// JPEG+RAW+XMP capture (<see cref="FileActionService.RenameGroupAsync"/>), including the case-only path (the stored spelling decides,
/// never a case-insensitive FileExists), Undo, startup reconcile and Recovery. In-memory disk behind the fault rig; never a real disk.
/// </summary>
public sealed class RenameFileActionTests
{
    private const string A = @"C:\photos\a.jpg";

    private static FaultRig Rig(params string[] names) =>
        new FaultRig().Seed(FaultRig.Photos, names.Length == 0 ? ["a.jpg", "b.jpg"] : names);

    private static string Stored(FaultRig rig, string path) => Assert.Single(rig.Disk.GetStoredFileNames(path));

    private static JournalEntry LatestOnly(FaultRig rig) => Assert.Single(rig.Latest().Values);

    // ---------------------------------------------------------------- single file

    [Fact]
    public async Task Rename_Success_MovesInPlaceAndJournalsOneCommittedMove()
    {
        var rig = Rig();
        var before = rig.Disk.GetFileStat(A)!;

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.True(result.Succeeded);
        Assert.False(result.NoChange);
        Assert.Equal(FileOperationType.Move, result.Operation);
        Assert.Equal(@"C:\photos\x.jpg", result.DestinationPath);
        Assert.False(rig.Disk.FileExists(A));
        Assert.True(rig.HasFull(@"C:\photos\x.jpg"));
        var after = rig.Disk.GetFileStat(@"C:\photos\x.jpg")!;
        Assert.Equal(before, after); // a rename never changes size or write time
        var entry = LatestOnly(rig);
        Assert.Equal(FileOperationType.Move, entry.Type);
        Assert.Equal(JournalState.Committed, entry.State);
        Assert.Equal(A, entry.Source);
        Assert.Equal(@"C:\photos\x.jpg", entry.Destination);
        Assert.Null(entry.GroupMembers);
        Assert.Null(entry.Undo);
    }

    [Fact]
    public async Task Rename_JournalLine_UsesExactlyTheSchemaOfAnOrdinaryMove()
    {
        // ADR 0003: no new journal type or field, so an older build reads a rename as the Move it is.
        var renameRig = Rig();
        _ = await renameRig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));
        var moveRig = Rig();
        _ = await moveRig.Service.ExecuteAsync(new FileActionRequest(A, FileOperationType.Move, "sel"));

        static string[] Keys(FaultRig rig) => rig.JournalText().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray())
            .Select(names => string.Join(",", names)).Distinct().Order().ToArray();

        Assert.Equal(Keys(moveRig), Keys(renameRig));
        foreach (var line in renameRig.JournalLines()) Assert.Equal(FileOperationType.Move, line.Type);
    }

    [Fact]
    public async Task Rename_TargetTakenByAnotherFile_IsRefusedAndTouchesNothing()
    {
        var rig = Rig();
        rig.Fs.Arm(null);

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "b.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.True(rig.HasFull(@"C:\photos\b.jpg"));
        Assert.Empty(rig.JournalLines());
        Assert.Empty(rig.Fs.Kinds);
    }

    [Fact]
    public async Task Rename_TargetTakenByAnotherFileWithOtherCase_IsRefused()
    {
        var rig = Rig();

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "B.JPG"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.Equal("b.jpg", Stored(rig, @"C:\photos\b.jpg"));
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public async Task Rename_TargetIsAFolder_IsRefused()
    {
        var rig = Rig();
        rig.Disk.CreateDirectory(@"C:\photos\x.jpg");

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public async Task Rename_SourceMissing_FailsWithoutJournal()
    {
        var rig = Rig();

        var result = await rig.Service.RenameAsync(new RenameRequest(@"C:\photos\gone.jpg", "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.Empty(rig.JournalLines());
        Assert.False(rig.Disk.FileExists(@"C:\photos\x.jpg"));
    }

    [Fact]
    public async Task Rename_ToTheSameName_IsANoChangeSuccessWithNoJournalLine()
    {
        var rig = Rig();
        rig.Fs.Arm(null);

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "a.jpg"));

        Assert.True(result.Succeeded);
        Assert.True(result.NoChange);
        Assert.Empty(rig.JournalLines());
        Assert.Empty(rig.Fs.Kinds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("sub\\x.jpg")]
    [InlineData("..\\x.jpg")]
    [InlineData("a/b.jpg")]
    [InlineData("a:b.jpg")]
    [InlineData("a?.jpg")]
    [InlineData("x.")]
    [InlineData("CON.jpg")]
    [InlineData("nul")]
    [InlineData("LPT1.jpg")]
    public async Task Rename_InvalidTarget_IsRejectedBeforeAnythingIsJournaled(string newFileName)
    {
        var rig = Rig();
        rig.Fs.Arm(null);

        var result = await rig.Service.RenameAsync(new RenameRequest(A, newFileName));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.True(rig.HasFull(A));
        Assert.Empty(rig.JournalLines());
        Assert.Empty(rig.Fs.Kinds);
    }

    [Fact]
    public async Task Rename_WhileAnotherActionIsRunning_IsRejectedAsBusy()
    {
        var rig = Rig();
        Assert.True(rig.Service.TryBegin());
        try
        {
            var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

            Assert.False(result.Succeeded);
            Assert.True(result.Rejected);
            Assert.True(rig.HasFull(A));
        }
        finally
        {
            rig.Service.End();
        }
    }

    [Fact]
    public async Task Rename_OsRefusesTheMove_NothingChangedSoTheLineIsDismissedNotARecoveryItem()
    {
        var rig = Rig();
        rig.Fs.Arm(new FaultPlan(2, FaultKind.FailOnce)); // call 1 = journal Prepared append, call 2 = the rename

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.False(rig.Disk.FileExists(@"C:\photos\x.jpg"));
        Assert.Equal(JournalState.Dismissed, LatestOnly(rig).State);
        Assert.Empty(rig.Journal.ReadFailedOperations());
    }

    [Fact]
    public async Task Rename_FileLockedByAnotherProgram_FailsCleanlyAndIsDismissed()
    {
        var rig = Rig();
        rig.Disk.MoveHook = (_, _) => new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.Equal(JournalState.Dismissed, LatestOnly(rig).State);
    }

    [Fact]
    public async Task Rename_ForeignFileAppearsAtTheTargetAfterThePreflight_IsNeverOverwritten()
    {
        var rig = Rig();
        rig.Disk.MoveHook = (_, destination) =>
        {
            rig.Disk.AddFile(destination, "SOMEONE ELSE'S PHOTO, longer than the original so the sizes differ", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return null; // the real Move then fails because the destination exists
        };

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.StartsWith("SOMEONE ELSE'S PHOTO", rig.Disk.ReadAllText(@"C:\photos\x.jpg"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_FailureAfterTheFileWasRenamed_StaysAFailedRecoveryItemNeverDismissed()
    {
        var rig = Rig();
        // The rename itself works, then the post-move check cannot be satisfied (the shell says it moved but threw): not "untouched".
        var service = new FileActionService(rig.Journal, rig.Fs, rig.Clock, rig.Bin, moveOverride: (source, destination) =>
        {
            rig.Disk.Move(source, destination);
            throw new IOException("simulated failure after the rename took effect");
        });

        var result = await service.RenameAsync(new RenameRequest(A, "x.jpg"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(@"C:\photos\x.jpg"));
        Assert.NotEqual(JournalState.Dismissed, LatestOnly(rig).State);
    }

    [Fact]
    public async Task Rename_ThenUndo_PutsTheOriginalNameBack()
    {
        var rig = Rig();
        var renamed = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));
        rig.Undo.Register(renamed);

        var undone = await rig.Undo.UndoLastAsync();

        Assert.True(undone.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.False(rig.Disk.FileExists(@"C:\photos\x.jpg"));
        var lines = rig.JournalLines();
        Assert.Contains(lines, line => line.Undo == true && line.State == JournalState.Committed
            && line.Source == @"C:\photos\x.jpg" && line.Destination == A);
    }

    [Fact]
    public async Task Rename_ThenUndo_AfterTheOriginalNameWasTakenByAnotherFile_IsRefused()
    {
        var rig = Rig();
        var renamed = await rig.Service.RenameAsync(new RenameRequest(A, "x.jpg"));
        rig.Undo.Register(renamed);
        rig.Disk.AddFile(A, "a different photo now living at the old name");

        var undone = await rig.Undo.UndoLastAsync();

        Assert.False(undone.Succeeded);
        Assert.True(rig.HasFull(@"C:\photos\x.jpg"));
        Assert.Equal("a different photo now living at the old name", rig.Disk.ReadAllText(A));
    }

    // ---------------------------------------------------------------- case only

    [Fact]
    public async Task CaseOnlyRename_Succeeds_StoresTheNewSpellingAndIsCommitted()
    {
        var rig = Rig();

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "A.jpg"));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.NoChange);
        Assert.Equal("A.jpg", Stored(rig, A));
        Assert.True(rig.HasFull(@"C:\photos\A.jpg"));
        Assert.Equal(JournalState.Committed, LatestOnly(rig).State);
        Assert.Empty(rig.NewJournal().ReconcilePendingOperations());
        Assert.Equal(RecoveryVerdict.AlreadyDone, new RecoveryFileCheck(rig.Fs).Check(LatestOnly(rig)).Verdict);
    }

    [Fact]
    public async Task CaseOnlyRename_ToTheSpellingTheFileAlreadyHas_IsNoChange()
    {
        var rig = Rig("Photo.JPG");

        var result = await rig.Service.RenameAsync(new RenameRequest(@"C:\photos\photo.jpg", "Photo.JPG"));

        Assert.True(result.Succeeded);
        Assert.True(result.NoChange);
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public async Task CaseOnlyRename_Undo_RestoresTheOldSpelling()
    {
        var rig = Rig();
        var renamed = await rig.Service.RenameAsync(new RenameRequest(A, "A.jpg"));
        rig.Undo.Register(renamed);

        var undone = await rig.Undo.UndoLastAsync();

        Assert.True(undone.Succeeded, undone.Error);
        Assert.Equal("a.jpg", Stored(rig, A));
        Assert.True(rig.HasFull(A));
    }

    [Fact]
    public async Task CaseOnlyRename_UndoOfAnAlreadyUndoneRename_DoesNotRunTwiceAndDropsTheEntry()
    {
        var rig = Rig();
        var renamed = await rig.Service.RenameAsync(new RenameRequest(A, "A.jpg"));
        rig.Undo.Register(renamed);
        // The user put the old spelling back by hand: the stored name is already "a.jpg".
        rig.Disk.Move(@"C:\photos\A.jpg", @"C:\photos\a.jpg");

        var undone = await rig.Undo.UndoLastAsync();

        Assert.False(undone.Succeeded); // nothing to undo: the destination spelling is gone, reported once
        Assert.Equal("a.jpg", Stored(rig, A));
        Assert.True(rig.HasFull(A));
    }

    [Fact]
    public async Task CaseOnlyRename_TwoDifferentFilesInACaseSensitiveFolder_IsRefused()
    {
        // Two entries that differ only by case (a case-sensitive folder): the target is another file, never "the same one".
        var rig = Rig();
        rig.Disk.AddFile(@"C:\photos\Other\a.jpg", "one");
        rig.Disk.AddFile(@"C:\photos\Other\A.jpg", "two");
        var fs = new TwoEntryListing(rig.Disk);
        var service = new FileActionService(rig.Journal, fs, rig.Clock, rig.Bin);

        var result = await service.RenameAsync(new RenameRequest(@"C:\photos\Other\a.jpg", "A.jpg"));

        Assert.False(result.Succeeded);
        Assert.Empty(rig.JournalLines());
    }

    /// <summary>Lists both spellings as separate entries (what a case-sensitive NTFS folder reports), otherwise the in-memory disk.</summary>
    private sealed class TwoEntryListing(InMemoryFileSystem inner) : FileSystemForwarder(inner)
    {
        public override IReadOnlyList<string> GetStoredFileNames(string path) => ["a.jpg", "A.jpg"];
    }

    [Fact]
    public async Task CaseOnlyRename_MoveFails_NothingChangedAndTheLineIsDismissedNotLost()
    {
        var rig = Rig();
        rig.Fs.Arm(new FaultPlan(2, FaultKind.FailOnce));

        var result = await rig.Service.RenameAsync(new RenameRequest(A, "A.jpg"));

        Assert.False(result.Succeeded);
        Assert.Equal("a.jpg", Stored(rig, A));
        Assert.Equal(JournalState.Dismissed, LatestOnly(rig).State);
    }

    [Theory]
    [InlineData("a.jpg", RecoveryVerdict.CanRetry)]   // still stored under the source spelling: not done yet
    [InlineData("A.jpg", RecoveryVerdict.AlreadyDone)] // stored under the destination spelling: done
    public void CaseOnlyRename_RecoveryVerdict_FollowsTheStoredSpelling(string storedName, RecoveryVerdict expected)
    {
        var rig = Rig();
        rig.Disk.Delete(A);
        rig.Disk.AddFile(Path.Combine(FaultRig.Photos, storedName), rig.ContentOf("a.jpg"), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        var stat = rig.Disk.GetFileStat(A)!;
        var entry = new JournalEntry("e1", FileOperationType.Move, JournalState.Prepared, A, @"C:\photos\A.jpg", stat.Length, stat.LastWriteUtc, rig.Clock.UtcNow);

        var verdict = new RecoveryFileCheck(rig.Fs).Check(entry).Verdict;

        Assert.Equal(expected, verdict);
    }

    [Theory]
    [InlineData("a.jpg", JournalState.Failed)]   // not renamed yet: the operation did not happen
    [InlineData("A.jpg", JournalState.Committed)] // renamed: it happened
    public void CaseOnlyRename_StartupReconcile_FollowsTheStoredSpelling(string storedName, JournalState expected)
    {
        var rig = Rig();
        rig.Disk.Delete(A);
        rig.Disk.AddFile(Path.Combine(FaultRig.Photos, storedName), rig.ContentOf("a.jpg"), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        var stat = rig.Disk.GetFileStat(A)!;
        rig.Journal.Append(new JournalEntry("e1", FileOperationType.Move, JournalState.Prepared, A, @"C:\photos\A.jpg", stat.Length, stat.LastWriteUtc,
            rig.Clock.UtcNow.AddHours(-1)));

        var reconciled = rig.NewJournal().ReconcilePendingOperations();

        Assert.Equal(expected, Assert.Single(reconciled).State);
    }

    [Fact]
    public async Task CaseOnlyRename_RecoveryRetryOfAFailedLine_FinishesTheRename()
    {
        var rig = Rig();
        rig.Fs.Arm(new FaultPlan(3, FaultKind.FailOnce)); // Prepared append, rename ok, FailOnce on the Committed append is not a retry case
        rig.Fs.Arm(null);
        var stat = rig.Disk.GetFileStat(A)!;
        var failed = new JournalEntry("e1", FileOperationType.Move, JournalState.Failed, A, @"C:\photos\A.jpg", stat.Length, stat.LastWriteUtc,
            rig.Clock.UtcNow, Error: "boom");
        rig.Journal.Append(failed);
        var retry = new RecoveryRetryService(rig.Journal, rig.Fs, rig.Clock, rig.Bin);

        var result = await retry.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("A.jpg", Stored(rig, A));
    }

    // ---------------------------------------------------------------- capture group

    private static FaultRig GroupRig(string xmpName = "a.xmp") =>
        new FaultRig().Seed(FaultRig.Photos, "a.jpg", "a.cr2", xmpName, "b.jpg");

    private static Catalog.CaptureGroup Group(string xmpName = "a.xmp") =>
        new(@"C:\photos\a.jpg", @"C:\photos\a.cr2", Path.Combine(FaultRig.Photos, xmpName));

    [Fact]
    public async Task RenameGroup_RenamesEveryMemberInOneJournalGroup_EachKeepingItsExtension()
    {
        var rig = GroupRig();

        var result = await rig.Service.RenameGroupAsync(Group(), "x");

        Assert.True(result.Succeeded, result.Error);
        foreach (var name in new[] { "x.jpg", "x.cr2", "x.xmp" }) Assert.True(rig.Disk.FileExists(Path.Combine(FaultRig.Photos, name)), name);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" }) Assert.False(rig.Disk.FileExists(Path.Combine(FaultRig.Photos, name)), name);
        var entry = LatestOnly(rig);
        Assert.Equal(JournalState.Committed, entry.State);
        Assert.Equal(3, entry.GroupMembers!.Count);
        Assert.All(entry.GroupMembers, member => Assert.Equal(Path.GetDirectoryName(member.Source), Path.GetDirectoryName(member.Destination)));
    }

    [Fact]
    public async Task RenameGroup_SidecarNamedAfterTheRawFile_FollowsTheRawFile()
    {
        var rig = GroupRig("a.cr2.xmp");

        var result = await rig.Service.RenameGroupAsync(Group("a.cr2.xmp"), "x");

        Assert.True(result.Succeeded, result.Error);
        Assert.True(rig.Disk.FileExists(@"C:\photos\x.cr2.xmp"));
        Assert.False(rig.Disk.FileExists(@"C:\photos\a.cr2.xmp"));
    }

    [Fact]
    public async Task RenameGroup_OneTargetTaken_ChangesNothingAndJournalsNothing()
    {
        var rig = GroupRig();
        rig.Disk.AddFile(@"C:\photos\x.cr2", "someone else's raw");

        var result = await rig.Service.RenameGroupAsync(Group(), "x");

        Assert.False(result.Succeeded);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" }) Assert.True(rig.HasFull(Path.Combine(FaultRig.Photos, name)), name);
        Assert.False(rig.Disk.FileExists(@"C:\photos\x.jpg"));
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public async Task RenameGroup_AMemberCannotBeRenamed_RollsTheOthersBackAndDismissesTheLine()
    {
        var rig = GroupRig();
        // calls: 1 Prepared append, 2 jpg rename, 3 cr2 rename (fails), ...
        rig.Fs.Arm(new FaultPlan(3, FaultKind.FailOnce));

        var result = await rig.Service.RenameGroupAsync(Group(), "x");

        Assert.False(result.Succeeded);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" }) Assert.True(rig.HasFull(Path.Combine(FaultRig.Photos, name)), name);
        Assert.False(rig.Disk.FileExists(@"C:\photos\x.jpg"));
        Assert.Equal(JournalState.Dismissed, LatestOnly(rig).State);
    }

    [Fact]
    public async Task RenameGroup_ThenUndo_RestoresEveryName()
    {
        var rig = GroupRig();
        var result = await rig.Service.RenameGroupAsync(Group(), "x");
        rig.Undo.RegisterGroup(result);

        var undone = await rig.Undo.UndoLastAsync();

        Assert.True(undone.Succeeded, undone.Error);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" }) Assert.True(rig.HasFull(Path.Combine(FaultRig.Photos, name)), name);
        foreach (var name in new[] { "x.jpg", "x.cr2", "x.xmp" }) Assert.False(rig.Disk.FileExists(Path.Combine(FaultRig.Photos, name)), name);
    }

    [Fact]
    public async Task RenameGroup_CaseOnly_RenamesAllMembersUndoesAndReconcilesClean()
    {
        var rig = GroupRig();

        var result = await rig.Service.RenameGroupAsync(Group(), "A");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["A.jpg", "A.cr2", "A.xmp"], new[] { "a.jpg", "a.cr2", "a.xmp" }.Select(n => Stored(rig, Path.Combine(FaultRig.Photos, n))));
        Assert.Empty(rig.NewJournal().ReconcilePendingOperations());
        Assert.Equal(RecoveryVerdict.AlreadyDone, new RecoveryFileCheck(rig.Fs).Check(LatestOnly(rig)).Verdict);

        rig.Undo.RegisterGroup(result);
        var undone = await rig.Undo.UndoLastAsync();

        Assert.True(undone.Succeeded, undone.Error);
        Assert.Equal(["a.jpg", "a.cr2", "a.xmp"], new[] { "a.jpg", "a.cr2", "a.xmp" }.Select(n => Stored(rig, Path.Combine(FaultRig.Photos, n))));
    }

    [Fact]
    public async Task RenameGroup_PartlyAlreadyNamed_RenamesOnlyTheOthers()
    {
        // The RAW already carries the target spelling (case-only for it); the JPEG and the XMP still need the change.
        var rig = new FaultRig().Seed(FaultRig.Photos, "a.jpg", "A.cr2", "a.xmp");
        var group = new Catalog.CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\A.cr2", @"C:\photos\a.xmp");

        var result = await rig.Service.RenameGroupAsync(group, "A");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("A.jpg", Stored(rig, @"C:\photos\a.jpg"));
        Assert.Equal("A.cr2", Stored(rig, @"C:\photos\a.cr2"));
        Assert.Equal("A.xmp", Stored(rig, @"C:\photos\a.xmp"));
        Assert.Equal(2, LatestOnly(rig).GroupMembers!.Count);
    }

    [Fact]
    public async Task RenameGroup_EveryMemberAlreadyNamed_IsASuccessWithNoJournalLine()
    {
        var rig = new FaultRig().Seed(FaultRig.Photos, "A.jpg", "A.cr2");
        var group = new Catalog.CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2");

        var result = await rig.Service.RenameGroupAsync(group, "A");

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public async Task RenameGroup_InvalidName_IsRejectedBeforeAnything_IsTouched()
    {
        var rig = GroupRig();

        var result = await rig.Service.RenameGroupAsync(Group(), "bad:name");

        Assert.False(result.Succeeded);
        Assert.Empty(rig.JournalLines());
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" }) Assert.True(rig.HasFull(Path.Combine(FaultRig.Photos, name)), name);
    }

    // ---------------------------------------------------------------- existing Move/Copy contract is unchanged

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task OrdinaryMoveOrCopyIntoTheSourceFolder_IsStillRefused(FileOperationType operation)
    {
        var rig = Rig();

        var result = await rig.Service.ExecuteAsync(new FileActionRequest(A, operation, @"C:\photos"));

        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.Empty(rig.JournalLines());
    }

    [Fact]
    public void FileOperationType_HasNoRenameMember_SoAnOlderBuildReadsEveryJournalLine()
    {
        // ADR 0003 cross-build compatibility: a rename is a Move; adding an enum value would make older builds drop or mis-map lines.
        Assert.Equal(["Move", "Copy", "Recycle"], Enum.GetNames<FileOperationType>().Where(n => n is "Move" or "Copy" or "Recycle" or "Rename"));
        Assert.DoesNotContain("Rename", Enum.GetNames<FileOperationType>());
    }
}
