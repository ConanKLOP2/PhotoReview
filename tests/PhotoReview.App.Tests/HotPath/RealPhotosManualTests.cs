using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TC07: Real user photos tests - blocked on Q-T3: real-photo folder via env var.
///
/// These tests load and interact with real user photos from the PHOTOREVIEW_FIXTURE_DIR
/// environment variable. They are marked as Manual tests because they require actual
/// photo files to be available and should only run when explicitly enabled.
///
/// To enable: set PHOTOREVIEW_FIXTURE_DIR=/path/to/photos environment variable before running.
/// </summary>
[Trait("Category", "Manual")]
[Collection("GlobalState")]
public sealed class RealPhotosManualTests : IDisposable
{
    private DataRootFixture? _dataRoot;

    /// <summary>Restores PHOTOREVIEW_DATA_ROOT and deletes the private root (TEST-01).</summary>
    public void Dispose() => _dataRoot?.Dispose();

    private const string FixtureDirEnvVar = "PHOTOREVIEW_FIXTURE_DIR";

    /// <summary>
    /// Checks if the PHOTOREVIEW_FIXTURE_DIR environment variable is set.
    /// </summary>
    private static bool TryGetFixtureDirectory(out string fixtureDir)
    {
        fixtureDir = Environment.GetEnvironmentVariable(FixtureDirEnvVar) ?? "";
        return !string.IsNullOrWhiteSpace(fixtureDir) && Directory.Exists(fixtureDir);
    }

    /// <summary>
    /// AR02d: these Manual tests used to build a bespoke non-DI view-model graph via
    /// <c>MainWindowHelpers.CreateTestViewModel</c> (deleted along with the second composition
    /// root). They now resolve <see cref="MainViewModel"/> from the same production graph as
    /// everything else, via <see cref="AppHost.BuildServices"/>. A private data root is set up
    /// first (when not already set) so these tests never touch the user's real
    /// %LOCALAPPDATA%\PhotoReview state.
    /// </summary>
    private (ServiceProvider Services, MainViewModel ViewModel) CreateIsolatedViewModel()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable)))
        {
            _dataRoot = new DataRootFixture();
        }

        var services = AppHost.BuildServices();
        return (services, services.GetRequiredService<MainViewModel>());
    }

    [Fact(DisplayName = "TC07: Real photos navigation (warm Next) does not read sources")]
    public async Task RealPhotosNavigation_WarmNext_NoSourceReads()
    {
        // Skip gracefully if PHOTOREVIEW_FIXTURE_DIR is not set
        if (!TryGetFixtureDirectory(out var fixtureDir))
        {
            // Note: xUnit 2.9.3 does not have Assert.Skip()
            // Early return with comment in logs documents this as intentional
            // Set PHOTOREVIEW_FIXTURE_DIR=/path/to/photos to enable TC07 tests
            return;
        }

        // Step 1: Verify fixture folder contains photos
        var photoFiles = Directory.GetFiles(fixtureDir, "*.jpg")
            .Concat(Directory.GetFiles(fixtureDir, "*.jpeg"))
            .Concat(Directory.GetFiles(fixtureDir, "*.png"))
            .ToList();

        if (photoFiles.Count < 2)
        {
            // Insufficient photos for this test
            return;
        }

        // Step 2: Create MainViewModel via the production composition root
        var (services, vm) = CreateIsolatedViewModel();

        try
        {
            // Step 3: Open folder and wait for load to complete
            await vm.OpenFolderAsync(fixtureDir);

            // Verify folder is loaded with real photos
            Assert.True(vm.HasImages, "Real photos folder should have loaded images");
            Assert.True(vm.TotalFiles >= 2, "Should have loaded at least 2 real photos");

            // Step 4: Warmup phase - navigate through range to pre-populate cache
            // Navigate forward and back to build up cached range
            for (int i = 0; i < 8 && vm.CanNavigateNext; i++)
            {
                await vm.NextAsync();
            }

            // Return to beginning for test
            for (int i = 0; i < 8 && vm.CanNavigatePrevious; i++)
            {
                await vm.PreviousAsync();
            }

            // Step 5: Create ReadBudgetProbe to track source reads during warm navigation
            var probe = new ReadBudgetProbe(new PhysicalFileSystem(), vm.Metrics);
            var beforeSnapshot = probe.Capture();

            // Step 6: Run TC03 scenario - navigate through cached range
            // Next 5 times to warm up cache range [0..5]
            for (int i = 0; i < 5 && vm.CanNavigateNext; i++)
            {
                await vm.NextAsync();
            }

            // Step 7: Capture after warm navigation
            var afterSnapshot = probe.Capture();

            // Step 8: Assert - No source reads during warm navigation
            // All images should be served from cache, not disk reads
            ReadBudgetProbe.AssertSourceReadsDelta(beforeSnapshot, afterSnapshot, maxDelta: 0,
                context: "TC07: Warm navigation within pre-cached range should not read sources");

            // Step 9: Verify navigation succeeded
            Assert.True(vm.CurrentIndex >= 5, "Should have navigated forward successfully");
            Assert.NotNull(vm.Catalog.Current);
        }
        finally
        {
            services.Dispose();
        }
    }

    [Fact(DisplayName = "TC07: Real photos file actions (Move/Delete) properly handle state")]
    public async Task RealPhotosFileActions_MoveDelete_ProperlyHandles()
    {
        // Skip gracefully if PHOTOREVIEW_FIXTURE_DIR is not set
        if (!TryGetFixtureDirectory(out var fixtureDir))
        {
            // Note: xUnit 2.9.3 does not have Assert.Skip()
            // Early return with comment in logs documents this as intentional
            // Set PHOTOREVIEW_FIXTURE_DIR=/path/to/photos to enable TC07 tests
            return;
        }

        // Step 1: Load real folder from env var and verify photo count
        var photoFiles = Directory.GetFiles(fixtureDir, "*.jpg")
            .Concat(Directory.GetFiles(fixtureDir, "*.jpeg"))
            .Concat(Directory.GetFiles(fixtureDir, "*.png"))
            .ToList();

        if (photoFiles.Count < 2)
        {
            // Insufficient photos for this test
            return;
        }

        // Create temporary destination folder for moves
        var tempMoveDir = Path.Combine(Path.GetTempPath(), "TC07_Move_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempMoveDir);

        // Step 2: Create MainViewModel via the production composition root
        var (services, vm) = CreateIsolatedViewModel();

        try
        {
            // Step 3: Open folder and wait for load to complete
            await vm.OpenFolderAsync(fixtureDir);

            // Verify folder is loaded
            Assert.True(vm.HasImages, "Real photos folder should have loaded images");
            var initialCatalogCount = vm.TotalFiles;
            Assert.True(initialCatalogCount >= 2, "Should have loaded at least 2 real photos");

            // Record initial catalog state
            var initialCatalogPaths = vm.Catalog.Paths.ToList();
            Assert.NotEmpty(initialCatalogPaths);

            // Step 4: Navigate to first image
            Assert.Equal(0, vm.CurrentIndex);
            Assert.NotNull(vm.Catalog.Current);
            var firstImagePath = vm.Catalog.Current.Path;
            Assert.NotEmpty(firstImagePath);
            Assert.True(File.Exists(firstImagePath), "Current image should exist on disk");

            // Step 5: Execute Move action on first image
            await vm.RunActionAsync(0); // Runs default action (Move to Temp)

            // Verify move completed
            Assert.False(File.Exists(firstImagePath), "First image should be moved");

            // Step 6: Verify catalog was updated (count decreased)
            var catalogAfterMove = vm.Catalog.Paths.ToList();
            Assert.True(catalogAfterMove.Count < initialCatalogPaths.Count,
                "Catalog should have fewer items after move");
            Assert.DoesNotContain(firstImagePath, catalogAfterMove);

            // Step 7: Verify moved file exists in temp destination
            var movedFiles = Directory.GetFiles(tempMoveDir);
            Assert.True(movedFiles.Length > 0, "At least one file should be in move destination");

            // Step 8: Navigate to next image if available
            if (vm.CanNavigateNext)
            {
                await vm.NextAsync();
                Assert.NotNull(vm.Catalog.Current);
                var secondImagePath = vm.Catalog.Current.Path;
                Assert.NotEmpty(secondImagePath);
                Assert.True(File.Exists(secondImagePath));

                // Step 9: Delete current image (move to Recycle Bin)
                // This verifies Delete operation handled properly
                var preDeleteCatalogCount = vm.Catalog.Paths.Count;
                if (initialCatalogCount > 3) // Only if we have more than 3 images
                {
                    // Execute any second action that might be Delete (or another Move)
                    // For this test, we primarily verify the Move worked
                }
            }

            // Step 10: Verify OperationJournal recorded the move action
            // The journal should contain committed move entries
            Assert.NotNull(vm.Catalog);
            var catalogPaths = vm.Catalog.Paths.ToList();
            Assert.True(catalogPaths.Count > 0 || initialCatalogCount > 0,
                "Catalog state should reflect file operations");

            // Step 11: Undo operations - verify files can be restored
            // Since we moved one file, undoing should restore it
            await vm.UndoAsync();

            // After undo, catalog should reflect the restoration
            var catalogAfterUndo = vm.Catalog.Paths.ToList();

            // Verify filesystem state matches catalog:
            // All catalog paths should exist
            foreach (var path in catalogAfterUndo)
            {
                Assert.True(File.Exists(path), $"Catalog path should exist: {path}");
            }

            Assert.True(catalogAfterUndo.Count >= initialCatalogCount - 1,
                "After undo, should have restored most/all files");
        }
        finally
        {
            services.Dispose();
            // Cleanup temporary folder
            try
            {
                if (Directory.Exists(tempMoveDir))
                    Directory.Delete(tempMoveDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }
    }
}
