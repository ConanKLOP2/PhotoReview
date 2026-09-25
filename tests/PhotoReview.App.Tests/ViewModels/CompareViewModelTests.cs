using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed class CompareViewModelTests
{
    [Fact]
    public void DefaultState_AllPropertiesEmptyOrNull_NotVisible()
    {
        var vm = new CompareViewModel();

        Assert.False(vm.IsVisible);
        Assert.Null(vm.LeftPath);
        Assert.Null(vm.RightPath);
        Assert.Null(vm.LeftImage);
        Assert.Null(vm.RightImage);
        Assert.Null(vm.SelectedPath);
        Assert.False(vm.IsLeftSelected);
        Assert.False(vm.IsRightSelected);
        Assert.Equal(string.Empty, vm.LeftSizeText);
        Assert.Equal(string.Empty, vm.RightSizeText);
        Assert.Equal(" | hash tắt", vm.HashText);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void Toggle_FlipsIsVisible()
    {
        var vm = new CompareViewModel();
        Assert.False(vm.IsVisible);

        vm.Toggle();
        Assert.True(vm.IsVisible);

        vm.Toggle();
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void Select_LeftAndRight_UpdatesSelectedPathAndFlags()
    {
        var vm = new CompareViewModel
        {
            LeftPath = @"C:\photos\img1.jpg",
            RightPath = @"C:\photos\img2.jpg"
        };

        var propChanged = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) propChanged.Add(e.PropertyName);
        };

        vm.SelectLeft();
        Assert.Equal(@"C:\photos\img1.jpg", vm.SelectedPath);
        Assert.True(vm.IsLeftSelected);
        Assert.False(vm.IsRightSelected);
        Assert.Contains(nameof(vm.SelectedPath), propChanged);
        Assert.Contains(nameof(vm.IsLeftSelected), propChanged);

        propChanged.Clear();
        vm.SelectRight();
        Assert.Equal(@"C:\photos\img2.jpg", vm.SelectedPath);
        Assert.False(vm.IsLeftSelected);
        Assert.True(vm.IsRightSelected);
        Assert.Contains(nameof(vm.SelectedPath), propChanged);
        Assert.Contains(nameof(vm.IsRightSelected), propChanged);

        vm.Select(null);
        Assert.Null(vm.SelectedPath);
        Assert.False(vm.IsLeftSelected);
        Assert.False(vm.IsRightSelected);
    }

    [Fact]
    public void Clear_ResetsAllProperties()
    {
        var vm = new CompareViewModel
        {
            IsVisible = true,
            LeftPath = @"C:\photos\img1.jpg",
            RightPath = @"C:\photos\img2.jpg",
            LeftImage = new object(),
            RightImage = new object(),
            SelectedPath = @"C:\photos\img1.jpg",
            LeftSizeText = " (1,000 B)",
            RightSizeText = " (2,000 B)",
            HashText = " | hash TRÙNG",
            StatusText = "Compare Status"
        };

        var resetProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) resetProperties.Add(e.PropertyName);
        };

        vm.Clear();

        Assert.False(vm.IsVisible);
        Assert.Null(vm.LeftPath);
        Assert.Null(vm.RightPath);
        Assert.Null(vm.LeftImage);
        Assert.Null(vm.RightImage);
        Assert.Null(vm.SelectedPath);
        Assert.False(vm.IsLeftSelected);
        Assert.False(vm.IsRightSelected);
        Assert.Equal(string.Empty, vm.LeftSizeText);
        Assert.Equal(string.Empty, vm.RightSizeText);
        Assert.Equal(" | hash tắt", vm.HashText);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Contains(nameof(vm.IsVisible), resetProperties);
        Assert.Contains(nameof(vm.LeftPath), resetProperties);
        Assert.Contains(nameof(vm.RightPath), resetProperties);
        Assert.Contains(nameof(vm.LeftImage), resetProperties);
        Assert.Contains(nameof(vm.RightImage), resetProperties);
    }

    [Fact]
    public async Task LoadAsync_LoadsPreviewsConcurrently_AndSetsProperties()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");
        var img1 = new object();
        var img2 = new object();

        var success = await vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: t => t == 1,
            loadImageAsync: p => Task.FromResult<object?>(p == pair.Item1 ? img1 : img2),
            getHashAsync: null,
            compareSizeEnabled: false,
            compareHashEnabled: false,
            currentIndex: 0,
            totalFiles: 10);

        Assert.True(success);
        Assert.True(vm.IsVisible);
        Assert.Equal(pair.Item1, vm.LeftPath);
        Assert.Equal(pair.Item2, vm.RightPath);
        Assert.Same(img1, vm.LeftImage);
        Assert.Same(img2, vm.RightImage);
        Assert.Equal(pair.Item1, vm.SelectedPath);
        Assert.True(vm.IsLeftSelected);
        Assert.False(vm.IsRightSelected);
        Assert.Equal(" | hash tắt", vm.HashText);
        Assert.Equal("1/10 | So sánh | img1.jpg ↔ img2.jpg | hash tắt | nhấn để chọn", vm.StatusText);
    }

    [Fact]
    public async Task LoadAsync_WithCompareSize_FormatsSizesCorrectly()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");

        var success = await vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: null,
            compareSizeEnabled: true,
            compareHashEnabled: false,
            currentIndex: 2,
            totalFiles: 5,
            getFileSize: p => p == pair.Item1 ? 1048576L : 2097152L);

        Assert.True(success);
        Assert.Equal(" (1,048,576 byte)", vm.LeftSizeText);
        Assert.Equal(" (2,097,152 byte)", vm.RightSizeText);
        Assert.Equal(" (1,048,576 byte) ↔  (2,097,152 byte)", vm.SizeText);
        Assert.Equal("3/5 | So sánh | img1.jpg (1,048,576 byte) ↔ img2.jpg (2,097,152 byte) | hash tắt | nhấn để chọn", vm.StatusText);
    }

    [Fact]
    public async Task LoadAsync_WithCompareHash_MatchingHashes_SetsTrung()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");

        var success = await vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: _ => Task.FromResult("abcdef123456"),
            compareSizeEnabled: false,
            compareHashEnabled: true,
            currentIndex: 0,
            totalFiles: 1);

        Assert.True(success);
        Assert.Equal(" | hash TRÙNG", vm.HashText);
        Assert.Equal("1/1 | So sánh | img1.jpg ↔ img2.jpg | hash TRÙNG | nhấn để chọn", vm.StatusText);
    }

    [Fact]
    public async Task LoadAsync_WithCompareHash_DifferentHashes_SetsKhac()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");

        var success = await vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: p => Task.FromResult(p == pair.Item1 ? "hashA" : "hashB"),
            compareSizeEnabled: false,
            compareHashEnabled: true,
            currentIndex: 0,
            totalFiles: 1);

        Assert.True(success);
        Assert.Equal(" | hash KHÁC", vm.HashText);
        Assert.Equal("1/1 | So sánh | img1.jpg ↔ img2.jpg | hash KHÁC | nhấn để chọn", vm.StatusText);
    }

    [Fact]
    public async Task LoadAsync_TokenMismatch_AbortsBeforeImageSet()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");
        var currentToken = 1L;

        var loadTcs = new TaskCompletionSource<object?>();

        var loadTask = vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: t => t == currentToken,
            loadImageAsync: _ => loadTcs.Task,
            getHashAsync: null);

        // Before images finish loading, user navigates to token 2
        currentToken = 2;
        loadTcs.SetResult(new object());

        var success = await loadTask;

        Assert.False(success);
        Assert.Null(vm.LeftImage);
        Assert.Null(vm.RightImage);
    }

    [Fact]
    public async Task LoadAsync_TokenMismatch_AbortsBeforeHashSet()
    {
        var vm = new CompareViewModel();
        var pair = (@"C:\photos\img1.jpg", @"C:\photos\img2.jpg");
        var currentToken = 1L;

        var hashTcs = new TaskCompletionSource<string>();

        var loadTask = vm.LoadAsync(
            pair,
            token: 1,
            isTokenCurrent: t => t == currentToken,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: _ => hashTcs.Task,
            compareSizeEnabled: false,
            compareHashEnabled: true);

        // Image loaded, now waiting for hash; user navigates to token 2
        currentToken = 2;
        hashTcs.SetResult("hashVal");

        var success = await loadTask;

        Assert.False(success);
        Assert.Equal(" | hash tắt", vm.HashText);
    }
}
