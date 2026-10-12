using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Second-order fault matrices on top of <see cref="FaultInjectionMatrix"/>: the first fault is a REAL crash of a scenario action and
/// the second one hits what the user (or the next start) does about it. Same rig, same invariants, same "restart = new objects over the
/// surviving disk" model; every run is deterministic (call numbers, never time).
/// </summary>
internal static class FaultInjectionSecondOrder
{
    /// <summary>The process dies at the numbered call (the disk shows what that kind of death leaves).</summary>
    private static readonly FaultKind[] CrashKinds =
        [FaultKind.CrashBefore, FaultKind.CrashAfter, FaultKind.CrashPartial, FaultKind.CrashMoveCopiedNotDeleted];

    /// <summary>What can happen to the follow-up step: it dies (cleanly, torn, mid cross-volume move) or fails once in two exception families.</summary>
    private static readonly FaultKind[] FollowUpKinds =
        [FaultKind.CrashBefore, FaultKind.CrashPartial, FaultKind.CrashMoveCopiedNotDeleted, FaultKind.FailOnce, FaultKind.FailPartial, FaultKind.FailUnauthorized];

    private static bool Alive(FaultKind kind) => kind is FaultKind.FailOnce or FaultKind.FailPartial or FaultKind.FailUnauthorized;

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

    /// <summary>A healthy start after the crash: new objects, startup reconcile (the Recovery window then lists what is Failed).</summary>
    private static FaultRig StartedAgain(FaultRig rig)
    {
        _ = rig.Restart().ReconcilePendingOperations();
        rig.Outcomes.Clear();
        rig.Fs.Arm(null);
        return rig;
    }

    /// <summary>"Retry" on every Recovery item, like the user pressing the button on each row.</summary>
    private static async Task RetryAllAsync(FaultRig rig)
    {
        var retry = new RecoveryRetryService(rig.Journal, rig.Fs, rig.Clock, rig.Bin);
        foreach (var failed in rig.Journal.ReadFailedOperations().ToList())
            _ = await retry.RetryMoveOrCopyAsync(failed);
    }

    /// <summary>
    /// For every crash point of <paramref name="act"/> (the process dies there), the Recovery retry that the user starts after the next
    /// start is itself interrupted at each of its own mutating calls. The start after THAT must be consistent: nothing lost, nothing
    /// Prepared, reconcile idempotent, every journal verdict true, a second retry safe.
    /// </summary>
    public static async Task AssertRetryInterruptedAsync(string name, Func<Task<FaultRig>> create, Func<FaultRig, Task> act)
    {
        var violations = new List<string>();
        var probe = await create();
        probe.Fs.Arm(null);
        await act(probe);
        var outerCalls = probe.Fs.Kinds.ToArray();
        var runs = 0;
        var crashStatesWithRetryWork = 0;
        for (var point = 1; point <= outerCalls.Length; point++)
        {
            foreach (var outer in CrashKinds)
            {
                if (!FaultInjectionFileSystem.Applies(outer, outerCalls[point - 1])) continue;
                var outerPlan = new FaultPlan(point, outer);
                var clean = StartedAgain(await CrashedAsync(create, act, outerPlan));
                await RetryAllAsync(clean);
                var retryCalls = clean.Fs.Kinds.ToArray();
                if (retryCalls.Length == 0) continue; // the reconcile alone settled it: no Recovery item to retry
                crashStatesWithRetryWork++;
                for (var inner = 1; inner <= retryCalls.Length; inner++)
                {
                    foreach (var kind in FollowUpKinds)
                    {
                        if (!FaultInjectionFileSystem.Applies(kind, retryCalls[inner - 1])) continue;
                        runs++;
                        var ctx = $"{name} [{outer} at call #{point}/{outerCalls.Length} = {outerCalls[point - 1]}; then the retry {kind} at its call #{inner}/{retryCalls.Length} = {retryCalls[inner - 1]}]";
                        var rig = StartedAgain(await CrashedAsync(create, act, outerPlan));
                        rig.Fs.Arm(new FaultPlan(inner, kind));
                        try
                        {
                            await RetryAllAsync(rig);
                        }
                        catch (Exception ex) when (rig.Fs.Dead)
                        {
                            _ = ex;
                        }
                        catch (Exception ex)
                        {
                            violations.Add($"{ctx}: an I/O failure ESCAPED the retry as {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        await FaultInjectionMatrix.VerifyAsync(rig, ctx, violations, Alive(kind), null, checkOutcomes: false);
                    }
                }
            }
        }

        Assert.True(crashStatesWithRetryWork > 0, $"{name}: no crash point left a Recovery item to retry; the matrix proved nothing");
        Assert.True(runs > 0, $"{name}: no retry run");
        Assert.True(violations.Count == 0, $"{name}: {violations.Count} invariant violation(s) over {runs} interrupted retries:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Take(25)));
    }

    /// <summary>
    /// Crash of <paramref name="first"/> at every point, a healthy restart, then the user acts again (<paramref name="second"/>, typically
    /// the same action on the same file) and THAT is faulted at every call by every kind. Two failed starts in a row must never lose a
    /// photo, overwrite a file the first run left at the destination, or leave a verdict that is not true.
    /// </summary>
    public static async Task AssertDoubleFaultAsync(string name, Func<Task<FaultRig>> create, Func<FaultRig, Task> first, Func<FaultRig, Task> second)
    {
        var violations = new List<string>();
        var probe = await create();
        probe.Fs.Arm(null);
        await first(probe);
        var firstCalls = probe.Fs.Kinds.ToArray();
        var runs = 0;
        for (var point = 1; point <= firstCalls.Length; point++)
        {
            foreach (var outer in CrashKinds)
            {
                if (!FaultInjectionFileSystem.Applies(outer, firstCalls[point - 1])) continue;
                var outerPlan = new FaultPlan(point, outer);
                var clean = StartedAgain(await CrashedAsync(create, first, outerPlan));
                await second(clean);
                var secondCalls = clean.Fs.Kinds.ToArray();
                for (var inner = 1; inner <= secondCalls.Length; inner++)
                {
                    foreach (var kind in FaultInjectionMatrix.Kinds)
                    {
                        if (!FaultInjectionFileSystem.Applies(kind, secondCalls[inner - 1])) continue;
                        runs++;
                        var ctx = $"{name} [{outer} at call #{point}/{firstCalls.Length}; restart; again {kind} at call #{inner}/{secondCalls.Length} = {secondCalls[inner - 1]}]";
                        var rig = StartedAgain(await CrashedAsync(create, first, outerPlan));
                        rig.Fs.Arm(new FaultPlan(inner, kind));
                        var alive = Alive(kind);
                        try
                        {
                            await second(rig);
                        }
                        catch (Exception ex) when (!alive && rig.Fs.Dead)
                        {
                            _ = ex;
                        }
                        catch (Exception ex)
                        {
                            violations.Add($"{ctx}: an I/O failure ESCAPED the service as {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        await FaultInjectionMatrix.VerifyAsync(rig, ctx, violations, alive, null);
                    }
                }
            }
        }

        Assert.True(runs > 0, $"{name}: the second action made no mutating call at any crash point");
        Assert.True(violations.Count == 0, $"{name}: {violations.Count} invariant violation(s) over {runs} double-fault runs:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Take(25)));
    }
}
