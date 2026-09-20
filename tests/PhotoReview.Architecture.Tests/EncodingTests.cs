namespace PhotoReview.Architecture.Tests;

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

public class EncodingTests
{
    // Regex to match mojibake (double-encoded UTF-8): À¯ (Ã followed by byte 80-BF), etc.
    private static readonly Regex MojibakePattern = new(
        @"[Ã][-¿]|[á][»]|[â][]|[Ä][-¿]",
        RegexOptions.Compiled);

    [Fact]
    public void NoMojibakeInTestFiles()
    {
        var testRoot = RepositoryRoot("tests");
        var mojibakeFiles = ScanDirectory(testRoot, "*.cs");
        Assert.Empty(mojibakeFiles);
    }

    [Fact]
    public void NoMojibakeInSourceFiles()
    {
        var srcRoot = RepositoryRoot("src");
        var mojibakeFiles = ScanDirectory(srcRoot, "*.cs");
        Assert.Empty(mojibakeFiles);
    }

    private List<string> ScanDirectory(string directory, string pattern)
    {
        var failures = new List<string>();
        if (!Directory.Exists(directory)) return failures;

        foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            if (file.Contains("/obj/") || file.Contains("/bin/")) continue;

            try
            {
                var content = File.ReadAllText(file);
                var matches = MojibakePattern.Matches(content);
                if (matches.Count > 0)
                {
                    var lines = content.Split('\n');
                    foreach (Match match in matches)
                    {
                        var lineNum = content[..match.Index].Count(c => c == '\n') + 1;
                        failures.Add($"{file}:{lineNum}: {match.Value}");
                    }
                }
            }
            catch (IOException) { }
        }
        return failures;
    }

    private string RepositoryRoot(string subdir)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PhotoReview.slnx")))
        {
            current = current.Parent;
        }
        return current == null ? "" : Path.Combine(current.FullName, subdir);
    }
}
