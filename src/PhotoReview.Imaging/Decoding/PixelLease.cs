using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// C-02: pixel mượn hoặc sở hữu từ <see cref="IPlatformImageCodec.ToPixels"/>. Thực thi ở WP-02.
/// <c>default(PixelLease)</c> không mang pixel (<see cref="Pixels"/> null) và Dispose là no-op.
/// </summary>
public readonly struct PixelLease : IDisposable
{
    public PixelLease(PixelBuffer pixels, bool owned)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Pixels = pixels;
        Owned = owned;
    }

    public PixelBuffer Pixels { get; }

    /// <summary>True -&gt; Dispose giải phóng (bản copy); false -&gt; mượn.</summary>
    public bool Owned { get; }

    /// <summary>Giải phóng <see cref="Pixels"/> chỉ khi <see cref="Owned"/>; buffer mượn để nguyên cho chủ của nó.</summary>
    public void Dispose()
    {
        if (Owned) Pixels.Dispose();
    }
}
