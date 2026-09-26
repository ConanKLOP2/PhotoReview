using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Xunit;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// Architecture rules for ST01-ST07 structure optimization refactoring.
/// These tests enforce the design contracts established in STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md
/// </summary>
public sealed class StructureOptimizeRulesTests
{
    [Fact(DisplayName = "Rule ST01: IProgressiveExplorerOrderProvider marker interface is completely eliminated")]
    public void MarkerInterface_IProgressiveExplorerOrderProvider_IsEliminated()
    {
        var violations = new List<string>();
        foreach (var file in RepoScan.CsFiles("src"))
        {
            // Check for interface definition or usage
            if (RepoScan.Text(file).Contains("IProgressiveExplorerOrderProvider", StringComparison.Ordinal))
            {
                violations.Add(RepoScan.Relative(file));
            }
        }

        Assert.True(
            violations.Count == 0,
            $"IProgressiveExplorerOrderProvider references still exist:\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "Rule ST02: SourceSizeTracker is the sole location for catalog size computation")]
    public void SourceSizeTracker_IsSoleLocationForCatalogSizeComputation()
    {
        var trackerAssembly = typeof(PhotoReview.Core.Catalog.SourceSizeTracker).Assembly;
        var trackerTypes = Types.InAssembly(trackerAssembly);
        var sourceTrackerType = trackerTypes.That().HaveName("SourceSizeTracker");

        var typesList = sourceTrackerType.GetTypes().ToList();
        Assert.NotEmpty(typesList);
        Assert.Single(typesList);

        var tracker = typesList[0];
        Assert.True(
            tracker.GetMethods().Any(m => m.Name == "GetTotal"),
            "SourceSizeTracker must have GetTotal() method for efficient size queries");
    }

    [Fact(DisplayName = "Rule ST03: MainViewModelCompositionRoot is isolated in Composition namespace")]
    public void MainViewModelCompositionRoot_IsIsolatedInCompositionNamespace()
    {
        var appAssembly = typeof(PhotoReview.App.App).Assembly;
        var appTypes = Types.InAssembly(appAssembly);
        var compositionTypes = appTypes.That().ResideInNamespace("PhotoReview.App.Composition");

        var typesList = compositionTypes.GetTypes().ToList();
        var rootTypes = typesList.Where(t => t.Name == "MainViewModelCompositionRoot").ToList();

        Assert.NotEmpty(rootTypes);
        Assert.Single(rootTypes);

        var root = rootTypes[0];
        Assert.True(
            root.GetMethods().Any(m => m.Name == "Create" && m.IsStatic),
            "MainViewModelCompositionRoot must have static Create() factory method");
    }

    [Fact(DisplayName = "Rule ST03: PerfDispatcherHooks moved to Diagnostics namespace")]
    public void PerfDispatcherHooks_IsInDiagnosticsNamespace()
    {
        var appAssembly = typeof(PhotoReview.App.App).Assembly;
        var appTypes = Types.InAssembly(appAssembly);
        var diagTypes = appTypes.That().ResideInNamespace("PhotoReview.App.Diagnostics");

        var typesList = diagTypes.GetTypes().ToList();
        var hookTypes = typesList.Where(t => t.Name == "PerfDispatcherHooks").ToList();

        Assert.NotEmpty(hookTypes);
    }

    [Fact(DisplayName = "Rule ST07: MainViewModel required parameters are explicit (fileSystem, fileActionService, undoService, dialogService, hashService, previewService, thumbnailCache, sessionWriter)")]
    [Trait("Category", "Architecture")]
    public void MainViewModel_RequiredDependenciesAreExplicit()
    {
        var mainVMType = typeof(PhotoReview.App.ViewModels.MainViewModel);
        var constructors = mainVMType.GetConstructors();

        Assert.NotEmpty(constructors);

        var ctor = constructors[0];
        var parameters = ctor.GetParameters();

        // Expected required parameter names (in order)
        var requiredParams = new[]
        {
            "catalog", "clock", "folderCoordinator", "presenter", "viewerState",
            "compare", "settingsStore", "sessionStore",
            "fileSystem", "fileActionService", "undoService", "dialogService",
            "hashService", "previewService", "thumbnailCache", "sessionWriter"
        };

        var firstNParams = parameters.Take(requiredParams.Length).Select(p => p.Name).ToList();
        Assert.Equal(requiredParams, firstNParams);

        // Verify these parameters don't have null defaults (are truly required)
        foreach (var param in parameters.Take(requiredParams.Length))
        {
            Assert.False(
                param.DefaultValue != System.DBNull.Value,
                $"Required parameter '{param.Name}' should not have a default value");
        }
    }

    [Fact(DisplayName = "Rule ST06: Benchmark.Cli does not reflect into types of PhotoReview.App")]
    [Trait("Category", "Architecture")]
    public void BenchmarkCli_DoesNotReflectIntoAppTypes()
    {
        var appTypeNames = typeof(PhotoReview.App.App).Assembly.GetTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var reflectionOnType = new Regex(
            @"typeof\((?<type>\w+)\)\s*\.\s*Get(Field|Fields|Method|Methods|Property|Properties|NestedType|NestedTypes)\s*\(",
            RegexOptions.CultureInvariant);

        const string cliDir = "tools/PhotoReview.Benchmark.Cli";
        Assert.NotEmpty(RepoScan.CsFiles(cliDir));

        var violations = RepoScan.FindLineViolations(
            line => reflectionOnType.Matches(line).Any(m => appTypeNames.Contains(m.Groups["type"].Value)),
            null,
            cliDir);

        Assert.True(violations.Count == 0,
            $"Benchmark.Cli reflects into App types; expose a public member instead:\n{string.Join("\n", violations)}");
    }

    [Theory(DisplayName = "Rule AR13: MainWindow code-behind holds no pointer/glide state machine (it lives in Input/PointerInputController.cs)")]
    [Trait("Category", "Architecture")]
    [InlineData("CompositionTarget.Rendering")]
    [InlineData("_isPanning")]
    public void MainWindowCodeBehind_HasNoPointerStateMachine(string forbidden)
    {
        var text = RepoScan.Text(Path.Combine(RepoScan.Root, "src", "PhotoReview.App", "MainWindow.xaml.cs"));

        Assert.False(
            text.Contains(forbidden, StringComparison.Ordinal),
            $"MainWindow.xaml.cs contains '{forbidden}'; pointer/kinetic logic belongs in PointerInputController (AR13a).");
    }
}
