using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>
/// BuildIndex orders files by full path (ignoring case, then ordinally) and keeps input order for paths that normalise to
/// the same full path (LINQ OrderBy was stable). The fuzz test deduplicates its inputs, so it never produces two spellings
/// of one path; this pins that tie-break, which decides which spelling becomes the "original" of a pair.
/// </summary>
public sealed class ComparePairTieOrderTests
{
    [Fact(DisplayName = "Two spellings of one file: the one listed first stays the original, like the stable LINQ order, in a list large enough to defeat an unstable sort")]
    public void BuildIndex_SpellingsOfOneFullPath_KeepInputOrder()
    {
        var files = new List<string>();
        var firstSpellings = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            var plain = $@"C:\Photos\s{i:D2}.jpg";
            var dotted = $@"C:\Photos\.\s{i:D2}.jpg"; // GetFullPath turns it into the same full path
            var (first, second) = i % 2 == 0 ? (plain, dotted) : (dotted, plain);
            files.Add(first);
            files.Add(second);
            files.Add($@"C:\Photos\s{i:D2} (1).jpg");
            firstSpellings.Add(first);
        }

        var index = ComparePairService.BuildIndex(files);

        for (var i = 0; i < 40; i++)
        {
            var numbered = $@"C:\Photos\s{i:D2} (1).jpg";
            Assert.True(index.TryGetValue(numbered, out var pair), numbered);
            Assert.Equal((firstSpellings[i], numbered), pair);
            Assert.Equal(ComparePairService.Find(files, numbered), pair);
        }
    }
}
