using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Services;
using PhotoReview.Core.Localization;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Context-menu "Copy File Name" / "Copy Full Pathname": clipboard content, enable state and status line.</summary>
public sealed partial class MainViewModelAdvancedTests
{
    private sealed class FakeClipboard(bool succeed = true) : IClipboardService
    {
        public string? Text { get; private set; }
        public int Calls { get; private set; }

        public bool TrySetText(string text)
        {
            Calls++;
            if (succeed) Text = text;
            return succeed;
        }
    }

    [Fact]
    public async Task CopyFileName_PutsNameWithExtensionOnClipboardAndConfirmsOnStatusLine()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("copy_name");
        var clip = new FakeClipboard();
        vm.Clipboard = clip;

        vm.CopyFileName();

        Assert.Equal("1.png", clip.Text);
        Assert.Equal(Tr.StatusCopiedFileName("1.png"), vm.StatusText);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task CopyFullPathname_PutsFullPathOnClipboardAndConfirmsOnStatusLine()
    {
        var (vm, folder) = await OpenSingleImageFolderAsync("copy_path");
        var clip = new FakeClipboard();
        vm.Clipboard = clip;

        vm.CopyFullPathname();

        var expected = Path.Combine(folder, "1.png");
        Assert.Equal(expected, clip.Text);
        Assert.Equal(Tr.StatusCopiedFullPath(expected), vm.StatusText);
    }

    [Fact]
    public async Task Copy_ClipboardBusy_ShowsFailureStatusInsteadOfConfirmation()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("copy_busy");
        var clip = new FakeClipboard(succeed: false);
        vm.Clipboard = clip;

        vm.CopyFileName();
        Assert.Equal(Tr.StatusClipboardCopyFailed, vm.StatusText);
        vm.CopyFullPathname();
        Assert.Equal(Tr.StatusClipboardCopyFailed, vm.StatusText);
        Assert.Equal(2, clip.Calls);
    }


    [Fact]
    public async Task Copy_WithoutAnInjectedClipboard_ReportsUnavailableInsteadOfTouchingTheRealClipboard()
    {
        // WP-09: the view model has no hidden dependency on the WPF clipboard any more; a host that injects none gets "unavailable".
        var (vm, _) = await OpenSingleImageFolderAsync("copy_unavailable");

        vm.CopyFileName();
        Assert.Equal(Tr.StatusClipboardCopyFailed, vm.StatusText);
        vm.CopyFullPathname();
        Assert.Equal(Tr.StatusClipboardCopyFailed, vm.StatusText);
        var injected = vm.Clipboard;
        Assert.False(injected.TrySetText("x"));
    }

    [Fact]
    public void Copy_NoImageOpen_IsDisabledAndDoesNotTouchClipboard()
    {
        var (vm, _) = CreateViewModel();
        var clip = new FakeClipboard();
        vm.Clipboard = clip;

        Assert.False(vm.CanCopyCurrentFilePath);
        vm.CopyFileName();
        vm.CopyFullPathname();

        Assert.Equal(0, clip.Calls);
    }

    [Fact]
    public async Task CanCopyCurrentFilePath_TrueOnceAnImageIsOpen()
    {
        var (vm, _) = await OpenSingleImageFolderAsync("copy_can");
        Assert.True(vm.CanCopyCurrentFilePath);
    }
}
