using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>ComparePairService.Find (per query) and BuildIndex (whole catalog) must give the same answer for every file.</summary>
public sealed class ComparePairFuzzTests
{
    private static readonly string[] Stems = ["IMG", "img", "DSC0001", "photo", "a (1)", "x (2) (3)", "日本", "İ", "ı", "(1)", " (1)", "b"];
    private static readonly string[] Suffixes = ["", "", " (1)", " (2)", " (10)", " (0)", " (99999999999)", "(3)", " ( 1)", " (１)", " (٣)"];
    private static readonly string[] Extensions = [".jpg", ".JPG", ".jpeg", ".png", ".Png", ""];
    private static readonly string[] Folders = [@"C:\Photos", @"c:\photos", @"C:\Photos\Sub", @"D:\Other"];

    [Theory(DisplayName = "Fuzz: BuildIndex equals Find for every path, and pairs are symmetric and in one folder with one extension")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void BuildIndex_MatchesFind(int seed)
    {
        var r = new Random(seed);
        for (var round = 0; round < 60; round++)
        {
            var files = Enumerable.Range(0, r.Next(1, 40))
                .Select(_ => Folders[r.Next(Folders.Length)] + @"\" + Stems[r.Next(Stems.Length)] + Suffixes[r.Next(Suffixes.Length)] + Extensions[r.Next(Extensions.Length)])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var index = ComparePairService.BuildIndex(files);

            foreach (var file in files)
            {
                var found = ComparePairService.Find(files, file);
                var indexed = index.TryGetValue(file, out var pair) ? pair : ((string, string)?)null;

                Assert.True(Equals(found, indexed), $"{file}: Find={found} Index={indexed}\n{string.Join("\n", files)}");
                if (found is var (left, right))
                {
                    Assert.Equal(System.IO.Path.GetDirectoryName(left)?.ToLowerInvariant(), System.IO.Path.GetDirectoryName(right)?.ToLowerInvariant());
                    Assert.Equal(System.IO.Path.GetExtension(left).ToLowerInvariant(), System.IO.Path.GetExtension(right).ToLowerInvariant());
                    Assert.Contains(left, files);
                    Assert.Contains(right, files);
                    Assert.NotEqual(left, right, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }
}
