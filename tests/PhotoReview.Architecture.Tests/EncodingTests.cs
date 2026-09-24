namespace PhotoReview.Architecture.Tests;

using System.Text.RegularExpressions;

public class EncodingTests
{
    // Regex to match mojibake (double-encoded UTF-8): À¯ (Ã followed by byte 80-BF), etc.
    private static readonly Regex MojibakePattern = new(
        @"[Ã][-¿]|[á][»]|[â][]|[Ä][-¿]",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    public void NoMojibakeInCSharpFiles(string relativeDir)
    {
        var files = RepoScan.CsFiles(relativeDir);
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var content = RepoScan.Text(file);
            foreach (Match match in MojibakePattern.Matches(content))
            {
                var lineNum = content[..match.Index].Count(c => c == '\n') + 1;
                failures.Add($"{file}:{lineNum}: {match.Value}");
            }
        }

        Assert.Empty(failures);
    }
}
