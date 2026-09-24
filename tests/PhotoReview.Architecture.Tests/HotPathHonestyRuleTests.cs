using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// TS06: "Honest hot-path tests" - every test must either implement a real assertion or be
/// explicitly marked with a Skip reason. A silent placeholder that returns without asserting
/// anything reads as a passing test while proving nothing - the pre-TC04 stub this rule guards
/// against was an async test method whose entire body was an await on an already-completed task,
/// with no Skip attribute.
///
/// Kept in its own file (not the existing structure-optimize rules file) so it does not collide
/// with other branches that are concurrently adding architecture tests there.
/// </summary>
public sealed class HotPathHonestyRuleTests
{
    [Fact(DisplayName = "Rule TS06: every [Fact]/[Theory] test method body containing TODO must be [Skip]-ped")]
    [Trait("Category", "Architecture")]
    public void FactOrTheoryWithTodoMustHaveSkip()
    {
        var violations = new List<string>();

        foreach (var file in RepoScan.CsFiles("tests"))
        {

            var text = RepoScan.Text(file);
            foreach (var method in EnumerateTestMethods(text))
            {
                if (!ContainsTodoMarker(method.Body)) continue;
                if (method.HasSkip) continue;

                var relative = RepoScan.Relative(file);
                violations.Add($"{relative}:{method.LineNumber}: test method has a TODO marker in its body but no [Skip = \"reason\"]");
            }
        }

        Assert.True(violations.Count == 0,
            "Every test with a TODO in its body must carry [Skip = \"reason\"] (TS06 honesty rule):\n" +
            string.Join("\n", violations));
    }

    private readonly record struct TestMethod(string Body, bool HasSkip, int LineNumber);

    private static readonly Regex FactOrTheoryAttribute = new(@"^\s*\[(Fact|Theory)\b", RegexOptions.Compiled);
    private static readonly Regex SkipMarker = new(@"\bSkip\s*=", RegexOptions.Compiled);
    // Uppercase-only by convention (matches the repo's existing TODO-marker rule): a lowercase
    // "todo" inside a descriptive comment (e.g. "scan for todo markers") is not a placeholder.
    private static readonly Regex TodoMarker = new(@"\bTODO\b", RegexOptions.Compiled);

    /// <summary>
    /// Finds each [Fact]/[Theory]-attributed method, collecting the full attribute block above it
    /// (to catch Skip on any line of a multi-line attribute list) and the brace-matched method body.
    /// </summary>
    private static IEnumerable<TestMethod> EnumerateTestMethods(string sourceText)
    {
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!FactOrTheoryAttribute.IsMatch(lines[i])) continue;

            // Collect the contiguous attribute block: consecutive lines starting with '[' or that
            // are a continuation of a multi-line attribute (until we hit the method signature).
            var attributeText = lines[i];
            var j = i;
            while (j + 1 < lines.Length && !LooksLikeMethodSignature(lines[j + 1]) && !FactOrTheoryAttribute.IsMatch(lines[j + 1]))
            {
                j++;
                attributeText += "\n" + lines[j];
                if (j - i > 20) break; // safety bound against runaway scans
            }

            // Find the method signature line (first non-attribute line after the block).
            var sigLine = j + 1;
            while (sigLine < lines.Length && (string.IsNullOrWhiteSpace(lines[sigLine]) || lines[sigLine].TrimStart().StartsWith('[')))
            {
                if (lines[sigLine].TrimStart().StartsWith('[')) attributeText += "\n" + lines[sigLine];
                sigLine++;
            }
            if (sigLine >= lines.Length) continue;

            // Find the opening brace of the method body, then brace-match to the closing one.
            var openLine = sigLine;
            while (openLine < lines.Length && !lines[openLine].Contains('{')) openLine++;
            if (openLine >= lines.Length) continue; // expression-bodied or abstract; nothing to scan

            var depth = 0;
            var bodyLines = new List<string>();
            var started = false;
            var endLine = openLine;
            for (var k = openLine; k < lines.Length; k++)
            {
                foreach (var ch in lines[k])
                {
                    if (ch == '{') { depth++; started = true; }
                    else if (ch == '}') depth--;
                }
                bodyLines.Add(lines[k]);
                if (started && depth <= 0) { endLine = k; break; }
            }

            yield return new TestMethod(
                Body: string.Join("\n", bodyLines),
                HasSkip: SkipMarker.IsMatch(attributeText),
                LineNumber: i + 1);
        }
    }

    private static bool LooksLikeMethodSignature(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && !trimmed.StartsWith('[') &&
               (trimmed.Contains("public ", StringComparison.Ordinal) ||
                trimmed.Contains("private ", StringComparison.Ordinal) ||
                trimmed.Contains("internal ", StringComparison.Ordinal));
    }

    private static bool ContainsTodoMarker(string methodBody)
    {
        foreach (var line in methodBody.Split('\n'))
        {
            // Only count TODO inside comments - a string literal or identifier containing "TODO"
            // (e.g. a test asserting on the literal word) is not a placeholder marker.
            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIndex < 0) continue;
            if (TodoMarker.IsMatch(line[commentIndex..])) return true;
        }
        return false;
    }

    [Fact(DisplayName = "Rule OC14: MainWindow code-behind holds no file-action/undo gate logic")]
    [Trait("Category", "Architecture")]
    public void MainWindowCodeBehindHasNoFileActionGateLogic()
    {
        var text = RepoScan.Text(Path.Combine(RepoScan.Root, "src", "PhotoReview.App", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("Interlocked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_fileActionInProgress", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Volatile.", text, StringComparison.Ordinal);
    }
}
