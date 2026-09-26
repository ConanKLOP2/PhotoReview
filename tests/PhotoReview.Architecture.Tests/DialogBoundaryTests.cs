using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR03c: startup and coordinator code must go through <c>IDialogService</c> rather than calling
/// <c>MessageBox.Show</c> directly. Only the WPF dialog service implementation and window code-behind
/// (view layer) are allowed to call it.
/// </summary>
public sealed class DialogBoundaryTests
{
    [Fact(DisplayName = "Rule 9: MessageBox.Show( is only called from Services/WpfDialogService.cs and *Window.xaml.cs")]
    [Trait("Category", "Architecture")]
    public void MessageBoxShow_OnlyCalledFrom_WpfDialogServiceOrWindowCodeBehind()
    {
        var violations = RepoScan.FindLineViolations(
            line => line.Contains("MessageBox.Show(", StringComparison.Ordinal),
            relative => relative.EndsWith("Services/WpfDialogService.cs", StringComparison.Ordinal)
                || relative.EndsWith("Window.xaml.cs", StringComparison.Ordinal),
            "src");

        Assert.True(
            violations.Count == 0,
            $"MessageBox.Show( called outside the allowed dialog boundary; route through IDialogService instead:\n{string.Join("\n", violations)}");
    }

    private static readonly Regex NewWindow = new(@"\bnew\s+[A-Z]\w*Window\s*\(", RegexOptions.CultureInvariant);

    /// <summary>
    /// Window constructions allowed outside the two composition/dialog files, as (file, construction) pairs: the
    /// Settings window opens its own child editor for the action profiles it is editing (an owned sub-dialog of an
    /// already-open dialog, not an app window).
    /// </summary>
    private static readonly (string File, string Construction)[] AllowedElsewhere =
    [
        ("src/PhotoReview.App/SettingsWindow.xaml.cs", "new ActionProfilesWindow("),
    ];

    [Fact(DisplayName = "Rule AR19: app windows are constructed only in Services/WpfDialogService.cs and App.xaml.cs (plus the explicit allowlist)")]
    [Trait("Category", "Architecture")]
    public void WindowConstruction_OnlyIn_WpfDialogServiceOrAppComposition()
    {
        var violations = RepoScan.FindLineViolations(
            line => NewWindow.IsMatch(line),
            relative => relative.EndsWith("Services/WpfDialogService.cs", StringComparison.Ordinal)
                || relative.EndsWith("PhotoReview.App/App.xaml.cs", StringComparison.Ordinal),
            "src");
        violations.RemoveAll(violation => AllowedElsewhere.Any(allowed =>
            violation.StartsWith(allowed.File + ":", StringComparison.Ordinal)
            && violation.Contains(allowed.Construction, StringComparison.Ordinal)));

        Assert.True(
            violations.Count == 0,
            $"A window is constructed outside the dialog boundary; add an IDialogService method instead:\n{string.Join("\n", violations)}");
    }
}
