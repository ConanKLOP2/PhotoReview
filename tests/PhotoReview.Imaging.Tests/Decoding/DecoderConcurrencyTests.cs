using System.Collections.Concurrent;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Metadata;
using PhotoReview.Imaging.Tests.Quality;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// One shared decoder instance per backend serves every preload/viewer thread (PreviewImageService caches it), so parallel
/// decodes of the same and of different images, through all backends at once, must each return the exact pixels a lone decode
/// returns -- no torn buffers, shared-state corruption, COM apartment failures or exceptions.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderConcurrencyTests
{
    [Fact(DisplayName = "Eight threads decoding different JPEGs through every backend at once each get exactly the pixels of a lone decode")]
    public async Task ParallelDecodes_MatchTheSerialReference()
    {
        var images = new[]
        {
            ExifTestData.EncodeJpegWithExif(64, 48, orientation: 1),
            ExifTestData.EncodeJpegWithExif(48, 64, orientation: 6),
            ExifTestData.EncodeJpegWithExif(33, 21, orientation: 3),
            ExifTestData.EncodeJpegWithExif(17, 40, orientation: 8),
        };
        (string Name, IImageDecoder Decoder)[] decoders =
        [
            ("Wpf", new WpfBitmapImageDecoder()),
            ("WicDirect", new WicDirectDecoder()),
            ("TurboJpeg", new TurboJpegDecoder()),
        ];
        var box = new DecodeBox(24, 24);

        // Serial reference, one decode per (decoder, image).
        var reference = new Dictionary<(string, int), byte[]>();
        foreach (var (name, decoder) in decoders)
            for (var i = 0; i < images.Length; i++)
                reference[(name, i)] = ImageCompare.ToBgra32(decoder.Decode(new DecodeRequest("ref.jpg", box, bytes: images[i])));

        using var barrier = new Barrier(8);
        var failures = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
        {
            var rng = new Random(worker);
            barrier.SignalAndWait();
            for (var n = 0; n < 60; n++)
            {
                var (name, decoder) = decoders[rng.Next(decoders.Length)];
                var index = rng.Next(images.Length);
                try
                {
                    var pixels = ImageCompare.ToBgra32(decoder.Decode(new DecodeRequest("par.jpg", box, bytes: images[index])));
                    if (!pixels.AsSpan().SequenceEqual(reference[(name, index)])) failures.Add($"{name} image {index}: pixels differ from the serial decode");
                }
                catch (Exception ex) { failures.Add($"{name} image {index}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }, TaskCreationOptions.LongRunning)));

        Assert.Empty(failures);
    }
}
