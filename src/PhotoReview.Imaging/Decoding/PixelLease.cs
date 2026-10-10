using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>C-02: pixel mượn hoặc sở hữu từ <see cref="IPlatformImageCodec.ToPixels"/>. Thực thi ở WP-02.</summary>
public readonly struct PixelLease : IDisposable
{
    public PixelLease(PixelBuffer pixels, bool owned) => throw new NotImplementedException();

    public PixelBuffer Pixels => throw new NotImplementedException();

    /// <summary>True -&gt; Dispose giải phóng (bản copy); false -&gt; mượn.</summary>
    public bool Owned => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
