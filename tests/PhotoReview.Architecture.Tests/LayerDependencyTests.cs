using System.Linq;
using NetArchTest.Rules;
using Xunit;

namespace PhotoReview.Architecture.Tests;

public sealed class LayerDependencyTests
{
    [Fact(DisplayName = "Rule 1: Core does not depend on UI/WPF/VB frameworks or other application layers")]
    public void Core_DoesNotDependOn_ForbiddenFrameworkAssemblies_OrOtherLayers()
    {
        var coreTypes = Types.InAssembly(typeof(PhotoReview.Core.AppPaths).Assembly);

        var result = coreTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                "System.Windows",
                "PresentationCore",
                "WindowsBase",
                "Microsoft.VisualBasic",
                "PhotoReview.Imaging",
                "PhotoReview.Imaging.Raw",
                "PhotoReview.Platform.Windows",
                "PhotoReview.App")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Core has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    [Fact(DisplayName = "Rule 2: Imaging does not depend on App, Platform, PresentationFramework, or WinForms")]
    public void Imaging_DoesNotDependOn_App_Platform_PresentationFramework_OrWinForms()
    {
        var imagingTypes = Types.InAssembly(typeof(PhotoReview.Imaging.Decoding.IDecodedImage).Assembly);

        var result = imagingTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                "PhotoReview.App",
                "PhotoReview.Imaging.Raw",
                "PhotoReview.Imaging.Wpf",
                "PhotoReview.Platform.Windows",
                "PresentationFramework",
                "PresentationCore",
                "WindowsBase",
                "System.Windows",
                "System.Windows.Forms")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Imaging has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    [Fact(DisplayName = "Rule 4: Platform does not depend on Imaging or App")]
    public void Platform_DoesNotDependOn_Imaging_Or_App()
    {
        var platformTypes = Types.InAssembly(typeof(PhotoReview.Platform.Windows.WindowsRecycleBin).Assembly);

        var result = platformTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                "PhotoReview.Imaging",
                "PhotoReview.Imaging.Raw",
                "PhotoReview.App")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Platform has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    [Fact(DisplayName = "Rule 9: Imaging.Raw depends only on Imaging and Core")]
    public void ImagingRaw_DoesNotDependOn_ForbiddenLayers()
    {
        var rawAssembly = typeof(PhotoReview.Imaging.Raw.RawFormat).Assembly;

        var result = Types.InAssembly(rawAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "PhotoReview.App",
                "PhotoReview.Platform.Windows",
                "PhotoReview.Benchmarking",
                "PhotoReview.Imaging.LibRaw",
                "PhotoReview.Imaging.Wpf",
                "PresentationFramework",
                "PresentationCore",
                "WindowsBase",
                "System.Windows")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Imaging.Raw has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
        Assert.Empty(UnexpectedPhotoReviewReferences(rawAssembly, "PhotoReview.Core", "PhotoReview.Imaging"));
    }

    [Fact(DisplayName = "Rule 10: Imaging.LibRaw depends only on Imaging and Core")]
    public void ImagingLibRaw_DoesNotDependOn_ForbiddenLayers()
    {
        var libRawAssembly = typeof(PhotoReview.Imaging.LibRaw.LibRawAvailability).Assembly;

        var result = Types.InAssembly(libRawAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "PhotoReview.App",
                "PhotoReview.Platform.Windows",
                "PhotoReview.Benchmarking",
                "PhotoReview.Imaging.Raw",
                "PhotoReview.Imaging.Wpf",
                "PresentationFramework",
                "PresentationCore",
                "WindowsBase",
                "System.Windows")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Imaging.LibRaw has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
        Assert.Empty(UnexpectedPhotoReviewReferences(libRawAssembly, "PhotoReview.Core", "PhotoReview.Imaging"));
    }

    [Fact(DisplayName = "Reference allow-list check reports a forbidden assembly reference")]
    public void UnexpectedPhotoReviewReferences_AssemblyWithOtherReferences_ReportsThem()
    {
        // The App assembly references far more than Core: the helper behind rules 9/10 must name them, so adding a
        // forbidden project reference to Imaging.Raw / Imaging.LibRaw makes those rules fail.
        var unexpected = UnexpectedPhotoReviewReferences(typeof(PhotoReview.App.App).Assembly, "PhotoReview.Core");

        Assert.Contains("PhotoReview.Imaging", unexpected);
    }

    /// <summary>Names of the PhotoReview.* assemblies <paramref name="assembly"/> references that are not in <paramref name="allowed"/>.</summary>
    private static string[] UnexpectedPhotoReviewReferences(System.Reflection.Assembly assembly, params string[] allowed) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("PhotoReview.", StringComparison.Ordinal) && !allowed.Contains(name, StringComparer.Ordinal))
            .ToArray();

    [Fact(DisplayName = "Rule 8: Platform.Windows does not reference WPF presentation assemblies (AR03b: builds without UseWPF)")]
    public void PlatformWindows_DoesNotDependOn_WpfPresentationAssemblies()
    {
        var platformTypes = Types.InAssembly(typeof(PhotoReview.Platform.Windows.WindowsRecycleBin).Assembly);

        var result = platformTypes
            .ShouldNot()
            .HaveDependencyOnAny(
                "PresentationFramework",
                "PresentationCore",
                "WindowsBase")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Platform.Windows has forbidden WPF dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    [Fact(DisplayName = "Rule 7: Benchmarking does not depend on App, the WPF bridge or any WPF/WinForms assembly (WP-06)")]
    public void Benchmarking_DoesNotDependOn_App()
    {
        var types = Types.InAssembly(typeof(PhotoReview.Benchmarking.BenchmarkEngine).Assembly);

        var result = types
            .ShouldNot()
            .HaveDependencyOnAny("PhotoReview.App", "PhotoReview.Imaging.Wpf", "System.Windows.Forms", "System.Windows",
                "PresentationFramework", "PresentationCore", "WindowsBase")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Benchmarking has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    /// <summary>The WPF framework assemblies. None of the Imaging family may reference them (WP-06, L-IMG).</summary>
    private static readonly string[] WpfAssemblyNames =
        ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "UIAutomationTypes", "UIAutomationProvider", "ReachFramework"];

    public static TheoryData<string> WpfFreeAssemblies() => new()
    {
        typeof(PhotoReview.Imaging.Decoding.IDecodedImage).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.Raw.RawFormat).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.LibRaw.LibRawAvailability).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.TurboJpeg.TurboJpegDecoder).Assembly.GetName().Name!,
        typeof(PhotoReview.Benchmarking.BenchmarkEngine).Assembly.GetName().Name!,
    };

    [Theory(DisplayName = "Rule 11 (L-IMG): Imaging, Imaging.Raw, Imaging.LibRaw, Imaging.TurboJpeg and Benchmarking reference no WPF assembly")]
    [MemberData(nameof(WpfFreeAssemblies))]
    public void ImagingFamily_ReferencesNoWpfAssembly(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? System.Reflection.Assembly.Load(assemblyName);

        var wpf = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => WpfAssemblyNames.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(wpf.Length == 0, $"{assemblyName} references WPF assemblies: {string.Join(", ", wpf)}");
    }

    [Theory(DisplayName = "Rule 11 (L-IMG): the project files of the Imaging family do not set UseWPF")]
    [InlineData("src/PhotoReview.Imaging/PhotoReview.Imaging.csproj")]
    [InlineData("src/PhotoReview.Imaging.Raw/PhotoReview.Imaging.Raw.csproj")]
    [InlineData("src/PhotoReview.Imaging.LibRaw/PhotoReview.Imaging.LibRaw.csproj")]
    [InlineData("src/PhotoReview.Imaging.TurboJpeg/PhotoReview.Imaging.TurboJpeg.csproj")]
    [InlineData("src/PhotoReview.Benchmarking/PhotoReview.Benchmarking.csproj")]
    public void ImagingFamily_ProjectFiles_DoNotEnableWpf(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoScan.Root, relativePath));

        Assert.DoesNotContain("UseWPF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWindowsForms", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Rule 12 (L-IMG): the WPF bridge is the only Imaging assembly with UseWPF and references only Core and Imaging")]
    public void ImagingWpf_IsTheOnlyWpfImagingAssembly_AndDependsOnlyOnCoreAndImaging()
    {
        var wpfBridge = typeof(PhotoReview.Imaging.Wpf.WpfBitmapSourceCodec).Assembly;

        Assert.Equal("PhotoReview.Imaging.Wpf", wpfBridge.GetName().Name);
        Assert.Contains(wpfBridge.GetReferencedAssemblies(), a => a.Name == "PresentationCore");
        Assert.Empty(UnexpectedPhotoReviewReferences(wpfBridge, "PhotoReview.Core", "PhotoReview.Imaging"));
        Assert.Contains("UseWPF", File.ReadAllText(Path.Combine(RepoScan.Root, "src/PhotoReview.Imaging.Wpf/PhotoReview.Imaging.Wpf.csproj")));
    }

    // WP-09 (NO-WPF-EXEC-PLAN L-SHARED): Rule 6 (K-2, "ViewModels do not use the System.Windows namespace") is replaced by a rule per
    // assembly. The type-level check was both too weak (System.Windows.Input.ICommand is not WPF) and too narrow (only one namespace);
    // L-SHARED (ShellRulesTests.AppShared_DoesNotReferenceWpfAppOrShell) forbids App.Shared any WPF assembly reference. This rule pins
    // WHERE the WPF-free layers live, so none of them can drift back into the WPF assembly.
    [Fact(DisplayName = "Rule 6 -> L-SHARED: ViewModels, Input and the coordinators live in App.Shared; only FullscreenWindowPlacer stays in App")]
    [Trait("Category", "Architecture")]
    public void PureUiLayers_LiveInAppShared_NotInTheWpfAssembly()
    {
        var wpfAppTypes = typeof(PhotoReview.App.App).Assembly.GetTypes()
            .Where(t => t.Namespace is "PhotoReview.App.ViewModels" or "PhotoReview.App.Input" or "PhotoReview.App.Coordinators"
                or "PhotoReview.App.Composition")
            .Where(t => !t.Name.StartsWith('<'))
            .Select(t => $"{t.Namespace}.{t.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The only WPF-bound members of those namespaces: the fullscreen placement adapter (Win32 interop over a WPF Window)
        // and AppHost (the WPF app's composition entry that calls App.ConfigureServices).
        Assert.Equal(
            ["PhotoReview.App.Composition.AppHost", "PhotoReview.App.Coordinators.FullscreenWindowPlacer"],
            wpfAppTypes);

        var sharedAssembly = typeof(PhotoReview.App.Input.PointD).Assembly;
        Assert.Equal("PhotoReview.App.Shared", sharedAssembly.GetName().Name);
        Assert.Contains(sharedAssembly.GetTypes(), t => t.FullName == "PhotoReview.App.ViewModels.MainViewModel");
        Assert.Contains(sharedAssembly.GetTypes(), t => t.FullName == "PhotoReview.App.Composition.MainViewModelCompositionRoot");
    }
}
