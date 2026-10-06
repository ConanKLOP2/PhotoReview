using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Fault-injection matrices for Undo, Recovery retry and an interrupted startup reconcile (see <see cref="FileActionFaultInjectionTests"/>
/// for the single-action matrices and <see cref="FaultInvariants"/> for what is checked). In-memory only.
/// </summary>
public sealed class FileActionFaultInjectionRecoveryTests
{
    private const string A = @"C:\photos\a.jpg";
    private const string ASel = @"C:\photos\sel\a.jpg";
    private static readonly string[] GroupNames = ["a.jpg", "a.cr2", "a.xmp"];

    private static Task<FaultRig> SingleRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "b.jpg"));

    private static Task<FaultRig> GroupRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "a.cr2", "a.xmp", "b.jpg"));

    private static async Task Do(FaultRig rig, FileActionRequest request) => rig.Outcomes.Add(await rig.Service.ExecuteAsync(request));

    private static async Task DoGroup(FaultRig rig, FileOperationType operation, string? destination = "sel") =>
        rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), operation, destination)));

    // ---- Undo ----------------------------------------------------------------------------------------------------------------

    /// <summary>The healthy part: the action ran and was registered for Ctrl+Z; only the undo itself is numbered.</summary>
    private static async Task<FaultRig> AfterAction(bool group, FileOperationType operation)
    {
        var rig = await (group ? GroupRig() : SingleRig());
        var destination = operation == FileOperationType.Move ? "sel" : null;
        if (group)
        {
            var done = await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), operation, destination));
            Assert.True(done.Succeeded, done.Error);
            rig.Undo.RegisterGroup(done);
        }
        else
        {
            var done = await rig.Service.ExecuteAsync(new FileActionRequest(A, operation, destination));
            Assert.True(done.Succeeded, done.Error);
            rig.Undo.Register(done);
        }

        rig.Outcomes.Clear();
        return rig;
    }

    private static async Task Undo(FaultRig rig) => rig.Outcomes.Add(await rig.Undo.UndoLastAsync());

    /// <summary>
    /// After a transient failure of an undo the in-memory undo stack must be truthful and the undo retryable: a single Move stays on the
    /// stack exactly while its file is still at the destination, and a second Ctrl+Z (the fault is over) completes the undo.
    /// </summary>
    private static async Task CheckUndoRetryable(FaultRig rig, string ctx, List<string> violations)
    {
        if (rig.Fs.Dead) return; // a dead process has no stack to keep consistent
        var first = rig.Outcomes.OfType<UndoResult>().LastOrDefault();
        if (first is null || first.Succeeded) return;
        // A cross-volume Move cut short can leave a truncated file at its destination; it is never deleted (D-06: no proof it is ours) and
        // keeps that path occupied, so "retryable" does not apply until the user clears it. The photo itself is safe (checked elsewhere).
        if (rig.Fs.Snapshot(FaultRig.Folders).Any(file => file.Value != rig.ContentOf(file.Key))) return;
        if (rig.Undo.MoveHistory.Count > 0)
        {
            var (source, destination) = rig.Undo.MoveHistory.Peek();
            if (rig.Disk.FileExists(source) || !rig.HasFull(destination))
                violations.Add($"{ctx}: the undo stack still offers {destination} -> {source} but the disk no longer matches (file at source={rig.Disk.FileExists(source)})");
        }
        else if (rig.Undo.LastUndoAction is null && first.Operation == FileOperationType.Move && rig.Disk.FileExists(ASel) && !rig.Disk.FileExists(A))
        {
            violations.Add($"{ctx}: the failed undo DROPPED the stack entry although the photo is still in the destination folder (Ctrl+Z can never bring it back)");
        }

        var second = await rig.Undo.UndoLastAsync();
        if (!second.Succeeded && rig.Undo.LastUndoAction is not null)
            violations.Add($"{ctx}: a second Ctrl+Z after a transient failure did not complete the undo: {second.ErrorMessage}");
    }

    [Fact(DisplayName = "Undo of a Move: every fault point keeps the photo, the stack truthful and the undo retryable")]
    public Task UndoMove_Matrix() => FaultInjectionMatrix.RunAsync("UndoMove", () => AfterAction(false, FileOperationType.Move), Undo, CheckUndoRetryable);

    [Fact(DisplayName = "Undo of a group Move: every fault point loses no member")]
    public Task UndoGroupMove_Matrix() => FaultInjectionMatrix.RunAsync("UndoGroupMove", () => AfterAction(true, FileOperationType.Move), Undo, CheckUndoRetryable);

    [Fact(DisplayName = "Undo of a Delete (restore from the fake bin): every fault point leaves the photo in exactly one place")]
    public Task UndoRecycle_Matrix() => FaultInjectionMatrix.RunAsync("UndoRecycle", () => AfterAction(false, FileOperationType.Recycle), Undo, CheckUndoRetryable, minCalls: 1);

    [Fact(DisplayName = "Undo of a group Delete: every fault point leaves each member in exactly one place")]
    public Task UndoGroupRecycle_Matrix() => FaultInjectionMatrix.RunAsync("UndoGroupRecycle", () => AfterAction(true, FileOperationType.Recycle), Undo, CheckUndoRetryable);

    // ---- Recovery retry --------------------------------------------------------------------------------------------------------

    private static async Task<FaultRig> WithFailedSingle(FileOperationType type)
    {
        var rig = await SingleRig();
        var stat = rig.Disk.GetFileStat(A)!;
        rig.Journal.Append(new JournalEntry("failed1", type, JournalState.Failed, A, ASel, stat.Length, stat.LastWriteUtc, rig.Clock.UtcNow, "boom"));
        return rig;
    }

    private static async Task Retry(FaultRig rig)
    {
        var failed = rig.Latest().Values.First(e => e.State == JournalState.Failed);
        var service = new RecoveryRetryService(rig.Journal, rig.Fs, rig.Clock, rig.Bin);
        _ = await service.RetryMoveOrCopyAsync(failed);
    }

    [Fact(DisplayName = "Retry of a failed Move: every fault point keeps the photo and the verdict matches the disk")]
    public Task RetryMove_Matrix() => FaultInjectionMatrix.RunAsync("RetryMove", () => WithFailedSingle(FileOperationType.Move), Retry);

    [Fact(DisplayName = "Retry of a failed Copy: every fault point keeps the source and never calls a truncated copy done")]
    public Task RetryCopy_Matrix() => FaultInjectionMatrix.RunAsync("RetryCopy", () => WithFailedSingle(FileOperationType.Copy), Retry);

    /// <summary>A group action that died after its first member: a.jpg already moved to sel (or already in the bin), the RAW and the XMP untouched.</summary>
    private static async Task<FaultRig> WithHalfDoneGroup(FileOperationType type)
    {
        var rig = await GroupRig();
        var members = GroupNames.Select(name =>
        {
            var source = Path.Combine(FaultRig.Photos, name);
            var stat = rig.Disk.GetFileStat(source)!;
            return new JournalGroupMember(source, type == FileOperationType.Recycle ? null : Path.Combine(FaultRig.Sel, name), stat.Length, stat.LastWriteUtc);
        }).ToArray();
        if (type == FileOperationType.Move)
        {
            rig.Disk.CreateDirectory(FaultRig.Sel);
            rig.Disk.Move(members[0].Source, members[0].Destination!);
        }
        else
        {
            rig.Bin.Items.Add(new FaultBin.Item(members[0].Source, rig.ContentOf(members[0].Source), new FileStat(members[0].Size, members[0].LastWriteUtc)));
            rig.Disk.Delete(members[0].Source);
        }

        rig.Journal.Append(new JournalEntry("failedgroup", type, JournalState.Failed, members[0].Source, members[0].Destination, members[0].Size,
            members[0].LastWriteUtc, rig.Clock.UtcNow, "boom", GroupId: "g1", GroupMembers: members));
        return rig;
    }

    [Fact(DisplayName = "Retry of a half-moved group Move: every fault point loses no member")]
    public Task RetryGroupMove_Matrix() => FaultInjectionMatrix.RunAsync("RetryGroupMove", () => WithHalfDoneGroup(FileOperationType.Move), Retry);

    [Fact(DisplayName = "Retry of a half-deleted group Delete: every fault point leaves each member in exactly one place")]
    public Task RetryGroupRecycle_Matrix() => FaultInjectionMatrix.RunAsync("RetryGroupRecycle", () => WithHalfDoneGroup(FileOperationType.Recycle), Retry);

    // ---- The startup reconcile is itself interrupted ---------------------------------------------------------------------------

    [Fact(DisplayName = "Reconcile interrupted at each of its own writes after a Move crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_Move() => FaultInjectionMatrix.RunReconcileInterruptedAsync("ReconcileMove", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Move, "sel")));

    [Fact(DisplayName = "Reconcile interrupted at each of its own writes after a Copy crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_Copy() => FaultInjectionMatrix.RunReconcileInterruptedAsync("ReconcileCopy", SingleRig,
        rig => Do(rig, new FileActionRequest(A, FileOperationType.Copy, "sel")));

    [Fact(DisplayName = "Reconcile interrupted at each of its own writes after a group Move crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_GroupMove() => FaultInjectionMatrix.RunReconcileInterruptedAsync("ReconcileGroupMove", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move));

    [Fact(DisplayName = "Reconcile interrupted at each of its own writes after a group Delete crash: the next start reaches the same verdict")]
    public Task ReconcileInterrupted_GroupRecycle() => FaultInjectionMatrix.RunReconcileInterruptedAsync("ReconcileGroupRecycle", GroupRig,
        rig => DoGroup(rig, FileOperationType.Recycle, null));
}
