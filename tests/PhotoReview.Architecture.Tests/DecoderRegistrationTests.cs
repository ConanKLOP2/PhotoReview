using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR01: locks the boundary between "backend offered in Settings" and "backend actually wired up
/// in the App's DI graph". Before AR01, ImageDecoderFactory used Type.GetType/Activator.CreateInstance
/// to opportunistically pick up PhotoReview.Imaging.TurboJpeg, which silently produced a WPF fallback
/// in release because App.csproj never referenced that assembly.
/// </summary>
public sealed class DecoderRegistrationTests
{
    [Fact(DisplayName = "AR01: App.ConfigureServices registers every DecoderBackend offered in Settings")]
    public void Release_RegistersEveryBackendOfferedInSettings()
    {
        var services = new ServiceCollection();
        PhotoReview.App.App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IImageDecoderFactory>();

        var unregistered = Enum.GetValues<DecoderBackend>()
            .Where(backend => !factory.IsRegistered(backend))
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            $"DecoderBackend values not registered in the App's IImageDecoderFactory: {string.Join(", ", unregistered)}");
    }

    [Fact(DisplayName = "AR01: ImageDecoderFactory no longer resolves backends via reflection")]
    public void ImageDecoderFactory_HasNoReflectionLoading()
    {
        var repoRoot = FindRepoRoot();
        var imagingDir = Path.Combine(repoRoot, "src", "PhotoReview.Imaging");
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(imagingDir, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Type.GetType(", StringComparison.Ordinal) ||
                    lines[i].Contains("Activator.CreateInstance(", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"PhotoReview.Imaging must not resolve decoder backends via reflection:\n{string.Join("\n", violations)}");
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
