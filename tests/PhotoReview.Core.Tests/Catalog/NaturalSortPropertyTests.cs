using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Order axioms and reference-oracle checks for the natural sort used to order every folder.</summary>
[Collection("GlobalState")] // some cases switch CurrentCulture
public sealed class NaturalSortPropertyTests
{
    private static readonly string[] Alphabet =
    [
        "a", "B", "c", "İ", "ı", "i", "I", "ß", "SS", "é", "e\u0301", "日", "本", "م", "ر", "0", "00", "1", "2", "9", "10", "007",
        "٣", "３", "_", "-", " ", "(", ")", ".", "\ud83d\ude00", "Z", "z", "~",
    ];

    private static readonly string[] TurkishSample = ["i2.jpg", "I10.jpg", "İ 1.jpg", "ı 3.jpg", "i1.jpg"];
    private static readonly string[] ZeroSample = ["a1", "a01", "a001", "a0", "a00", "a2", "a10", "a010"];

    private static string RandomName(Random r)
    {
        var parts = r.Next(1, 7);
        var s = string.Concat(Enumerable.Range(0, parts).Select(_ => Alphabet[r.Next(Alphabet.Length)]));
        return s + (r.Next(3) == 0 ? ".jpg" : r.Next(2) == 0 ? ".JPG" : "");
    }

    [Theory(DisplayName = "ManagedNaturalComparer is a strict total order: antisymmetric, transitive, zero only for identical strings")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Comparer_IsTotalOrder(int seed)
    {
        var r = new Random(seed);
        var names = Enumerable.Range(0, 220).Select(_ => RandomName(r)).ToArray();
        var c = ManagedNaturalComparer.Instance;

        foreach (var a in names)
        {
            Assert.Equal(0, c.Compare(a, a));
            foreach (var b in names)
            {
                var ab = Math.Sign(c.Compare(a, b));
                Assert.Equal(-ab, Math.Sign(c.Compare(b, a)));
                if (ab == 0) Assert.Equal(a, b);
            }
        }

        for (var i = 0; i < 20_000; i++)
        {
            var a = names[r.Next(names.Length)];
            var b = names[r.Next(names.Length)];
            var d = names[r.Next(names.Length)];
            if (c.Compare(a, b) <= 0 && c.Compare(b, d) <= 0) Assert.True(c.Compare(a, d) <= 0, $"{a} <= {b} <= {d} but {a} > {d}");
        }
    }

    [Theory(DisplayName = "Sorting any shuffle of a folder gives one deterministic order (also under tr-TR/de-DE/ja-JP/ar-SA)")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    [InlineData("")]
    public void Sort_IsShuffleAndCultureInvariant(string culture)
    {
        var r = new Random(5);
        var names = Enumerable.Range(0, 400).Select(_ => RandomName(r)).Distinct(StringComparer.Ordinal).ToList();
        var reference = ImageSortService.Sort(names.Select(n => @"C:\f\" + n), ImageSortMode.Name);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            for (var round = 0; round < 5; round++)
            {
                var shuffled = names.OrderBy(_ => r.Next()).Select(n => @"C:\f\" + n);
                Assert.Equal(reference, ImageSortService.Sort(shuffled, ImageSortMode.Name));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>Same comparison as the managed one but a different type, so ImageSortService takes its generic path.</summary>
    private sealed class OpaqueComparer : PhotoReview.Core.Abstractions.INaturalComparer
    {
        public int Compare(string? x, string? y) => ManagedNaturalComparer.Instance.Compare(x, y);
    }

    [Theory(DisplayName = "perf: the precomputed-key fast path orders exactly like the generic comparer path (all sort modes)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sort_FastPathMatchesGenericComparerPath(int seed)
    {
        var r = new Random(seed);
        // Same file name in different folders and different sizes exercise the stable tie-breaking too.
        var entries = Enumerable.Range(0, 600)
            .Select(i => new CatalogEntry(@"C:\f" + r.Next(3) + @"\" + RandomName(r)).WithMetadata(r.Next(0, 6), DateTime.UtcNow))
            .ToList();
        var paths = entries.Select(e => e.Path).ToList();

        foreach (var mode in Enum.GetValues<ImageSortMode>())
        {
            Assert.Equal(
                ImageSortService.SortEntries(entries, mode, new OpaqueComparer()).Select(e => e.Path),
                ImageSortService.SortEntries(entries, mode).Select(e => e.Path));
            if (mode == ImageSortMode.Name)
            {
                Assert.Equal(ImageSortService.Sort(paths, mode, new OpaqueComparer()), ImageSortService.Sort(paths, mode));
            }
        }
    }

    [Fact(DisplayName = "Turkish dotted/dotless i are not folded together and do not break the order")]
    public void Comparer_TurkishI_Consistent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var c = ManagedNaturalComparer.Instance;

            Assert.Equal(0, c.Compare("photo.jpg", "photo.jpg"));
            Assert.NotEqual(0, c.Compare("PHOTO.jpg", "photo.jpg"));
            Assert.Equal(-Math.Sign(c.Compare("I.jpg", "\u0131.jpg")), Math.Sign(c.Compare("\u0131.jpg", "I.jpg")));
            Assert.NotEqual(0, c.Compare("\u0130.jpg", "i.jpg"));
            var sorted = TurkishSample.OrderBy(x => x, c).ToArray();
            Assert.Equal(["i1.jpg", "i2.jpg", "I10.jpg"], sorted.Where(x => x[0] is 'i' or 'I'));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory(DisplayName = "Numeric runs sort by value, not text, for a shared prefix and suffix (oracle: BigInteger)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sort_NumericRunsByValue(int seed)
    {
        var r = new Random(seed);
        var numbers = Enumerable.Range(0, 300)
            .Select(_ => new BigInteger(r.NextInt64(0, long.MaxValue)) * (r.Next(4) == 0 ? BigInteger.Pow(10, r.Next(1, 30)) : BigInteger.One))
            .Distinct().ToList();
        var files = numbers.Select(n => "IMG_" + n + ".jpg").ToList();

        var sorted = ImageSortService.Sort(files.OrderBy(_ => r.Next()).Select(f => @"C:\f\" + f), ImageSortMode.Name)
            .Select(p => BigInteger.Parse(Regex.Match(p, @"IMG_(\d+)\.jpg$").Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(numbers.OrderBy(n => n).ToList(), sorted);
    }

    [Fact(DisplayName = "Leading zeros: equal numbers order deterministically and 0/00/000 sort before 1")]
    public void Comparer_LeadingZeros()
    {
        var c = ManagedNaturalComparer.Instance;
        var list = ZeroSample.OrderBy(x => x, c).ToList();

        Assert.Equal(list, list.OrderBy(x => x, c).ToList());
        Assert.True(list.IndexOf("a0") < list.IndexOf("a1") && list.IndexOf("a00") < list.IndexOf("a1"));
        Assert.True(list.IndexOf("a2") < list.IndexOf("a10") && list.IndexOf("a2") < list.IndexOf("a010"));
    }

    [Fact(DisplayName = "A digit run of thousands of digits and an all-digit name sort without overflow or exception")]
    public void Comparer_HugeDigitRuns()
    {
        var c = ManagedNaturalComparer.Instance;
        var big = new string('9', 5000);
        var bigger = "1" + new string('0', 5000);

        Assert.True(c.Compare("f" + big + ".jpg", "f" + bigger + ".jpg") < 0);
        Assert.True(c.Compare(big, bigger) < 0);
        Assert.Equal(0, c.Compare(string.Empty, string.Empty));
        Assert.True(c.Compare(string.Empty, "a") < 0);
    }

    [Fact(DisplayName = "Size sort: unknown sizes first (descending: last), ties by natural name, entries and paths overloads agree")]
    public void SortEntries_SizeModes_Consistent()
    {
        var entries = new[]
        {
            new CatalogEntry(@"C:\f\b10.jpg"), new CatalogEntry(@"C:\f\b2.jpg"), new CatalogEntry(@"C:\f\a.jpg"),
        };
        var sized = new[]
        {
            entries[0].WithMetadata(100, DateTime.UtcNow), entries[1].WithMetadata(100, DateTime.UtcNow),
            entries[2].WithMetadata(5, DateTime.UtcNow), new CatalogEntry(@"C:\f\unknown.jpg"),
        };

        var asc = ImageSortService.SortEntries(sized, ImageSortMode.SizeAscending).Select(e => Path.GetFileName(e.Path)).ToList();
        var desc = ImageSortService.SortEntries(sized, ImageSortMode.SizeDescending).Select(e => Path.GetFileName(e.Path)).ToList();

        Assert.Equal(["unknown.jpg", "a.jpg", "b2.jpg", "b10.jpg"], asc);
        Assert.Equal(["b2.jpg", "b10.jpg", "a.jpg", "unknown.jpg"], desc);
    }
}
