using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

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
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var ctorCall = new Regex(@"\bnew\s+MainViewModel\s*\(", RegexOptions.CultureInvariant);

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (relativePath.Contains("/obj/", StringComparison.Ordinal)) continue;
            // Only the composition root itself (Composition/MainViewModelCompositionRoot.cs) may
            // construct MainViewModel directly; every other caller must resolve it through DI
            // (AppHost.BuildServices / IServiceProvider).
            if (relativePath.Contains("/Composition/", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (ctorCall.IsMatch(lines[i]))
                    violations.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"MainViewModel must be constructed only inside Composition/ (via DI elsewhere):\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "AR02d: no direct `new MainWindow(` outside AppHost-based composition")]
    public void NoDirectMainWindowConstructionOutsideAppHost()
    {
        var repoRoot = FindRepoRoot();
        var ctorCall = new Regex(@"\bnew\s+MainWindow\s*\(", RegexOptions.CultureInvariant);

        var violations = new List<string>();
        foreach (var searchDir in new[] { "src", "tools", "tests" })
        {
            var dir = Path.Combine(repoRoot, searchDir);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (relativePath.Contains("/obj/", StringComparison.Ordinal)) continue;
                // App.xaml.cs registers the DI factory `services.AddTransient<MainWindow>(sp => new MainWindow(...))`;
                // that registration is the composition root itself, not a second way to build the window.
                if (relativePath == "src/PhotoReview.App/App.xaml.cs") continue;
                // This file's own doc comments/strings mention the pattern in prose; skip self-scan.
                if (relativePath == "tests/PhotoReview.Architecture.Tests/AppCompositionTests.cs") continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (ctorCall.IsMatch(lines[i]))
                        violations.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"MainWindow must be resolved through AppHost.BuildServices, not constructed directly:\n{string.Join("\n", violations)}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhotoReview.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root (looking for PhotoReview.slnx or .git)");
    }
}
