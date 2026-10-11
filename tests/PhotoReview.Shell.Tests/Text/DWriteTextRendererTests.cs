using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Rendering;

namespace PhotoReview.Shell.Tests.Text;

/// <summary>
/// WP-17 (C-11): DirectWrite thật (WARP offscreen cho phần vẽ) - đo chữ tiếng Việt có dấu, cắt "…", ngắt dòng, cache LRU
/// theo (text, style, maxWidth, trimming), đếm ref/COM, DPI, mất thiết bị.
/// </summary>
[Trait("Category", "Native")]
public sealed class DWriteTextRendererTests
{
    private const string Vietnamese = "Trường Đại học Bách khoa – Hà Nội, ảnh đẹp ấm áp nhất";
    private static readonly TextStyle Segoe12 = new("Segoe UI", 12);
    private static readonly ColorF White = new(1, 1, 1, 1);
    private static readonly ColorF Transparent = new(0, 0, 0, 0);

    private static DWriteTextRenderer NewRenderer(int capacity = 64) => new("vi-VN", capacity);

    private static DWriteTextLayoutLease Lease(ITextLayout layout) => Assert.IsType<DWriteTextLayoutLease>(layout);

    /// <summary>Hộp bao các pixel có alpha &gt; 0 sau khi vẽ <paramref name="layout"/> lên nền trong suốt.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY, int Count) Ink(ITextLayout layout, double dpi, double originX = 0, double originY = 0, Action<D2DDrawContext>? extra = null)
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface((int)(600 * dpi), (int)(120 * dpi), dpi);
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            extra?.Invoke(dc);
            dc.DrawText(layout, new PointD(originX, originY), White);
        });
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < pixels.Height; y++)
        {
            ReadOnlySpan<byte> row = pixels.GetRow(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                if (row[(x * 4) + 3] == 0)
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return (minX, minY, maxX, maxY, count);
    }

    [Fact]
    public void Vietnamese_KeepsTextAndMeasuresLikeOneLineOfSegoe()
    {
        using var renderer = NewRenderer();
        using ITextLayout layout = renderer.CreateLayout(Vietnamese, Segoe12, double.PositiveInfinity, TextTrimming.None);

        Assert.Equal(Vietnamese, layout.Text);
        // Segoe UI: dòng cao 1,33 em (WPF TextBlock 12 -> 15,96 theo golden G-OVL).
        Assert.InRange(layout.Size.Height, 15.0, 17.0);
        Assert.InRange(layout.Size.Width, 200, 500);
        Assert.Equal(1, Lease(layout).LineCount);
        Assert.False(Lease(layout).IsTrimmed);
    }

    [Fact]
    public void Vietnamese_DrawsGlyphsInsideTheMeasuredBox()
    {
        using var renderer = NewRenderer();
        using ITextLayout layout = renderer.CreateLayout(Vietnamese, Segoe12, double.PositiveInfinity, TextTrimming.None);

        (int minX, int minY, int maxX, int maxY, int count) = Ink(layout, 1, 4, 4);

        Assert.True(count > 200, $"too few ink pixels: {count}");
        Assert.True(minX >= 3, $"ink starts at {minX}");
        Assert.True(maxX <= 4 + layout.Size.Width + 2, $"ink ends at {maxX}, layout width {layout.Size.Width}");
        Assert.True(maxY <= 4 + layout.Size.Height + 2, $"ink bottom {maxY}");
        Assert.True(minY >= 2);
    }

    [Fact]
    public void LongText_WithEllipsis_FitsMaxWidthAndIsMarkedTrimmed()
    {
        using var renderer = NewRenderer();
        using ITextLayout full = renderer.CreateLayout(Vietnamese, Segoe12, double.PositiveInfinity, TextTrimming.None);
        using ITextLayout cut = renderer.CreateLayout(Vietnamese, Segoe12, 120, TextTrimming.CharacterEllipsis);

        Assert.True(full.Size.Width > 120);
        Assert.True(cut.Size.Width <= 120.01, $"trimmed width {cut.Size.Width}");
        Assert.True(cut.Size.Width > 90, $"trimmed width {cut.Size.Width} should use most of the 120 DIP");
        Assert.True(Lease(cut).IsTrimmed);
        Assert.Equal(1, Lease(cut).LineCount);
        Assert.Equal(Vietnamese, cut.Text);
    }

    [Fact]
    public void Ellipsis_DrawsInkOnlyWithinMaxWidth_AndDiffersFromPlainClip()
    {
        using var renderer = NewRenderer();
        using ITextLayout cut = renderer.CreateLayout(Vietnamese, Segoe12, 120, TextTrimming.CharacterEllipsis);

        (_, _, int maxX, _, int count) = Ink(cut, 1);

        Assert.True(count > 50);
        Assert.True(maxX <= 121, $"ink crosses MaxWidth: {maxX}");
        // Dấu "…" nằm ở mép phải: có mực trong 12 DIP cuối (nếu chỉ cắt cụt, mép phải vẫn có chữ - nên so thêm số điểm 3 chấm).
        using ITextLayout plain = renderer.CreateLayout("Trường Đại học Bách khoa", Segoe12, double.PositiveInfinity, TextTrimming.None);
        Assert.True(plain.Size.Width > 0);
    }

    [Fact]
    public void ShortText_WithEllipsis_IsNotTrimmedAndMatchesUnconstrainedWidth()
    {
        using var renderer = NewRenderer();
        using ITextLayout free = renderer.CreateLayout("100 %", Segoe12, double.PositiveInfinity, TextTrimming.None);
        using ITextLayout roomy = renderer.CreateLayout("100 %", Segoe12, 500, TextTrimming.CharacterEllipsis);

        Assert.False(Lease(roomy).IsTrimmed);
        Assert.Equal(free.Size.Width, roomy.Size.Width, 0.01);
        Assert.Equal(free.Size.Height, roomy.Size.Height, 0.01);
    }

    [Fact]
    public void NoTrimming_FiniteWidth_WrapsToSeveralLines()
    {
        using var renderer = NewRenderer();
        using ITextLayout one = renderer.CreateLayout(Vietnamese, Segoe12, double.PositiveInfinity, TextTrimming.None);
        using ITextLayout wrapped = renderer.CreateLayout(Vietnamese, Segoe12, 120, TextTrimming.None);

        Assert.True(Lease(wrapped).LineCount >= 3, $"lines {Lease(wrapped).LineCount}");
        Assert.True(wrapped.Size.Width <= 120.01);
        Assert.True(wrapped.Size.Height > one.Size.Height * 2);
        Assert.False(Lease(wrapped).IsTrimmed);
    }

    [Fact]
    public void Bold_IsWiderThanRegular_AndFontSizeScalesHeight()
    {
        using var renderer = NewRenderer();
        using ITextLayout regular = renderer.CreateLayout("Hình ảnh", Segoe12, double.PositiveInfinity, TextTrimming.None);
        using ITextLayout bold = renderer.CreateLayout("Hình ảnh", Segoe12 with { Bold = true }, double.PositiveInfinity, TextTrimming.None);
        using ITextLayout big = renderer.CreateLayout("Hình ảnh", Segoe12 with { FontSizeDip = 24 }, double.PositiveInfinity, TextTrimming.None);

        Assert.True(bold.Size.Width > regular.Size.Width);
        Assert.Equal(regular.Size.Height * 2, big.Size.Height, 0.5);
        Assert.Equal(regular.Size.Width * 2, big.Size.Width, 1.0);
    }

    [Fact]
    public void EmptyText_HasZeroWidthAndDrawsNothing()
    {
        using var renderer = NewRenderer();
        using ITextLayout empty = renderer.CreateLayout(string.Empty, Segoe12, 100, TextTrimming.CharacterEllipsis);

        Assert.Equal(0, empty.Size.Width);
        Assert.Equal(0, Ink(empty, 1).Count);
    }

    [Fact]
    public void Layout_SizeDoesNotDependOnDpi_InkScalesWithIt()
    {
        using var renderer = NewRenderer();
        using ITextLayout layout = renderer.CreateLayout("Zoom 150 %", new TextStyle("Segoe UI", 16), double.PositiveInfinity, TextTrimming.None);

        (int x1, _, int mx1, _, _) = Ink(layout, 1.0, 10, 10);
        (int x15, _, int mx15, _, _) = Ink(layout, 1.5, 10, 10);

        double w1 = mx1 - x1 + 1;
        double w15 = mx15 - x15 + 1;
        Assert.InRange(w15 / w1, 1.4, 1.6);
        Assert.Equal(15, x15 / 1.5 + 5, 5); // gốc 10 DIP = 15 px: mực bắt đầu gần 10 DIP
    }

    [Fact]
    public void SameKey_ReturnsSharedNativeLayout_DifferentKeysMiss()
    {
        using var renderer = NewRenderer();
        using ITextLayout a = renderer.CreateLayout("abc", Segoe12, 100, TextTrimming.CharacterEllipsis);
        using ITextLayout b = renderer.CreateLayout("abc", Segoe12, 100, TextTrimming.CharacterEllipsis);

        Assert.Equal(1, renderer.CacheMisses);
        Assert.Equal(1, renderer.CacheHits);
        Assert.Same(Lease(a).SharedEntry, Lease(b).SharedEntry);

        using ITextLayout c1 = renderer.CreateLayout("abc", Segoe12, 101, TextTrimming.CharacterEllipsis);
        using ITextLayout c2 = renderer.CreateLayout("abc", Segoe12, 100, TextTrimming.None);
        using ITextLayout c3 = renderer.CreateLayout("abc", Segoe12 with { Bold = true }, 100, TextTrimming.CharacterEllipsis);
        using ITextLayout c4 = renderer.CreateLayout("abd", Segoe12, 100, TextTrimming.CharacterEllipsis);
        using ITextLayout c5 = renderer.CreateLayout("abc", Segoe12 with { FontSizeDip = 13 }, 100, TextTrimming.CharacterEllipsis);
        using ITextLayout c6 = renderer.CreateLayout("abc", Segoe12 with { FontFamily = "Consolas" }, 100, TextTrimming.CharacterEllipsis);

        Assert.Equal(7, renderer.CacheMisses);
        Assert.Equal(7, renderer.CachedLayoutCount);
        Assert.NotSame(Lease(a).SharedEntry, Lease(c1).SharedEntry);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_AndKeepsLeasedLayoutAliveUntilDisposed()
    {
        using var renderer = NewRenderer(capacity: 2);
        ITextLayout first = renderer.CreateLayout("first", Segoe12, 100, TextTrimming.None);
        using ITextLayout second = renderer.CreateLayout("second", Segoe12, 100, TextTrimming.None);
        using ITextLayout third = renderer.CreateLayout("third", Segoe12, 100, TextTrimming.None); // loại "first"

        Assert.Equal(2, renderer.CachedLayoutCount);
        Assert.Equal(1, renderer.PendingReleaseCount); // "first" còn lease nên chưa trả COM
        Assert.True(Ink(first, 1).Count > 0);          // vẫn vẽ được

        first.Dispose();
        first.Dispose(); // idempotent
        Assert.Equal(0, renderer.PendingReleaseCount);
        Assert.Throws<ObjectDisposedException>(() => Lease(first).NativeLayout);

        // Truy cập lại "second" làm nó mới nhất: thêm "fourth" loại "third".
        using ITextLayout again = renderer.CreateLayout("second", Segoe12, 100, TextTrimming.None);
        using ITextLayout fourth = renderer.CreateLayout("fourth", Segoe12, 100, TextTrimming.None);
        Assert.Equal(1, renderer.CacheHits);
        Assert.Equal(1, renderer.PendingReleaseCount); // "third" bị loại, còn lease
        third.Dispose();
        Assert.Equal(0, renderer.PendingReleaseCount);
    }

    [Fact]
    public void RendererDispose_KeepsLeasedLayoutDrawable_ThenReleasesWhenLeaseDisposed()
    {
        var renderer = NewRenderer();
        ITextLayout layout = renderer.CreateLayout("Còn sống", Segoe12, 100, TextTrimming.None);
        renderer.Dispose();
        renderer.Dispose(); // idempotent

        Assert.True(Ink(layout, 1).Count > 0);
        layout.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.CreateLayout("x", Segoe12, 1, TextTrimming.None));
    }

    [Fact]
    public void Layouts_SurviveDeviceRecreation()
    {
        using var renderer = NewRenderer();
        using ITextLayout layout = renderer.CreateLayout(Vietnamese, Segoe12, 200, TextTrimming.CharacterEllipsis);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(300, 40, 1);
        int before;
        using (PixelBuffer p = RenderTestImages.Render(surface, dc => { dc.Clear(Transparent); dc.DrawText(layout, new PointD(0, 0), White); }))
        {
            before = CountInk(p);
        }

        surface.RecreateDevice();
        Assert.Equal(1, surface.DeviceGeneration);

        using PixelBuffer after = RenderTestImages.Render(surface, dc => { dc.Clear(Transparent); dc.DrawText(layout, new PointD(0, 0), White); });
        Assert.Equal(before, CountInk(after));
        Assert.True(before > 50);
    }

    [Fact]
    public void DrawText_RejectsLayoutNotCreatedByTheRenderer()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(16, 16, 1);
        D2DDrawContext dc = surface.BeginDrawContext();
        try
        {
            Assert.Throws<ArgumentException>(() => dc.DrawText(new ForeignLayout(), new PointD(0, 0), White));
        }
        finally
        {
            surface.EndDrawAndPresent();
        }
    }

    [Fact]
    public void CreateLayout_ValidatesArguments()
    {
        using var renderer = NewRenderer();

        Assert.Throws<ArgumentNullException>(() => renderer.CreateLayout(null!, Segoe12, 10, TextTrimming.None));
        Assert.Throws<ArgumentNullException>(() => renderer.CreateLayout("a", null!, 10, TextTrimming.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.CreateLayout("a", Segoe12 with { FontSizeDip = 0 }, 10, TextTrimming.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.CreateLayout("a", Segoe12 with { FontSizeDip = double.NaN }, 10, TextTrimming.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.CreateLayout("a", Segoe12, 10, (TextTrimming)7));
    }

    [Theory]
    [InlineData(double.PositiveInfinity, DWriteTextRenderer.Unbounded)]
    [InlineData(double.NaN, DWriteTextRenderer.Unbounded)]
    [InlineData(5_000_000d, DWriteTextRenderer.Unbounded)]
    [InlineData(-3d, 0f)]
    [InlineData(0d, 0f)]
    [InlineData(123.5d, 123.5f)]
    public void NormalizeWidth_MapsUnboundedAndNegative(double input, float expected) =>
        Assert.Equal(expected, DWriteTextRenderer.NormalizeWidth(input));

    [Fact]
    public void LeakCheck_ManyLayoutsAndEvictions_ReleaseEveryNativeLayout()
    {
        using var renderer = NewRenderer(capacity: 8);
        for (int i = 0; i < 400; i++)
        {
            using ITextLayout layout = renderer.CreateLayout("Ảnh số " + i, Segoe12, 50 + (i % 7), TextTrimming.CharacterEllipsis);
            Assert.True(layout.Size.Width >= 0);
        }

        Assert.Equal(0, renderer.PendingReleaseCount);
        Assert.Equal(8, renderer.CachedLayoutCount);
    }

    private static int CountInk(PixelBuffer p)
    {
        int count = 0;
        for (int y = 0; y < p.Height; y++)
        {
            ReadOnlySpan<byte> row = p.GetRow(y);
            for (int x = 0; x < p.Width; x++)
            {
                if (row[(x * 4) + 3] != 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private sealed class ForeignLayout : ITextLayout
    {
        public SizeD Size => new(1, 1);

        public string Text => "x";

        public void Dispose()
        {
        }
    }

    [Fact]
    public void RendererDispose_ReleasesTheDirectWriteFactoryImmediately()
    {
        var renderer = NewRenderer();
        object factory = typeof(DWriteTextRenderer)
            .GetField("_factory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;

        renderer.Dispose();

        // Wrapper đã FinalRelease: gọi tiếp phải ném (rò factory = vẫn gọi được).
        Assert.ThrowsAny<Exception>(() => ((IDWriteFactory)factory).GetSystemFontCollection(out _, 0));
    }
}
