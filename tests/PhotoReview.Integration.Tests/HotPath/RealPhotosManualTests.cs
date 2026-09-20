using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.Integration.Tests.HotPath;

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
public sealed class RealPhotosManualTests
{
    private const string FixtureDirEnvVar = "PHOTOREVIEW_FIXTURE_DIR";

    /// <summary>
    /// Checks if the PHOTOREVIEW_FIXTURE_DIR environment variable is set.
    /// </summary>
    private bool TryGetFixtureDirectory(out string fixtureDir)
    {
        fixtureDir = Environment.GetEnvironmentVariable(FixtureDirEnvVar) ?? "";
        return !string.IsNullOrWhiteSpace(fixtureDir) && Directory.Exists(fixtureDir);
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

        // Step 2: Create MainViewModel and load real folder
        // TODO: Implement MainViewModel creation from real folder
        // TODO: Run TC03 scenario (warmup + navigate)
        // TODO: Assert source reads = 0

        throw new NotImplementedException(
            "TC07.RealPhotosNavigation_WarmNext_NoSourceReads: Implementation pending. " +
            $"Fixture folder detected: {fixtureDir} ({photoFiles.Count} photos)");
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

        // Step 1: Load real folder from env var
        var photoFiles = Directory.GetFiles(fixtureDir, "*.jpg")
            .Concat(Directory.GetFiles(fixtureDir, "*.jpeg"))
            .Concat(Directory.GetFiles(fixtureDir, "*.png"))
            .ToList();

        if (photoFiles.Count < 2)
        {
            // Insufficient photos for this test
            return;
        }

        // Step 2: Create MainViewModel and catalog
        // TODO: Implement real folder loading
        // TODO: Move/Delete real images
        // TODO: Verify state on disk
        // TODO: Verify catalog correct

        throw new NotImplementedException(
            "TC07.RealPhotosFileActions_MoveDelete_ProperlyHandles: Implementation pending. " +
            $"Fixture folder detected: {fixtureDir} ({photoFiles.Count} photos)");
    }
}
