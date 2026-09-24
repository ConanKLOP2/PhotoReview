using System.IO;
using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>I18N: a live language switch re-renders the code-built title and folder line (ADR 0006).</summary>
[Collection("GlobalState")]
public sealed class MainViewModelLanguageSwitchTests
{
    [Fact]
    public void RefreshLocalizedText_AfterSwitchToEnglish_RerendersFolderText()
    {
        using var host = new MainViewModelAdvancedTests();
        var (vm, _) = host.CreateViewModel();
        var folder = Path.Combine(Path.GetTempPath(), "lang_switch");
        IFolderLoadSink sink = vm;
        sink.OnCatalogReady(folder, 3);
        sink.OnOrderApplied(3, 0, currentKept: false);
        Assert.Equal($"{folder}  (3 ảnh) · Explorer", vm.FolderText);

        using (TestLocalization.Use(TestLocalization.English))
        {
            vm.RefreshLocalizedText();

            Assert.DoesNotContain("ảnh", vm.FolderText, StringComparison.Ordinal);
            Assert.StartsWith(folder, vm.FolderText, StringComparison.Ordinal);
            Assert.True(vm.IsExplorerOrderApplied);
        }

        vm.RefreshLocalizedText();
        Assert.Equal($"{folder}  (3 ảnh) · Explorer", vm.FolderText);
    }
}
