using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR03c: startup and coordinator code must go through <c>IDialogService</c> rather than calling
/// <c>MessageBox.Show</c> directly. Only the WPF dialog service implementation and window code-behind
/// (view layer) are allowed to call it.
/// </summary>
public sealed class DialogBoundaryTests
{
    [Fact(DisplayName = "Rule 9: MessageBox.Show( is only called from Services/WpfDialogService.cs and *Window.xaml.cs")]
    [Trait("Category", "Architecture")]
    public void MessageBoxShow_OnlyCalledFrom_WpfDialogServiceOrWindowCodeBehind()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (var file in csFiles)
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;

            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var isAllowed = relativePath.EndsWith("Services/WpfDialogService.cs", StringComparison.Ordinal)
                || relativePath.EndsWith("Window.xaml.cs", StringComparison.Ordinal);
            if (isAllowed) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("MessageBox.Show(", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"MessageBox.Show( called outside the allowed dialog boundary; route through IDialogService instead:\n{string.Join("\n", violations)}");
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
