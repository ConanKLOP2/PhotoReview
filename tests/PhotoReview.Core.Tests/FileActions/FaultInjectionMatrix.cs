using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Drives a scenario through EVERY injection point (each numbered mutating call x each applicable <see cref="FaultKind"/>) and checks
/// the data-safety invariants after the "restart + startup reconcile" and after the follow-up actions a user would take next. All
/// violations of one matrix are collected and reported together, each with the exact injection point that produced it.
/// </summary>
internal static class FaultInjectionMatrix
{
    public static readonly FaultKind[] Kinds = Enum.GetValues<FaultKind>();

    /// <summary>
    /// Runs <paramref name="act"/> on a fresh rig for every injection point. <paramref name="create"/> builds the rig AND runs the
    /// healthy part of the scenario (the setup is never faulted); only <paramref name="act"/> is numbered.
    /// </summary>
    public static async Task RunAsync(string name, Func<Task<FaultRig>> create, Func<FaultRig, Task> act,
        Func<FaultRig, string, List<string>, Task>? extraCheck = null, FaultKind[]? kinds = null, int minCalls = 2)
    {
        var violations = new List<string>();
        var probe = await create();
        probe.Fs.Arm(null);
        await act(probe);
        var calls = probe.Fs.Kinds.ToArray();
        Assert.True(calls.Length >= minCalls, $"{name}: the scenario must perform at least {minCalls} mutating calls (saw {calls.Length}); the matrix would prove nothing");
        // Healthy run: nothing may be violated either (guards the checker itself against false positives).
        await VerifyAsync(probe, $"{name} [healthy]", violations, alive: true, extraCheck);

        var runs = 0;
        for (var point = 1; point <= calls.Length; point++)
        {
            foreach (var kind in kinds ?? Kinds)
            {
                if (!FaultInjectionFileSystem.Applies(kind, calls[point - 1])) continue;
                runs++;
                var ctx = $"{name} [{kind} at call #{point}/{calls.Length} = {calls[point - 1]}]";
                var rig = await create();
                rig.Fs.Arm(new FaultPlan(point, kind));
                var alive = kind is FaultKind.FailOnce or FaultKind.FailPartial;
                try
                {
                    await act(rig);
                }
                catch (Exception ex) when (!alive && rig.Fs.Dead)
                {
                    _ = ex; // the process died: nothing is left to observe except the disk
                }
                catch (Exception ex) when (alive)
                {
                    violations.Add($"{ctx}: an I/O failure ESCAPED the service as {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                await VerifyAsync(rig, ctx, violations, alive, extraCheck);
            }
        }

        Assert.True(runs >= calls.Length, $"{name}: only {runs} injection runs");
        Assert.True(violations.Count == 0, $"{name}: {violations.Count} invariant violation(s) over {runs} injection runs:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Take(25)));
    }

    public static async Task VerifyAsync(FaultRig rig, string ctx, List<string> violations, bool alive,
        Func<FaultRig, string, List<string>, Task>? extraCheck, bool checkOutcomes = true)
    {
        // 1. What the code under test REPORTED must be the truth (checked on the disk exactly as the failure left it).
        if (checkOutcomes) CheckOutcomes(rig, ctx, violations);
        if (extraCheck is not null) await extraCheck(rig, ctx, violations);
        _ = alive;

        // 2. The disk itself is consistent right after the failure (before any recovery has had a chance to run).
        FaultInvariants.CheckState(rig, ctx + " (right after the fault)", violations, afterRecovery: false);

        // 3. Restart + startup reconcile: nothing left Prepared, idempotent, state consistent.
        var journal = rig.Restart();
        _ = journal.ReconcilePendingOperations();
        FaultInvariants.CheckState(rig, ctx + " (after restart+reconcile)", violations, afterRecovery: true);
        var settled = rig.JournalText();
        _ = journal.ReconcilePendingOperations();
        _ = rig.NewJournal().ReconcilePendingOperations();
        if (rig.JournalText() != settled)
            violations.Add($"{ctx}: reconcile is NOT idempotent (a second pass appended journal lines)");

        // 4. The user's next steps must also be safe: a different photo with the same name never overwrites anything, and retrying every
        //    Recovery item loses nothing.
        await FaultInvariants.FollowUpsAsync(rig, ctx, violations);
    }


    /// <summary>One comparable line per live journal verdict (Dismissed lines and Ids excluded: Ids differ between two runs).</summary>
    public static string Summary(FaultRig rig) => string.Join(" ; ", rig.Latest().Values
        .Where(e => e.State != JournalState.Dismissed)
        .Select(e => $"{e.Type}|{e.State}|undo={e.Undo}|{string.Join(",", FaultRig.MembersOf(e).Select(m => m.Source + ">" + m.Destination))}")
        .OrderBy(line => line, StringComparer.Ordinal));

    private static async Task<FaultRig> CrashedAsync(Func<Task<FaultRig>> create, Func<FaultRig, Task> act, FaultPlan plan)
    {
        var rig = await create();
        rig.Fs.Arm(plan);
        try
        {
            await act(rig);
        }
        catch (Exception) when (rig.Fs.Dead)
        {
            // the process died
        }

        return rig;
    }

    /// <summary>
    /// The startup reconcile itself is interrupted (crash, torn append, transient failure) at every one of its own mutating calls, for
    /// every crash point of <paramref name="act"/>: the next, healthy start must still reach exactly the verdicts an uninterrupted
    /// reconcile reaches, lose nothing, and be idempotent.
    /// </summary>
    public static async Task RunReconcileInterruptedAsync(string name, Func<Task<FaultRig>> create, Func<FaultRig, Task> act)
    {
        var violations = new List<string>();
        var probe = await create();
        probe.Fs.Arm(null);
        await act(probe);
        var total = probe.Fs.Kinds.Count;
        var runs = 0;
        for (var point = 1; point <= total; point++)
        {
            foreach (var outer in new[] { FaultKind.CrashBefore, FaultKind.CrashAfter })
            {
                if (!FaultInjectionFileSystem.Applies(outer, probe.Fs.Kinds[point - 1])) continue;
                var outerPlan = new FaultPlan(point, outer);
                var clean = await CrashedAsync(create, act, outerPlan);
                _ = clean.Restart().ReconcilePendingOperations();
                var expected = Summary(clean);
                var reconcileCalls = clean.Fs.Kinds.ToArray();
                for (var inner = 1; inner <= reconcileCalls.Length; inner++)
                {
                    foreach (var kind in new[] { FaultKind.CrashBefore, FaultKind.CrashPartial, FaultKind.FailOnce })
                    {
                        if (!FaultInjectionFileSystem.Applies(kind, reconcileCalls[inner - 1])) continue;
                        runs++;
                        var ctx = $"{name} [{outer} at call #{point}, then reconcile {kind} at its call #{inner}/{reconcileCalls.Length} = {reconcileCalls[inner - 1]}]";
                        var rig = await CrashedAsync(create, act, outerPlan);
                        var journal = rig.Restart();
                        rig.Fs.Arm(new FaultPlan(inner, kind));
                        try
                        {
                            _ = journal.ReconcilePendingOperations();
                        }
                        catch (Exception) when (rig.Fs.Dead || kind == FaultKind.FailOnce)
                        {
                            // interrupted reconcile
                        }

                        _ = rig.Restart().ReconcilePendingOperations();
                        var actual = Summary(rig);
                        if (actual != expected)
                            violations.Add($"{ctx}: interrupted reconcile then healthy reconcile reached a different verdict.{Environment.NewLine}  expected: {expected}{Environment.NewLine}  actual:   {actual}");
                        await VerifyAsync(rig, ctx, violations, alive: false, null, checkOutcomes: false);
                    }
                }
            }
        }

        Assert.True(runs > 0, $"{name}: the reconcile made no mutating call at any crash point; nothing was interrupted");
        Assert.True(violations.Count == 0, $"{name}: {violations.Count} invariant violation(s) over {runs} interrupted reconciles:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Take(25)));
    }

    private static void CheckOutcomes(FaultRig rig, string ctx, List<string> violations)
    {
        foreach (var outcome in rig.Outcomes)
        {
            switch (outcome)
            {
                case FileActionResult { Succeeded: true, Rejected: false } result:
                    FaultInvariants.CheckSucceededEffect(rig, result.Operation, result.Source, result.DestinationPath, ctx + " (reported success)", violations);
                    break;
                case FileActionResult { Succeeded: false, Operation: FileOperationType.Move } failed when !rig.Fs.Dead:
                    if (failed.SourceRemoved == rig.Disk.FileExists(failed.Source))
                        violations.Add($"{ctx}: failed Move reports SourceRemoved={failed.SourceRemoved} but the source {(rig.Disk.FileExists(failed.Source) ? "is" : "is not")} on disk");
                    break;
                case UndoResult undo:
                    // Whatever an undo reports as back in place (success, or the members a failed one did restore) must really be complete there.
                    foreach (var restored in undo.RestoredPaths ?? (undo.Succeeded ? [undo.Source] : []))
                    {
                        if (!rig.HasFull(restored)) violations.Add($"{ctx}: Undo reported {restored} restored but it is not the complete file there");
                    }

                    break;
                case CaptureGroupActionResult group:
                    foreach (var member in group.Members.Where(m => m.StateKnown && (m.Completed || group.Succeeded)))
                        FaultInvariants.CheckSucceededEffect(rig, group.Operation, member.Member.Source, member.Member.Destination, ctx + " (reported member success)", violations);
                    break;
            }
        }
    }
}
