using System.Reflection;
using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR02d: locks the single-composition-root outcome (AR02-single-composition-root.md, AR02d
/// step 3) so a future change cannot silently reintroduce a second, non-DI way to build
/// <see cref="PhotoReview.App.MainWindow"/> or <see cref="PhotoReview.App.ViewModels.MainViewModel"/>.
/// </summary>
public sealed class AppCompositionTests
{
    [Fact(DisplayName = "AR02d: MainWindow has exactly one constructor")]
    public void MainWindow_HasSingleConstructor()
    {
        var ctors = typeof(PhotoReview.App.MainWindow).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Single(ctors);
    }

    [Fact(DisplayName = "AR02d: PhotoReview.App exposes no public instance fields")]
    public void AppAssembly_HasNoPublicInstanceFields()
    {
        var appAssembly = typeof(PhotoReview.App.App).Assembly;
        var violations = new List<string>();

        foreach (var type in appAssembly.GetExportedTypes())
        {
            // XAML-generated backing fields for named elements (InitializeComponent) are
            // `internal`, not exported by GetExportedTypes()'s field query below, but guard
            // explicitly anyway: only look at fields declared directly on the type (not inherited
            // WPF/BCL fields) and skip compiler-generated ones (e.g. backing fields for
            // auto-properties, which are never public regardless).
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                if (field.IsSpecialName) continue;
                violations.Add($"{type.FullName}.{field.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            $"PhotoReview.App must not declare public instance fields (CA1051); use properties instead:\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "AR02d: MainViewModel is constructed only from the Composition namespace")]
    public void NoCallToMainViewModelCtorOutsideCompositionRoot()
    {
        var ctorCall = new Regex(@"\bnew\s+MainViewModel\s*\(", RegexOptions.CultureInvariant);

        // Only the composition root itself (Composition/MainViewModelCompositionRoot.cs) may
        // construct MainViewModel directly; every other caller must resolve it through DI
        // (AppHost.BuildServices / IServiceProvider).
        var violations = RepoScan.FindLineViolations(
            ctorCall.IsMatch,
            relative => relative.Contains("/Composition/", StringComparison.Ordinal),
            "src");

        Assert.True(violations.Count == 0,
            $"MainViewModel must be constructed only inside Composition/ (via DI elsewhere):\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "AR02d: no direct `new MainWindow(` outside AppHost-based composition")]
    public void NoDirectMainWindowConstructionOutsideAppHost()
    {
        var ctorCall = new Regex(@"\bnew\s+MainWindow\s*\(", RegexOptions.CultureInvariant);

        var violations = RepoScan.FindLineViolations(
            ctorCall.IsMatch,
            // App.xaml.cs registers the DI factory `services.AddTransient<MainWindow>(sp => new MainWindow(...))`;
            // that registration is the composition root itself, not a second way to build the window.
            // This file's own doc comments/strings mention the pattern in prose; skip self-scan.
            relative => relative == "src/PhotoReview.App/App.xaml.cs"
                || relative == "tests/PhotoReview.Architecture.Tests/AppCompositionTests.cs",
            "src", "tools", "tests");

        Assert.True(violations.Count == 0,
            $"MainWindow must be resolved through AppHost.BuildServices, not constructed directly:\n{string.Join("\n", violations)}");
    }
}
