using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// FEAT-RENAME-DESIGN 7.2: the rename pipeline (single, case-only, JPEG+RAW+XMP group, Undo) through EVERY injection point of the
/// fault matrix (#482): crash/failure before and after the journal's Prepared line, before and after the rename, before and after
/// Committed, between two members of a group, then restart + startup reconcile + Recovery retries. A same-folder rename is one atomic
/// rename (the injector gives it its own "rename" kind, so no truncated-destination / copied-not-deleted kinds apply).
/// </summary>
public sealed class RenameFaultInjectionTests
{
    private static Task<FaultRig> SingleRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "b.jpg").Alias("x.jpg", "a.jpg"));

    private static Task<FaultRig> GroupRig() =>
        Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "a.cr2", "a.xmp", "b.jpg")
            .Alias("x.jpg", "a.jpg").Alias("x.cr2", "a.cr2").Alias("x.xmp", "a.xmp"));

    private static async Task Rename(FaultRig rig, string from, string to) =>
        rig.Outcomes.Add(await rig.Service.RenameAsync(new RenameRequest(from, to)));

    private static async Task RenameGroup(FaultRig rig, string stem) =>
        rig.Outcomes.Add(await rig.Service.RenameGroupAsync(FaultRig.Capture(), stem));

    /// <summary>A rename never changes size or write time, and a journal line that says Committed is true under the NEW spelling.</summary>
    private static Task KeepsFingerprint(FaultRig rig, string ctx, List<string> violations)
    {
        foreach (var line in rig.JournalLines().Where(l => l.Type == FileOperationType.Move && l.State == JournalState.Committed))
        {
            foreach (var member in FaultRig.MembersOf(line))
            {
                if (member.Destination is null || rig.Disk.GetFileStat(member.Destination) is not { } stat) continue;
                if (stat.Length != member.Size || stat.LastWriteUtc != member.LastWriteUtc)
                    violations.Add($"{ctx}: renamed {member.Destination} changed size or write time");
                var stored = rig.Disk.GetStoredFileNames(member.Destination);
                if (stored.Count == 1 && !string.Equals(stored[0], Path.GetFileName(member.Destination), StringComparison.Ordinal)
                    && !rig.JournalLines().Any(l => l.Undo == true))
                    violations.Add($"{ctx}: Committed rename to {member.Destination} but the file is stored as '{stored[0]}'");
            }
        }

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Rename: every fault point keeps the photo, never overwrites and tells the truth")]
    public Task Rename_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Rename", SingleRig,
        rig => Rename(rig, @"C:\photos\a.jpg", "x.jpg"), KeepsFingerprint);

    [Fact(DisplayName = "Rename over an existing file never reaches a mutation and loses nothing")]
    public Task Rename_ToTakenName_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Rename-taken",
        () => Task.FromResult(new FaultRig().Seed(FaultRig.Photos, "a.jpg", "b.jpg")),
        rig => Rename(rig, @"C:\photos\a.jpg", "b.jpg"), minCalls: 0);

    [Fact(DisplayName = "Case-only rename: every fault point is judged by the stored spelling, never Lost or Conflict")]
    public Task CaseOnlyRename_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("RenameCase", SingleRig,
        rig => Rename(rig, @"C:\photos\a.jpg", "A.jpg"), KeepsFingerprint);

    [Fact(DisplayName = "Group rename (JPEG+RAW+XMP): every fault point loses no member and rolls back or completes")]
    public Task GroupRename_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("RenameGroup", GroupRig,
        rig => RenameGroup(rig, "x"), KeepsFingerprint);

    [Fact(DisplayName = "Group case-only rename: every fault point is judged per member by the stored spelling")]
    public Task GroupCaseOnlyRename_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("RenameGroupCase", GroupRig,
        rig => RenameGroup(rig, "A"), KeepsFingerprint);

    [Fact(DisplayName = "Rename then Undo: every fault point keeps the photo under one name")]
    public Task RenameThenUndo_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Rename+Undo", SingleRig, async rig =>
    {
        var renamed = await rig.Service.RenameAsync(new RenameRequest(@"C:\photos\a.jpg", "x.jpg"));
        rig.Undo.Register(renamed);
        rig.Outcomes.Add(await rig.Undo.UndoLastAsync());
    });

    [Fact(DisplayName = "Case-only rename then Undo: every fault point keeps the photo under one spelling")]
    public Task CaseOnlyRenameThenUndo_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("RenameCase+Undo", SingleRig, async rig =>
    {
        var renamed = await rig.Service.RenameAsync(new RenameRequest(@"C:\photos\a.jpg", "A.jpg"));
        rig.Undo.Register(renamed);
        rig.Outcomes.Add(await rig.Undo.UndoLastAsync());
    });

    [Fact(DisplayName = "Group rename then Undo: every fault point keeps every member")]
    public Task GroupRenameThenUndo_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("RenameGroup+Undo", GroupRig, async rig =>
    {
        var renamed = await rig.Service.RenameGroupAsync(FaultRig.Capture(), "x");
        rig.Undo.RegisterGroup(renamed);
        rig.Outcomes.Add(await rig.Undo.UndoLastAsync());
    });

    [Fact(DisplayName = "Rename, then a second rename of the same photo: a fault in the second never harms the first")]
    public Task TwoRenames_Matrix() => FaultInjectionMatrix.AssertAllFaultPointsAsync("Rename+Rename", SingleRig, async rig =>
    {
        var first = await rig.Service.RenameAsync(new RenameRequest(@"C:\photos\a.jpg", "x.jpg"));
        if (first.Succeeded) await Rename(rig, @"C:\photos\x.jpg", "A.jpg"); // a failed first rename leaves nothing to rename again
    });

    [Fact(DisplayName = "Interrupted startup reconcile after a rename crash reaches the same verdicts")]
    public Task Rename_ReconcileInterrupted() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("Rename", SingleRig,
        rig => Rename(rig, @"C:\photos\a.jpg", "x.jpg"));

    [Fact(DisplayName = "Interrupted startup reconcile after a case-only rename crash reaches the same verdicts")]
    public Task CaseOnlyRename_ReconcileInterrupted() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("RenameCase", SingleRig,
        rig => Rename(rig, @"C:\photos\a.jpg", "A.jpg"));

    [Fact(DisplayName = "Interrupted startup reconcile after a group rename crash reaches the same verdicts")]
    public Task GroupRename_ReconcileInterrupted() => FaultInjectionMatrix.AssertReconcileInterruptedAsync("RenameGroup", GroupRig,
        rig => RenameGroup(rig, "x"));
}
