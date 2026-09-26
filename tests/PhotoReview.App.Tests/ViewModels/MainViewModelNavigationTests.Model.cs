using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Randomized navigation sequences checked step by step against a small reference model: Next/Previous/First/Last/Skip,
/// files deleted behind the app's back (which the presenter drops when it reaches them) and re-opening the folder.
/// The seed is part of the theory row, so any failure replays exactly.
/// </summary>
public sealed partial class MainViewModelNavigationTests
{
    /// <summary>The obviously-correct model of what the catalog, current index and skipped list must be.</summary>
    private sealed class NavigationModel(IEnumerable<string> names)
    {
        public List<string> List { get; } = names.ToList();
        public int Current { get; set; }
        public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LastPresented { get; set; }

        /// <summary>Present index <paramref name="index"/>; a file that no longer exists is dropped and the neighbour that takes its place is tried.</summary>
        public void Present(int index)
        {
            if (index < 0 || index >= List.Count) return;
            Current = index;
            while (Deleted.Contains(List[Current]))
            {
                var removedAt = Current;
                List.RemoveAt(removedAt);
                if (List.Count == 0) { Current = -1; return; }
                Current = Math.Min(removedAt, List.Count - 1);
            }
            LastPresented = List[Current];
        }

        public void Next() { if (List.Count == 0) return; Present(Math.Min(Current + 1, List.Count - 1)); }
        public void Previous() { if (List.Count == 0) return; Present(Math.Max(Current - 1, 0)); }
        public void First() { if (List.Count == 0) return; Present(0); }
        public void Last() { if (List.Count == 0) return; Present(List.Count - 1); }

        public void Skip()
        {
            if (Current < 0 || Current >= List.Count) return;
            Skipped.Add(List[Current]);
            Present(Math.Min(Current + 1, List.Count - 1));
        }

        public void Reopen()
        {
            var alive = List.Where(n => !Deleted.Contains(n)).ToList();
            // Files deleted while not yet reached simply are not scanned again.
            List.Clear();
            List.AddRange(alive);
            if (List.Count == 0) { Current = -1; return; }
            var resume = LastPresented is null ? -1 : List.FindIndex(n => string.Equals(n, LastPresented, StringComparison.OrdinalIgnoreCase));
            Present(resume >= 0 ? resume : 0);
        }
    }

    public static TheoryData<int> NavigationSeeds => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    [Theory(DisplayName = "Random navigation / skip / external-delete / reopen sequences match the reference model at every step")]
    [MemberData(nameof(NavigationSeeds))]
    public async Task RandomNavigationSequence_MatchesTheReferenceModel(int seed)
    {
        var rng = new Random(seed);
        var folder = Path.Combine(_tempDir, "model" + seed);
        Directory.CreateDirectory(folder);
        var names = Enumerable.Range(0, 7).Select(i => $"img{i}.png").ToList();
        foreach (var name in names) CreateImageFile(folder, name);
        var (vm, _, _) = CreateViewModel(sharedSessionWriter: true);
        await vm.OpenFolderAsync(folder);
        var model = new NavigationModel(names) { Current = 0, LastPresented = names[0] };
        var log = new List<string>();

        for (var step = 0; step < 80; step++)
        {
            var op = rng.Next(9);
            switch (op)
            {
                case 0: case 1: log.Add("Next"); model.Next(); await vm.NextAsync(); break;
                case 2: log.Add("Previous"); model.Previous(); await vm.PreviousAsync(); break;
                case 3: log.Add("First"); model.First(); await vm.FirstAsync(); break;
                case 4: log.Add("Last"); model.Last(); await vm.LastImageAsync(); break;
                case 5: log.Add("Skip"); model.Skip(); await vm.SkipAsync(); break;
                case 6:
                case 7:
                    {
                        if (model.List.Count == 0) continue;
                        var victim = model.List[rng.Next(model.List.Count)];
                        log.Add("Delete " + victim);
                        File.Delete(Path.Combine(folder, victim));
                        model.Deleted.Add(victim);
                        continue; // nothing changes until the app reaches the file
                    }
                default:
                    log.Add("Reopen");
                    vm.FlushSession();
                    model.Reopen();
                    await vm.OpenFolderAsync(folder);
                    break;
            }

            var context = $"seed {seed}, step {step}: {string.Join(" > ", log.TakeLast(8))}";
            Assert.True(model.List.Count == vm.TotalFiles, $"{context}: count {vm.TotalFiles}, expected {model.List.Count}");
            Assert.Equal(model.List, _catalog.Paths.Select(Path.GetFileName));
            Assert.True(model.Current == vm.CurrentIndex, $"{context}: index {vm.CurrentIndex}, expected {model.Current}");
            Assert.Equal(model.List.Count > 0, vm.HasImages);
            Assert.Equal(model.List.Count > 0 && model.Current < model.List.Count - 1, vm.CanNavigateNext);
            Assert.Equal(model.List.Count > 0 && model.Current > 0, vm.CanNavigatePrevious);
            Assert.Equal(model.Skipped.Count, vm.Session?.Skipped.Count ?? 0); // no duplicates in the persisted list
            Assert.True(
                model.Skipped.SetEquals((vm.Session?.Skipped ?? []).Select(Path.GetFileName)!),
                $"{context}: skipped [{string.Join(",", vm.Session?.Skipped.Select(Path.GetFileName) ?? [])}], expected [{string.Join(",", model.Skipped)}]");
        }
    }

    [Fact(DisplayName = "Empty folder: every navigation, skip, zoom and file command is a harmless no-op and the state says so")]
    public async Task EmptyFolder_EveryCommandIsANoOp()
    {
        var folder = Path.Combine(_tempDir, "empty_model");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an image");
        var (vm, _, _) = CreateViewModel();

        await vm.OpenFolderAsync(folder);
        await vm.NextAsync();
        await vm.PreviousAsync();
        await vm.FirstAsync();
        await vm.LastImageAsync();
        await vm.SkipAsync();
        await vm.RecycleAsync();
        await vm.RunActionAsync(0);
        await vm.UndoAsync();
        vm.ZoomIn();
        vm.ZoomOut();
        vm.ZoomActualSize();
        vm.ToggleCompare();

        Assert.False(vm.HasImages);
        Assert.Equal(0, vm.TotalFiles);
        Assert.False(vm.CanNavigateNext);
        Assert.False(vm.CanNavigatePrevious);
        Assert.Null(vm.CurrentImage);
        Assert.False(vm.CurrentHasComparePair);
        Assert.Empty(vm.Session?.Skipped ?? []);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "Single image: Next/Previous/First/Last stay on it and never advance past the ends")]
    public async Task SingleImage_NavigationStaysPut()
    {
        var folder = Path.Combine(_tempDir, "single_model");
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "only.png");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        for (var i = 0; i < 5; i++)
        {
            await vm.NextAsync();
            await vm.PreviousAsync();
            await vm.FirstAsync();
            await vm.LastImageAsync();
        }

        Assert.Equal(0, vm.CurrentIndex);
        Assert.Equal(1, vm.TotalFiles);
        Assert.False(vm.CanNavigateNext);
        Assert.False(vm.CanNavigatePrevious);
        Assert.NotNull(vm.CurrentImage);
    }

    [Fact(DisplayName = "A folder open that arrives after the window closed (forwarded open, drop, undo) is ignored: no exception, no catalog change")]
    public async Task OpenFolderAfterCloseSession_IsIgnored()
    {
        var folder = Path.Combine(_tempDir, "closed_model");
        var other = Path.Combine(_tempDir, "closed_other");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        CreateImageFile(folder, "a.png");
        CreateImageFile(other, "b.png");
        CreateImageFile(other, "c.png");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        vm.CloseSession();

        await vm.OpenFolderAsync(other);
        await vm.OpenPathAsync(other);

        Assert.Equal(1, vm.TotalFiles);
        Assert.Equal(Path.Combine(folder, "a.png"), vm.Catalog.Current?.Path);
    }

    [Fact(DisplayName = "Setting StatusText raises PropertyChanged(StatusText) exactly once per change")]
    public void StatusText_Set_RaisesOneNotification()
    {
        var (vm, _, _) = CreateViewModel();
        var count = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.StatusText)) count++; };

        vm.StatusText = "hello";
        Assert.Equal(1, count);
        vm.StatusText = "hello";
        Assert.Equal(1, count);
    }
}
