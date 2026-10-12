using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// The cells the first fault-injection PR (<see cref="FileActionFaultInjectionTests"/>, <see cref="FileActionFaultInjectionRecoveryTests"/>)
/// left open (docs/refactoring/decisions/NOWPF-FAULTINJ-JOURNAL.md): a retry that is itself interrupted after a REAL crash, two faulted
/// starts in a row, cancellation as a fault, Dismiss of a Recovery item, permanent delete on a volume without a bin, and the empty
/// destination folder an action may or may not leave. The UnauthorizedAccessException family is part of every default matrix
/// (<see cref="FaultKind.FailUnauthorized"/>). In-memory only; never the real Recycle Bin.
/// </summary>
public sealed class FileActionFaultInjectionGapTests
{
    private const string A = @"C:\photos\a.jpg";

    private static Task<FaultRig> SingleRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "b.jpg"));

    private static Task<FaultRig> GroupRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "a.cr2", "a.xmp", "b.jpg"));

    private static async Task Do(FaultRig rig, FileActionRequest request) => rig.Outcomes.Add(await rig.Service.ExecuteAsync(request));

    private static async Task DoGroup(FaultRig rig, FileOperationType operation, string? destination = "sel") =>
        rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), operation, destination)));

    private static async Task DoCancellable(FaultRig rig, FileActionRequest request) =>
        rig.Outcomes.Add(await rig.Service.ExecuteAsync(request, rig.Token));

    private static async Task DoGroupCancellable(FaultRig rig, FileOperationType operation, string? destination = "sel") =>
        rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), operation, destination), rig.Token));

    private static Task Move(FaultRig rig) => Do(rig, new FileActionRequest(A, FileOperationType.Move, "sel"));

    // ---- 1. The Recovery retry is itself interrupted after a REAL crash --------------------------------------------------------

    [Fact(DisplayName = "Move crashed anywhere, then its Recovery retry interrupted anywhere: nothing lost, nothing Prepared")]
    public Task RetryInterrupted_Move() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("Move>retry", SingleRig, Move);

    [Fact(DisplayName = "Copy crashed anywhere, then its Recovery retry interrupted anywhere: sources intact, no truncated copy called done")]
    public Task RetryInterrupted_Copy() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("Copy>retry", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Copy, "sel")));

    [Fact(DisplayName = "Group Move crashed anywhere, then its Recovery retry interrupted anywhere: no member lost")]
    public Task RetryInterrupted_GroupMove() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("GroupMove>retry", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move));

    [Fact(DisplayName = "Group Copy crashed anywhere, then its Recovery retry interrupted anywhere: every source intact")]
    public Task RetryInterrupted_GroupCopy() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("GroupCopy>retry", GroupRig,
        rig => DoGroup(rig, FileOperationType.Copy));

    [Fact(DisplayName = "Group Delete crashed anywhere, then its Recovery retry interrupted anywhere: each member in exactly one place")]
    public Task RetryInterrupted_GroupRecycle() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("GroupRecycle>retry", GroupRig,
        rig => DoGroup(rig, FileOperationType.Recycle, null));

    private static Task<FaultRig> AfterUndoable(bool group, FileOperationType operation) =>
        FileActionFaultInjectionRecoveryTests.AfterAction(group, operation);

    [Fact(DisplayName = "Undo of a Move crashed anywhere, then its Recovery retry interrupted anywhere: nothing lost")]
    public Task RetryInterrupted_UndoMove() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("UndoMove>retry",
        () => AfterUndoable(false, FileOperationType.Move), FileActionFaultInjectionRecoveryTests.Undo);

    [Fact(DisplayName = "Undo of a group Move crashed anywhere, then its Recovery retry interrupted anywhere: no member lost")]
    public Task RetryInterrupted_UndoGroupMove() => FaultInjectionSecondOrder.AssertRetryInterruptedAsync("UndoGroupMove>retry",
        () => AfterUndoable(true, FileOperationType.Move), FileActionFaultInjectionRecoveryTests.Undo);

    // ---- 1b. The startup reconcile is interrupted (cells the first PR left open) ---------------------------------------------

    [Fact(DisplayName = "Reconcile interrupted after a group Copy crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_GroupCopy() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcileGroupCopy", GroupRig,
        rig => DoGroup(rig, FileOperationType.Copy));

    [Fact(DisplayName = "Reconcile interrupted after a single Delete crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_Recycle() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcileRecycle", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Recycle, null)));

    [Fact(DisplayName = "Reconcile interrupted after a permanent Delete crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_PermanentDelete() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcilePermanent", NoBinGroup, PermanentGroup);

    [Fact(DisplayName = "Reconcile interrupted after an Undo of a Move crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_UndoMove() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcileUndoMove",
        () => AfterUndoable(false, FileOperationType.Move), FileActionFaultInjectionRecoveryTests.Undo);

    [Fact(DisplayName = "Reconcile interrupted after an Undo of a group Move crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_UndoGroupMove() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcileUndoGroupMove",
        () => AfterUndoable(true, FileOperationType.Move), FileActionFaultInjectionRecoveryTests.Undo);

    [Fact(DisplayName = "Reconcile interrupted after an Undo of a group Delete crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_UndoGroupRecycle() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("ReconcileUndoGroupRecycle",
        () => AfterUndoable(true, FileOperationType.Recycle), FileActionFaultInjectionRecoveryTests.Undo);

    // ---- 2. Two faulted starts in a row ---------------------------------------------------------------------------------------

    [Fact(DisplayName = "Move crashed anywhere, restart, the same Move again faulted anywhere: no overwrite, no loss")]
    public Task DoubleFault_SameMoveAgain() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("Move>Move again", SingleRig, Move, Move);

    [Fact(DisplayName = "Move crashed anywhere, restart, ANOTHER photo moved into the same folder faulted anywhere")]
    public Task DoubleFault_OtherMove() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("Move>Move b", SingleRig, Move,
        rig => Do(rig, new FileActionRequest(@"C:\photos\b.jpg", FileOperationType.Move, "sel")));

    [Fact(DisplayName = "Copy crashed anywhere, restart, the same Copy again faulted anywhere: the source is never harmed")]
    public Task DoubleFault_SameCopyAgain() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("Copy>Copy again", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Copy, "sel")),
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Copy, "sel")));

    [Fact(DisplayName = "Delete crashed anywhere, restart, the same Delete again faulted anywhere: one place, always")]
    public Task DoubleFault_SameRecycleAgain() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("Recycle>Recycle again", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Recycle, null)),
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Recycle, null)));

    [Fact(DisplayName = "Group Move crashed anywhere, restart, the same group Move again faulted anywhere: no member lost or overwritten")]
    public Task DoubleFault_SameGroupMoveAgain() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("GroupMove>GroupMove again", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move), rig => DoGroup(rig, FileOperationType.Move));

    [Fact(DisplayName = "Group Move crashed anywhere, restart, the capture is deleted instead, faulted anywhere")]
    public Task DoubleFault_GroupMoveThenGroupRecycle() => FaultInjectionSecondOrder.AssertDoubleFaultAsync("GroupMove>GroupRecycle", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move), rig => DoGroup(rig, FileOperationType.Recycle, null));

    // ---- 3. Cancellation as a fault (the process lives; the token is cancelled right after each numbered call) -------------------

    private static readonly FaultKind[] CancelOnly = [FaultKind.CancelAfter];

    [Fact(DisplayName = "Cancel after any call of a Move: the photo is in exactly one place and the verdict is true")]
    public Task Cancel_Move() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel Move", SingleRig,
        rig => DoCancellable(rig, new FileActionRequest(A, FileOperationType.Move, "sel")), kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a Copy: the source is intact and no half copy is reported done")]
    public Task Cancel_Copy() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel Copy", SingleRig,
        rig => DoCancellable(rig, new FileActionRequest(A, FileOperationType.Copy, "sel")), kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a Delete: the photo is in exactly one place")]
    public Task Cancel_Recycle() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel Recycle", SingleRig,
        rig => DoCancellable(rig, new FileActionRequest(A, FileOperationType.Recycle, null)), kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a group Move: every member is back or complete, never half")]
    public Task Cancel_GroupMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel GroupMove", GroupRig,
        rig => DoGroupCancellable(rig, FileOperationType.Move), kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a group Copy: every source intact, no leftover half capture")]
    public Task Cancel_GroupCopy() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel GroupCopy", GroupRig,
        rig => DoGroupCancellable(rig, FileOperationType.Copy), kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a group Delete: each member in exactly one place")]
    public Task Cancel_GroupRecycle() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel GroupRecycle", GroupRig,
        rig => DoGroupCancellable(rig, FileOperationType.Recycle, null), kinds: CancelOnly);

    private static async Task CancellableRetry(FaultRig rig)
    {
        var failed = rig.Latest().Values.First(e => e.State == JournalState.Failed);
        var service = new RecoveryRetryService(rig.Journal, rig.Fs, rig.Clock, rig.Bin);
        _ = await service.RetryMoveOrCopyAsync(failed, ct: rig.Token);
    }

    [Fact(DisplayName = "Cancel after any call of a Recovery retry of a Move: the photo is safe and the item stays retryable")]
    public Task Cancel_RetryMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel RetryMove",
        () => FileActionFaultInjectionRecoveryTests.WithFailedSingle(FileOperationType.Move), CancellableRetry, kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a Recovery retry of a half-moved group: no member lost")]
    public Task Cancel_RetryGroupMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel RetryGroupMove",
        () => FileActionFaultInjectionRecoveryTests.WithHalfDoneGroup(FileOperationType.Move), CancellableRetry, kinds: CancelOnly);

    // ---- Guards: the new fault kinds really bite (a matrix that cannot observe its fault would pass for the wrong reason) --------

    [Fact(DisplayName = "Cancel right after the Prepared line of a group Move is observed: rolled back, nothing moved, folder left empty or removed")]
    public async Task CancelAfterPrepared_GroupMove_IsObservedAndRolledBack()
    {
        var rig = await GroupRig();
        var probe = await GroupRig();
        await DoGroup(probe, FileOperationType.Move);
        rig.Fs.Arm(new FaultPlan(1 + probe.Fs.Kinds.IndexOf("append"), FaultKind.CancelAfter)); // right after the Prepared line
        var result = await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), FileOperationType.Move, "sel"), rig.Token);
        Assert.True(rig.Token.IsCancellationRequested);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("move", rig.Fs.Kinds);
        Assert.All(rig.Names.ToList(), name => Assert.True(rig.HasFull(Path.Combine(FaultRig.Photos, name))));
        Assert.Empty(rig.Fs.Snapshot([FaultRig.Sel]));
        Assert.DoesNotContain(rig.Latest().Values, e => e.State is JournalState.Prepared or JournalState.Committed);
    }

    [Fact(DisplayName = "Cancel right after the Prepared line of a single Move is observed: the photo stays, the result is a failure, nothing is Prepared")]
    public async Task CancelAfterPrepared_SingleMove_IsObserved()
    {
        var rig = await SingleRig();
        var probe = await SingleRig();
        await Move(probe);
        rig.Fs.Arm(new FaultPlan(1 + probe.Fs.Kinds.IndexOf("append"), FaultKind.CancelAfter));
        var result = await rig.Service.ExecuteAsync(new FileActionRequest(A, FileOperationType.Move, "sel"), rig.Token);
        Assert.False(result.Succeeded);
        Assert.True(rig.HasFull(A));
        Assert.False(rig.Disk.FileExists(@"C:\photos\sel\a.jpg"));
        Assert.DoesNotContain(rig.Latest().Values, e => e.State == JournalState.Prepared);
    }

    [Theory(DisplayName = "An UnauthorizedAccessException at the file operation is a clean failure: reported, source intact, journaled, retryable")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task Unauthorized_AtTheFileOperation_IsACleanFailure(FileOperationType operation)
    {
        var rig = await SingleRig();
        var probe = await SingleRig();
        await Do(probe, new FileActionRequest(A, operation, "sel"));
        var kind = operation == FileOperationType.Move ? "move" : "copy";
        rig.Fs.Arm(new FaultPlan(1 + probe.Fs.Kinds.IndexOf(kind), FaultKind.FailUnauthorized));
        var result = await rig.Service.ExecuteAsync(new FileActionRequest(A, operation, "sel"));
        Assert.False(result.Succeeded);
        Assert.Equal(kind, rig.Fs.Kinds[probe.Fs.Kinds.IndexOf(kind)]); // the fault hit the file operation itself
        Assert.True(rig.HasFull(A));
        Assert.False(rig.Disk.FileExists(@"C:\photos\sel\a.jpg"));
        var latest = Assert.Single(rig.Latest().Values);
        Assert.Equal(JournalState.Failed, latest.State);
    }

    [Fact(DisplayName = "Cancel after any call of a permanent delete (no bin): stays inside the action, journaled")]
    public Task Cancel_PermanentDelete() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel PermanentDelete", NoBinSingle,
        rig => DoCancellable(rig, new FileActionRequest(A, FileOperationType.Recycle, null, AllowPermanentDelete: true)), PermanentStaysInScope, kinds: CancelOnly);

    [Fact(DisplayName = "Cancel after any call of a permanent group delete (no bin): stays inside the action, journaled")]
    public Task Cancel_PermanentDeleteGroup() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Cancel PermanentDeleteGroup", NoBinGroup,
        async rig => rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(
            new CaptureGroupActionRequest(FaultRig.Capture(), FileOperationType.Recycle, null, AllowPermanentDelete: true), rig.Token)),
        PermanentStaysInScope, kinds: CancelOnly);

    // ---- 4. Dismiss of a Recovery item (an append of its own) ------------------------------------------------------------------

    private static Task DismissFailed(FaultRig rig)
    {
        var failed = rig.Latest().Values.Where(e => e.State == JournalState.Failed).ToList();
        try
        {
            rig.Outcomes.Add(rig.Journal.Dismiss(failed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Contract: a failed dismiss throws and the item simply stays in the Recovery window.
        }

        return Task.CompletedTask;
    }

    /// <summary>An acknowledged dismiss must be on the disk as the item's LATEST verdict; an unacknowledged one leaves the item Failed.</summary>
    private static Task DismissIsTruthful(FaultRig rig, string ctx, List<string> violations)
    {
        var acknowledged = rig.Outcomes.OfType<DismissOutcome>().SelectMany(o => o.Dismissed).ToList();
        var latest = rig.Latest();
        foreach (var entry in acknowledged)
        {
            if (latest[entry.Id].State != JournalState.Dismissed)
                violations.Add($"{ctx}: Dismiss reported {entry.Source} dismissed but the journal's latest verdict is {latest[entry.Id].State}");
        }

        if (!rig.Fs.Dead && acknowledged.Count == 0 && !latest.Values.Any(e => e.State == JournalState.Failed))
            violations.Add($"{ctx}: Dismiss was not acknowledged yet the Failed item vanished from the journal");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Dismiss of a failed Move: every fault point keeps the photo and the item either stays or is truly dismissed")]
    public Task Dismiss_FailedMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Dismiss failed Move",
        () => FileActionFaultInjectionRecoveryTests.WithFailedSingle(FileOperationType.Move), DismissFailed, DismissIsTruthful, minCalls: 1);

    [Fact(DisplayName = "Dismiss of a half-moved group: no member is lost and a torn Dismissed line never hides the item wrongly")]
    public Task Dismiss_HalfMovedGroup() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Dismiss half group",
        () => FileActionFaultInjectionRecoveryTests.WithHalfDoneGroup(FileOperationType.Move), DismissFailed, DismissIsTruthful, minCalls: 1);

    // ---- 5. Permanent delete on a volume without a Recycle Bin (the one intended loss) ------------------------------------------

    private static async Task<FaultRig> NoBinSingle()
    {
        var rig = await SingleRig();
        rig.Bin.VolumeHasBin = false;
        return rig;
    }

    private static async Task<FaultRig> NoBinGroup()
    {
        var rig = await GroupRig();
        rig.Bin.VolumeHasBin = false;
        return rig;
    }

    private static async Task PermanentGroup(FaultRig rig) => rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(
        new CaptureGroupActionRequest(FaultRig.Capture(), FileOperationType.Recycle, null, AllowPermanentDelete: true)));

    /// <summary>Only the capture's own files may be deleted, never via the bin, and the bystander photo stays complete.</summary>
    private static Task PermanentStaysInScope(FaultRig rig, string ctx, List<string> violations)
    {
        if (rig.Bin.Items.Count > 0) violations.Add($"{ctx}: a permanent delete put something in the Recycle Bin");
        if (!rig.HasFull(@"C:\photos\b.jpg")) violations.Add($"{ctx}: the bystander photo b.jpg was harmed by the permanent delete of a.*");
        foreach (var gone in rig.Bin.PermanentlyDeleted.Where(path => !Path.GetFileName(path).StartsWith("a.", StringComparison.OrdinalIgnoreCase)))
            violations.Add($"{ctx}: {gone} was deleted permanently but is not part of the action");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Permanent delete of one photo (opted in, no bin): every fault point stays inside the action and journaled")]
    public Task PermanentDelete_Single() => FaultInjectionMatrix.AssertAllFaultPointsAsync("PermanentDelete", NoBinSingle,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Recycle, null, AllowPermanentDelete: true)), PermanentStaysInScope);

    [Fact(DisplayName = "Permanent delete of a capture (opted in, no bin): every fault point stays inside the action and journaled")]
    public Task PermanentDelete_Group() => FaultInjectionMatrix.AssertAllFaultPointsAsync("PermanentDeleteGroup", NoBinGroup,
        PermanentGroup, PermanentStaysInScope);

    [Fact(DisplayName = "Permanent delete NOT opted in (no bin): refused before any mutation, single and group, nothing journaled")]
    public async Task PermanentDelete_NotOptedIn_IsRefusedWithoutAnyMutation()
    {
        var single = await NoBinSingle();
        var one = await single.Service.ExecuteAsync(new FileActionRequest(A, FileOperationType.Recycle, null));
        Assert.False(one.Succeeded);
        Assert.True(single.HasFull(A));
        Assert.Equal(0, single.Fs.Mutations);
        Assert.Empty(single.Bin.PermanentlyDeleted);

        var group = await NoBinGroup();
        var many = await group.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), FileOperationType.Recycle, null));
        Assert.False(many.Succeeded);
        Assert.All(group.Names.ToList(), name => Assert.True(group.HasFull(Path.Combine(FaultRig.Photos, name))));
        Assert.Equal(0, group.Fs.Mutations);
        Assert.Empty(group.Bin.PermanentlyDeleted);
        Assert.Empty(group.JournalLines());
    }

    // ---- 6. The destination folder an action creates ---------------------------------------------------------------------------

    private static async Task<FaultRig> WithExistingEmptyDestination()
    {
        var rig = await GroupRig();
        rig.Disk.CreateDirectory(FaultRig.Sel);
        return rig;
    }

    /// <summary>A folder that existed before the action (the user's own, even empty) is never removed by any failure or rollback.</summary>
    private static Task PreExistingFolderSurvives(FaultRig rig, string ctx, List<string> violations)
    {
        if (!rig.Disk.DirectoryExists(FaultRig.Sel))
            violations.Add($"{ctx}: the destination folder {FaultRig.Sel} existed before the action and was removed by it");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A failure that happens BEFORE the Prepared line exists (the process lives) leaves no folder this call created; files are
    /// never touched by that cleanup. (After Prepared an empty leftover folder after a full rollback is allowed, see the fragment.)
    /// </summary>
    private static Task CreatedFolderRemovedBeforePrepared(FaultRig rig, string ctx, List<string> violations)
    {
        if (rig.Fs.Dead || rig.JournalLines().Count > 0) return Task.CompletedTask;
        var failed = rig.Outcomes.Any(o => o is FileActionResult { Succeeded: false } or CaptureGroupActionResult { Succeeded: false });
        if (!failed) return Task.CompletedTask;
        foreach (var folder in new[] { Path.Combine(FaultRig.Sel, "sub"), FaultRig.Sel })
        {
            if (rig.Disk.DirectoryExists(folder))
                violations.Add($"{ctx}: the action failed before its journal line and left the folder it created: {folder}");
        }

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Move into an existing empty folder: no fault ever removes the user's folder")]
    public Task Folder_ExistingSurvives_Move() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder kept (Move)", WithExistingEmptyDestination,
        Move, PreExistingFolderSurvives);

    [Fact(DisplayName = "Group Move into an existing empty folder: no fault ever removes the user's folder")]
    public Task Folder_ExistingSurvives_GroupMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder kept (GroupMove)", WithExistingEmptyDestination,
        rig => DoGroup(rig, FileOperationType.Move), PreExistingFolderSurvives);

    [Fact(DisplayName = "Group Copy into an existing empty folder: no fault ever removes the user's folder")]
    public Task Folder_ExistingSurvives_GroupCopy() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder kept (GroupCopy)", WithExistingEmptyDestination,
        rig => DoGroup(rig, FileOperationType.Copy), PreExistingFolderSurvives);

    [Fact(DisplayName = "Move into a new nested folder: a failure before the journal line leaves no folder behind")]
    public Task Folder_CreatedIsRemoved_Move() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder removed (Move)", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Move, @"sel\sub")), CreatedFolderRemovedBeforePrepared);

    [Fact(DisplayName = "Copy into a new nested folder: a failure before the journal line leaves no folder behind")]
    public Task Folder_CreatedIsRemoved_Copy() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder removed (Copy)", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Copy, @"sel\sub")), CreatedFolderRemovedBeforePrepared);

    [Fact(DisplayName = "Group Move into a new nested folder: a failure before the journal line leaves no folder behind")]
    public Task Folder_CreatedIsRemoved_GroupMove() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder removed (GroupMove)", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move, @"sel\sub"), CreatedFolderRemovedBeforePrepared);

    [Fact(DisplayName = "Group Copy into a new nested folder: a failure before the journal line leaves no folder behind")]
    public Task Folder_CreatedIsRemoved_GroupCopy() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Folder removed (GroupCopy)", GroupRig,
        rig => DoGroup(rig, FileOperationType.Copy, @"sel\sub"), CreatedFolderRemovedBeforePrepared);
}
