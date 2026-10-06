using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Tests.Properties;

/// <summary>
/// ImageCacheKey identity over a small random universe (so equal pairs are common): equality holds exactly when every identity
/// field matches (path spelling normalised, box ignored for Original), equal keys hash equally, and a dictionary agrees.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ImageCacheKeyPropertyTests
{
    // (spellings of one file) x 2 files: normalisation (case, '/', '.' segments) must map every spelling to one key path.
    private static readonly string[][] Spellings =
    [
        [@"C:\Pics\A.jpg", @"c:\pics\a.JPG", @"C:\Pics\.\A.jpg", "C:/Pics/A.jpg", @"C:\Pics\Sub\..\A.jpg"],
        [@"C:\Pics\B.jpg", @"c:\PICS\b.jpg"],
    ];

    private static readonly int[] Widths = [0, 1600, 2400];
    private static readonly int[] Heights = [0, 900];

    private readonly record struct Spec(int File, int Spelling, long Length, long Ticks, bool Original, int W, int H, bool Orient, DecoderBackend Backend, byte Kind)
    {
        public ImageCacheKey Build() => ImageCacheKey.Create(
            new CatalogEntry(Spellings[File][Spelling]) { Length = Length, LastWriteUtc = new DateTime(Ticks, DateTimeKind.Utc) },
            Original, new DecodeBox(W, H), Orient, Backend, Kind);

        public bool SameIdentity(Spec o) =>
            File == o.File && Length == o.Length && Ticks == o.Ticks && Original == o.Original
            && (Original || (W == o.W && H == o.H)) && Orient == o.Orient && Backend == o.Backend && Kind == o.Kind;
    }

    private static Spec RandomSpec(Random rng)
    {
        var file = rng.Next(Spellings.Length);
        return new Spec(file, rng.Next(Spellings[file].Length), rng.Next(1, 3), 638_000_000_000_000_000L + rng.Next(0, 2),
            rng.Next(3) == 0, Widths[rng.Next(Widths.Length)], Heights[rng.Next(Heights.Length)], rng.Next(2) == 0,
            (DecoderBackend)rng.Next(0, 4), (byte)rng.Next(0, 3));
    }

    [Fact(DisplayName = "ImageCacheKey equality is exactly identity-field equality, hashes agree for equal keys, and a Dictionary behaves the same")]
    public void KeyEquality_MatchesIdentityFields()
    {
        PropertyRunner.Check("ImageCacheKey identity", iterations: 8000, (rng, _) =>
        {
            var a = RandomSpec(rng);
            var b = rng.Next(3) == 0 ? a with { Spelling = rng.Next(Spellings[a.File].Length) } : RandomSpec(rng);
            var keyA = a.Build();
            var keyB = b.Build();
            var expected = a.SameIdentity(b);

            Assert.Equal(expected, keyA == keyB);
            Assert.Equal(expected, keyA.Equals(keyB));
            Assert.Equal(!expected, keyA != keyB);
            Assert.Equal(keyA == keyB, keyB == keyA);
            if (expected)
            {
                Assert.Equal(keyA.GetHashCode(), keyB.GetHashCode());
                Assert.Equal(keyA.Path, keyB.Path);
            }

            var dict = new Dictionary<ImageCacheKey, int> { [keyA] = 1 };
            Assert.Equal(expected, dict.ContainsKey(keyB));
        });
    }

    [Fact(DisplayName = "Original keys ignore the decode box and CreateOriginal keeps the source identity")]
    public void OriginalKeys_IgnoreTheBox()
    {
        PropertyRunner.Check("ImageCacheKey original", iterations: 3000, (rng, _) =>
        {
            var spec = RandomSpec(rng) with { Original = true };
            var key = spec.Build();

            Assert.Equal(0, key.TargetWidth);
            Assert.Equal(0, key.TargetHeight);
            Assert.Equal(key, (spec with { W = rng.Next(0, 5000), H = rng.Next(0, 5000) }).Build());

            var preview = (spec with { Original = false, W = 2400, H = 900 }).Build();
            var fromPreview = ImageCacheKey.CreateOriginal(preview);
            Assert.True(fromPreview.IsOriginal);
            Assert.Equal(preview.Path, fromPreview.Path);
            Assert.Equal(preview.Length, fromPreview.Length);
            Assert.Equal(preview.LastWriteUtcTicks, fromPreview.LastWriteUtcTicks);
        });
    }
}
