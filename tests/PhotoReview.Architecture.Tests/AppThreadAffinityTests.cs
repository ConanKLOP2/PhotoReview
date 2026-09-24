using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR04 / ADR 0005: the App layer (ViewModels, Coordinators, Services, Windows) is UI-thread
/// affine. Continuations must return to the WPF SynchronizationContext, so App code never uses
/// <c>ConfigureAwait(false)</c>, and it never blocks on a Task (which would deadlock against that
/// same context). Core / Imaging / Platform.Windows are the layers that use ConfigureAwait(false)
/// and push heavy work into Task.Run.
/// </summary>
public sealed class AppThreadAffinityTests
{
    private static readonly Regex ConfigureAwaitFalse = new(
        @"ConfigureAwait\(\s*(continueOnCapturedContext\s*:\s*)?false\s*\)", RegexOptions.Compiled);

    private static readonly Regex BlockingOnTask = new(
        @"\.Result\b|\.Wait\(|\.WaitAll\(|GetAwaiter\(\)\s*\.GetResult\(\)", RegexOptions.Compiled);

    [Fact(DisplayName = "ADR 0005: src/PhotoReview.App never uses ConfigureAwait(false)")]
    [Trait("Category", "Architecture")]
    public void AppLayer_DoesNotUseConfigureAwaitFalse()
    {
        var violations = FindViolations(ConfigureAwaitFalse);

        Assert.True(
            violations.Count == 0,
            "ConfigureAwait(false) in the App layer resumes on a pool thread and mutates UI-only state " +
            "(ReviewCatalog, ViewerState, PropertyChanged) off the UI thread. Remove it; if the callee does " +
            "heavy synchronous work, move that work into Task.Run inside the callee (ADR 0005):\n" +
            string.Join("\n", violations));
    }

    [Fact(DisplayName = "ADR 0005: src/PhotoReview.App never blocks on a Task (.Result/.Wait()/GetAwaiter().GetResult())")]
    [Trait("Category", "Architecture")]
    public void AppLayer_DoesNotBlockOnTasks()
    {
        var violations = FindViolations(BlockingOnTask);

        Assert.True(
            violations.Count == 0,
            "Blocking on a Task in the UI-affine App layer deadlocks once its continuation needs the UI " +
            "thread (ADR 0005). Await it instead:\n" + string.Join("\n", violations));
    }

    private static List<string> FindViolations(Regex pattern)
    {
        const string appDir = "src/PhotoReview.App";
        Assert.NotEmpty(RepoScan.CsFiles(appDir));

        return RepoScan.FindLineViolations(line => pattern.IsMatch(StripLineComment(line)), null, appDir);
    }

    private static string StripLineComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return string.Empty;
        var index = line.IndexOf(" // ", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }
}
