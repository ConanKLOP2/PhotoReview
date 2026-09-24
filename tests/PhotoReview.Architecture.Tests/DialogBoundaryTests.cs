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
}
