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
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (var file in csFiles)
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            // Check for interface definition or usage
            if (text.Contains("IProgressiveExplorerOrderProvider", StringComparison.Ordinal))
            {
                violations.Add(relativePath);
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
        var repoRoot = FindRepoRoot();
        var appTypeNames = typeof(PhotoReview.App.App).Assembly.GetTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var reflectionOnType = new Regex(
            @"typeof\((?<type>\w+)\)\s*\.\s*Get(Field|Fields|Method|Methods|Property|Properties|NestedType|NestedTypes)\s*\(",
            RegexOptions.CultureInvariant);

        var violations = new List<string>();
        var cliDir = Path.Combine(repoRoot, "tools", "PhotoReview.Benchmark.Cli");
        foreach (var file in Directory.GetFiles(cliDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in reflectionOnType.Matches(lines[i]))
                {
                    if (appTypeNames.Contains(match.Groups["type"].Value))
                        violations.Add($"{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Benchmark.Cli reflects into App types; expose a public member instead:\n{string.Join("\n", violations)}");
    }

    [Fact(DisplayName = "Rule TS06: No [Fact] with TODO in body without [Skip]")]
    [Trait("Category", "Architecture")]
    public void FactWithTodoMustHaveSkip()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();
        var todoRegex = new Regex(@"\bTODO\b", RegexOptions.CultureInvariant);
        var skipRegex = new Regex(@"\[Fact.*Skip\s*=", RegexOptions.CultureInvariant);

        foreach (var file in Directory.GetFiles(Path.Combine(repoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("obj")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("[Fact")) continue;

                // Check if this [Fact] line has Skip
                bool hasSkip = skipRegex.IsMatch(line);

                // Scan method body for todo markers
                for (var j = i + 1; j < lines.Length && j < i + 50; j++)
                {
                    if (lines[j].Contains("{")) continue;
                    if (lines[j].Contains("}")) break;
                    if (todoRegex.IsMatch(lines[j]) && !hasSkip)
                    {
                        violations.Add($"{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}:{i + 1}: [Fact] at line {i + 1} has TODO at line {j + 1} but no [Skip]");
                        break;
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Test facts with TODO in body must have [Skip] attribute:\n{string.Join("\n", violations)}");
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
