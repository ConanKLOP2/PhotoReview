using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Crash-consistency / fault-injection matrices for the file-action pipeline (Move, Copy, Delete via a fake Recycle Bin, Undo, group
/// actions, Recovery retry and startup reconcile). Each scenario is run once per (mutating call x fault kind): the process dies before,
/// during (torn/partial) or after the call, or the call fails once and the process lives on. After each run the invariants in
/// <see cref="FaultInvariants"/> are checked (no photo lost, duplicated or silently overwritten; every journal verdict true; nothing
/// Prepared and a second reconcile a no-op after restart; Recovery retries and a same-named foreign photo are safe).
/// In-memory only (no real disk, no real Recycle Bin).
/// </summary>
public sealed class FileActionFaultInjectionTests
{
    private static Task<FaultRig> SingleRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "b.jpg"));

    private static Task<FaultRig> GroupRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "a.cr2", "a.xmp", "b.jpg"));

    private static async Task Do(FaultRig rig, FileActionRequest request) => rig.Outcomes.Add(await rig.Service.ExecuteAsync(request));

    private static async Task DoGroup(FaultRig rig, FileOperationType operation, string? destination = "sel") =>
        rig.Outcomes.Add(await rig.Service.ExecuteGroupAsync(new CaptureGroupActionRequest(FaultRig.Capture(), operation, destination)));

    [Fact(DisplayName = "Move: every fault point keeps the photo and tells the truth")]
    public Task Move_Matrix() => FaultInjectionMatrix.RunAsync("Move", SingleRig,
        rig => Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "sel")));

    [Fact(DisplayName = "Copy: every fault point keeps the source and never calls a truncated copy done")]
    public Task Copy_Matrix() => FaultInjectionMatrix.RunAsync("Copy", SingleRig,
        rig => Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Copy, "sel")));

    [Fact(DisplayName = "Delete (fake bin): every fault point leaves the photo in exactly one place")]
    public Task Recycle_Matrix() => FaultInjectionMatrix.RunAsync("Recycle", SingleRig,
        rig => Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Recycle, null)));

    [Fact(DisplayName = "Two Moves in a row: a fault in the second never harms the first, and neither is lost")]
    public Task TwoMoves_Matrix() => FaultInjectionMatrix.RunAsync("Move+Move", SingleRig, async rig =>
    {
        await Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "sel"));
        await Do(rig, new FileActionRequest(@"C:\photos\b.jpg", FileOperationType.Move, "sel"));
    });

    [Fact(DisplayName = "Copy then Delete of the original: every fault point keeps one complete photo")]
    public Task CopyThenDelete_Matrix() => FaultInjectionMatrix.RunAsync("Copy+Delete", SingleRig, async rig =>
    {
        await Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Copy, "sel"));
        await Do(rig, new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Recycle, null));
    });

    [Fact(DisplayName = "Group Move (JPEG+RAW+XMP): every fault point loses no member and the verdict matches the disk")]
    public Task GroupMove_Matrix() => FaultInjectionMatrix.RunAsync("GroupMove", GroupRig,
        rig => DoGroup(rig, FileOperationType.Move));

    [Fact(DisplayName = "Group Copy: every fault point keeps every source and never calls a truncated member done")]
    public Task GroupCopy_Matrix() => FaultInjectionMatrix.RunAsync("GroupCopy", GroupRig,
        rig => DoGroup(rig, FileOperationType.Copy));

    [Fact(DisplayName = "Group Delete (fake bin): every fault point leaves each member in exactly one place")]
    public Task GroupRecycle_Matrix() => FaultInjectionMatrix.RunAsync("GroupRecycle", GroupRig,
        rig => DoGroup(rig, FileOperationType.Recycle, null));
}
