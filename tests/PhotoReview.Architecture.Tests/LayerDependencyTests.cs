using System;
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
                "PhotoReview.Platform.Windows",
                "PresentationFramework",
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
                "PhotoReview.App")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Platform has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

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

    [Fact(DisplayName = "Rule 7: Benchmarking does not depend on App or UI frameworks beyond WPF imaging types")]
    public void Benchmarking_DoesNotDependOn_App()
    {
        var types = Types.InAssembly(typeof(PhotoReview.Benchmarking.BenchmarkEngine).Assembly);

        var result = types
            .ShouldNot()
            .HaveDependencyOnAny("PhotoReview.App", "System.Windows.Forms")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Benchmarking has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
    }

    [Fact(DisplayName = "Rule 6: ViewModels do not depend on System.Windows (K-2)")]
    [Trait("Category", "Architecture")]
    public void ViewModels_DoNotDependOn_SystemWindows()
    {
        // K-2 constraint: Will be enabled after T46a introduces PhotoReview.App.ViewModels
        var appTypes = Types.InAssembly(typeof(PhotoReview.App.App).Assembly);
        var vmTypes = appTypes.That().ResideInNamespace("PhotoReview.App.ViewModels");

        // When ViewModels namespace exists, verify it doesn't depend on System.Windows
        var typesList = vmTypes.GetTypes();
        if (typesList.Any())
        {
            var result = vmTypes
                .ShouldNot()
                .HaveDependencyOn("System.Windows")
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"ViewModels have forbidden System.Windows dependency: {string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>())}");
        }
    }
}
