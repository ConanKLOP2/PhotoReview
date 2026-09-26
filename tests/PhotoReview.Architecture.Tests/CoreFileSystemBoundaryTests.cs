using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR11a (docs/refactoring/arch-review/AR11-settings-persistence.md): <c>PhotoReview.Core</c> reads/writes disk
/// only through <see cref="PhotoReview.Core.Abstractions.IFileSystem"/> (INV-11) -- direct <c>File.</c>/
/// <c>Directory.</c>/<c>FileInfo.</c>/<c>DirectoryInfo.</c> calls bypass the fakes tests use and any counting
/// wrapper (e.g. <c>CountingFileSystem</c>) production wires around it. This guard was added when
/// <c>AppSettings.Load(path)</c> -- the one static, <see cref="System.IO.File"/>-based reader in Core -- was
/// deleted; the handful of remaining direct calls all pre-date this task and are named individually in
/// <see cref="AllowlistFile"/> (a shrinking allowlist, same shape as <c>TestQualityRulesTests</c>'s) instead of
/// being silently fixed here, which would balloon AR11a into an unrelated Core-wide IFileSystem migration.
/// </summary>
public sealed class CoreFileSystemBoundaryTests
{
    private const string AllowlistFile = "tests/PhotoReview.Architecture.Tests/core-filesystem-boundary-allowlist.txt";
    private const string CoreDir = "src/PhotoReview.Core";

    private static readonly Regex DirectFileSystemCall = new(@"\b(File|Directory|FileInfo|DirectoryInfo)\.", RegexOptions.Compiled);

    [Fact(DisplayName = "Rule CORE-FS: PhotoReview.Core does not call File./Directory./FileInfo./DirectoryInfo. directly outside the shrinking allowlist")]
    public void Core_DoesNotCallFileSystemDirectly_OutsideAllowlist()
    {
        var allowed = LoadAllowlist();
        var actualCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in RepoScan.CsFiles(CoreDir))
        {
            var relative = RepoScan.Relative(file);
            var lines = RepoScan.Lines(file);
            var count = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // A line that is entirely a comment (incl. XML doc) never counts: this guards real code, not prose.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectFileSystemCall.IsMatch(lines[i])) count++;
            }
            if (count > 0) actualCounts[relative] = count;
        }

        var problems = new List<string>();
        foreach (var (file, count) in actualCounts)
        {
            if (!allowed.TryGetValue(file, out var limit)) problems.Add($"NEW   {file}: {count} direct call(s), not in the allowlist");
            else if (count > limit) problems.Add($"MORE  {file}: {count}, allowlist says {limit}");
            else if (count < limit) problems.Add($"FEWER {file}: {count}, allowlist says {limit} (lower it)");
        }
        foreach (var file in allowed.Keys.Except(actualCounts.Keys, StringComparer.Ordinal))
            problems.Add($"GONE  {file}: allowlisted but no longer has any direct call (remove the line)");

        Assert.True(problems.Count == 0,
            "PhotoReview.Core must go through IFileSystem, not File./Directory./FileInfo./DirectoryInfo. directly " +
            $"(update {AllowlistFile} only if the new/changed call is justified there):\n" + string.Join('\n', problems.Order(StringComparer.Ordinal)));
    }

    private static Dictionary<string, int> LoadAllowlist()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(Path.Combine(RepoScan.Root, AllowlistFile)))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|', 2);
            Assert.True(parts.Length == 2, $"{AllowlistFile}: malformed line (expected path|count): {rawLine}");
            Assert.True(int.TryParse(parts[1], out var count), $"{AllowlistFile}: non-numeric count: {rawLine}");
            result[parts[0]] = count;
        }
        return result;
    }
}
