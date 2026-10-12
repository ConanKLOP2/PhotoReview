using PhotoReview.TestSupport.PixelGolden;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28b (G-PIX extension of WP-28, NO-WPF-EXEC-PLAN 7.3 (2)): the last frame of the Win32 shell against the WPF
/// <c>MainWindow</c> for compare of two pictures, the first frame after F11 / a DPI change (fake 720x480 px screen) and a bitmap
/// whose pixel size differs from the original (a RAW's embedded preview, then the full decode). Everything runs on the D2D WARP
/// offscreen surface with sizes taken from the case, so nothing depends on the monitor of the runner (CI: 1024x768).
/// RAW: the repo has no RAW corpus, so the "RAW" pictures are the synthetic <c>photo</c> scene at two pixel sizes through the same
/// bitmap-stretched-to-the-original's-layout path; a real RAW decode is NOT exercised here (see the decision fragment).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PixelGoldenExtTests(ITestOutputHelper output)
{
    public static TheoryData<string> FrameCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in PixelGoldenExtCatalog.Frames)
        {
            data.Add(c.Name);
        }

        return data;
    }

    public static TheoryData<string> CompareCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in PixelGoldenExtCatalog.Compare)
        {
            data.Add(c.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FrameCases))]
    public void Frame_MatchesTheWpfGolden_WithinTheSection73Bars(string caseName)
    {
        var c = PixelGoldenExtCatalog.Frame(caseName);

        PixelGoldenTests.Verify(c, PixelGoldenShellRenderer.Render(c), output);
    }

    [Theory]
    [MemberData(nameof(CompareCases))]
    public void Compare_MatchesTheWpfGolden_BordersAndCanvasByteIdentical_PicturesWithinTheFitBar(string caseName)
    {
        var c = PixelGoldenExtCatalog.CompareCase(caseName);
        var golden = PixelGoldenTests.ReadGolden(c.Name);
        var shell = PixelGoldenCompareRenderer.Render(c);
        Assert.Equal((c.PixelWidth, c.PixelHeight), (golden.Width, golden.Height));
        Assert.Equal((c.PixelWidth, c.PixelHeight), (shell.Frame.Width, shell.Frame.Height));

        var measure = PixelGoldenCompareComparer.Measure(golden, shell);
        output.WriteLine(PixelGoldenExtCatalog.Describe(c));
        output.WriteLine(measure.Summary);
        var violations = PixelGoldenCompareComparer.Violations(measure);
        if (violations.Count > 0)
        {
            var diff = PixelDiffReport.Write(PixelGoldenFiles.ReportDirectory(), c.Name, golden, shell.Frame);
            Assert.Fail($"{PixelGoldenExtCatalog.Describe(c)}\n  {string.Join("\n  ", violations)}\n  diff image: {diff}");
        }
    }

    [Fact]
    public void Compare_SelectionBorder_IsLimeGreenOnTheSelectedCellAndGreyOnTheOther_AtTheLayoutPosition()
    {
        // Independent of the golden: the colours of CompareBorderBrushConverter (LimeGreen / #555555) at the middle of each cell's left edge.
        var c = PixelGoldenExtCatalog.CompareCase("cmp-photo-land-port-right");
        var shell = PixelGoldenCompareRenderer.Render(c);

        var leftEdge = shell.Frame.At(shell.LeftBorder.X, shell.LeftBorder.Y + (shell.LeftBorder.Height / 2));
        var rightEdge = shell.Frame.At(shell.RightBorder.X, shell.RightBorder.Y + (shell.RightBorder.Height / 2));

        Assert.Equal(((byte)0x55, (byte)0x55, (byte)0x55), leftEdge);
        Assert.Equal(((byte)0x32, (byte)0xCD, (byte)0x32), (rightEdge.B, rightEdge.G, rightEdge.R));
        // The border sits INSIDE its cell box: the pixel just outside is the panel canvas.
        Assert.Equal(((byte)0x10, (byte)0x10, (byte)0x10),
            shell.Frame.At(shell.RightBorder.X - 1, shell.RightBorder.Y + (shell.RightBorder.Height / 2)));
    }

    [Fact]
    public void Compare_CellPicture_IsDrawnWithTheSameHighQualityFilterAsAMainPictureInThatBox()
    {
        // WPF gives CompareLeftImage the same BitmapScalingMode as MainImage (Viewer.ScalingQuality = HighQuality here). A 4x downscale
        // is where a cheaper filter would show: the cell must equal the picture drawn as the main image into the cell's box, per pixel.
        var c = PixelGoldenExtCatalog.CompareCase("cmp-photo-pair-none");
        var shell = PixelGoldenCompareRenderer.Render(c);
        var side = c.Left!;
        var client = new PhotoReview.App.Input.SizeD(c.ClientWidth, c.ClientHeight);
        var box = PhotoReview.Shell.Win32.Viewing.CompareLayout.Image(client, PhotoReview.Shell.Win32.Viewing.CompareSide.Left,
            side.OrientedWidth, side.OrientedHeight);

        using var picture = PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(side.Scene, side.Width, side.Height));
        using var surface = PhotoReview.Shell.Rendering.D2DRenderSurface.CreateOffscreenSurface(c.PixelWidth, c.PixelHeight, c.Dpi);
        var dispatcher = new PhotoReview.Shell.Tests.Rendering.ManualDispatcher();
        using var cache = new PhotoReview.Shell.Rendering.GpuImageCache(surface, dispatcher);
        using var composer = new PhotoReview.Shell.Win32.Viewing.PresentationComposer(cache, dispatcher,
            new PhotoReview.Shell.Tests.Overlay.FakeFrameClock(), () => { }, ticksPerSecond: 1000, timestamp: () => 0);
        composer.Set(picture);
        var inputs = new PhotoReview.Shell.Win32.Viewing.PresentationInputs(client, box, PhotoReview.Core.Model.ScalingQuality.HighQuality,
            PhotoReview.Core.Model.ScalingQuality.HighQuality, c.Dpi, false, default);
        using var pixels = PhotoReview.Shell.Tests.Rendering.RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(PhotoReview.Shell.Rendering.RenderColors.DarkCanvas);
            composer.Draw(dc, inputs);
        });
        var main = PixelGoldenShellRenderer.ToFrame(pixels);

        var cell = shell.LeftImage!.Value.Inflate(-1);
        Assert.Equal(0, PixelMetrics.MaxAbsoluteError(main, shell.Frame, cell));
    }

    [Fact]
    public void Compare_DifferentOrientationsInTheTwoCells_ShowBothUpright()
    {
        // exif 1 (stored landscape) and exif 6 (stored landscape, shown portrait): the cells must be a landscape and a portrait picture.
        var c = PixelGoldenExtCatalog.CompareCase("cmp-orient-1-vs-6-left");
        var shell = PixelGoldenCompareRenderer.Render(c);

        Assert.NotNull(shell.LeftImage);
        Assert.NotNull(shell.RightImage);
        Assert.True(shell.LeftImage!.Value.Width > shell.LeftImage.Value.Height, "left (exif 1) must be landscape");
        Assert.True(shell.RightImage!.Value.Height > shell.RightImage.Value.Width, "right (exif 6) must be portrait");
    }

    // ---- F11 / DPI: the FIRST frame after the window changes -------------------------------------------------

    private static PixelGoldenCase Start(string scene, int iw, int ih, int zoom = 0, double offset = 0) =>
        new("start", scene, iw, ih, 480, 320, 1.0, zoom, offset, offset, false, 1, false,
            zoom == 0 ? PixelGoldenExpectation.Fit : PixelGoldenExpectation.Identical);

    /// <summary>
    /// The first frame after the window changed (<paramref name="first"/>) and the frame that follows once the picture has sat still
    /// for <c>ScalingPolicy.SettleMs</c>. Policy (WP-21): while the picture MOVES (its box changed) and is shrunk by more than 4x in
    /// area, the first frame is drawn Linear and the high-quality frame follows when it settles; otherwise the first frame is already
    /// the settled one. Either way the SETTLED frame is, byte for byte, what a window that was in the target state all along draws,
    /// and that frame is the WPF golden within the 7.3 bars.
    /// </summary>
    private void AssertFirstFrameAfter(PixelGoldenCase target, PixelGoldenWindowRig window, ShellFrame first, bool expectDegraded)
    {
        var fresh = PixelGoldenShellRenderer.Render(target);
        Assert.Equal(expectDegraded, window.LastComposed.Degraded);
        Assert.Equal(fresh.Image, first.Image); // geometry is right from the very first frame
        Assert.Equal(fresh.Viewport, first.Viewport);
        var settled = first;
        if (expectDegraded)
        {
            // Linear on a >4x shrink: measured 45.4 dB against the settled frame (pinned 5 dB below).
            var all = new FrameRect(0, 0, first.Frame.Width, first.Frame.Height);
            var degradedPsnr = PixelMetrics.Psnr(fresh.Frame, first.Frame, all);
            output.WriteLine($"{target.Name}: degraded first frame vs settled: PSNR {degradedPsnr:F1} dB");
            Assert.True(degradedPsnr >= 40.0, "the degraded first frame is too far from the settled one");
            window.Settle();
            settled = window.Frame();
            Assert.False(window.LastComposed.Degraded);
        }

        AssertSameFrame(fresh.Frame, settled.Frame, "settled frame after the change vs a window that was in that state all along",
            target.Expectation == PixelGoldenExpectation.Fit);
        PixelGoldenTests.Verify(target, settled, new NullOutput());
    }
    /// <summary>
    /// WP28B-WARP-EDGE. Two draws of the SAME bytes with the SAME parameters on a WARP device are not always the same bytes when the
    /// picture is scaled with the high quality filter (HighQualityCubic = D2D's multi-pass scale effect): the first draw of a
    /// geometry in a process that has drawn another scaled geometry before can differ from every later draw of it by 1 level in one
    /// channel on single pixels of the columns x = W/2 - 1 and x = W - 1 (the seam of the two halves and the last column). Measured on
    /// 600x400 (RawPreviewToFullDecode, Fit), 15 process histories: 0, 55, 64, 71, 163, 292 px (0.12 % at most), max delta 1, columns
    /// 299 and 599 only; every later draw is byte identical again. It does not come from the product (same inputs, no clock, no
    /// shared state: the Ids, the cache and the time are fixed) and it is not the first-frame Linear policy (the frame is HQ,
    /// <c>Degraded</c> is false). 1:1 copies (100 %, NearestNeighbor) never show it and stay byte exact.
    /// So a scaled frame is "the same" within 1 level on at most two full columns of pixels (the seam + the last column: 0.33 % of
    /// 600x400). Anything a real defect does is far outside both numbers: Linear instead of HQ, another size, a shifted box or
    /// another picture differ by 2+ levels on a large share of the frame (PSNR 45 dB between the two filters = ~1.4 levels RMS).
    /// </summary>
    internal const int WarpScaledMaxDelta = 1;

    private static int WarpScaledMaxPixels(PixelFrame frame) => 2 * frame.Height;
    private static void AssertSameFrame(PixelFrame expected, PixelFrame actual, string what, bool scaledWithHqFilter = false)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var differing = 0;
        (int X, int Y)? first = null;
        var max = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var e = expected.At(x, y);
                var a = actual.At(x, y);
                var d = Math.Max(Math.Abs(e.B - a.B), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.R - a.R)));
                if (d > 0)
                {
                    differing++;
                    first ??= (x, y);
                    max = Math.Max(max, d);
                }
            }
        }

        var allowed = scaledWithHqFilter ? WarpScaledMaxPixels(expected) : 0;
        var allowedDelta = scaledWithHqFilter ? WarpScaledMaxDelta : 0;
        Assert.True(differing <= allowed && max <= allowedDelta,
            $"{what}: {differing} px differ (max delta {max}, first at {first}); allowed {allowed} px with delta <= {allowedDelta}");
    }

    private sealed class NullOutput : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }

    [Theory]
    [InlineData("f11-screen-fit-landscape", 1200, 800, false)]
    [InlineData("f11-screen-fit-portrait", 800, 1200, true)] // 14x shrink: Linear first, high quality after settling
    public void F11_FirstFrame_IsTheFitOfTheFakeScreen_NotAnIntermediateSize(string target, int iw, int ih, bool expectDegraded)
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, iw, ih));
        var before = window.Frame();
        Assert.Equal(480, before.Frame.Width);

        window.Transition(PixelGoldenExtCatalog.ScreenWidthPixels, PixelGoldenExtCatalog.ScreenHeightPixels, 1.0, fullscreen: true);
        var first = window.Frame();

        Assert.True(window.Viewer.IsFullscreen);
        Assert.True(window.Viewer.IsFit);
        Assert.Equal((PixelGoldenExtCatalog.ScreenWidthPixels, PixelGoldenExtCatalog.ScreenHeightPixels), (first.Frame.Width, first.Frame.Height));
        AssertFirstFrameAfter(PixelGoldenExtCatalog.Frame(target), window, first, expectDegraded);
    }

    [Fact]
    public void F11_At100Percent_KeepsTheOriginAndDrawsTheSourceOneToOneOnTheFakeScreen()
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, 1200, 800, zoom: 100));
        window.Frame();

        window.Transition(PixelGoldenExtCatalog.ScreenWidthPixels, PixelGoldenExtCatalog.ScreenHeightPixels, 1.0, fullscreen: true);
        var first = window.Frame();

        AssertFirstFrameAfter(PixelGoldenExtCatalog.Frame("f11-screen-z100-origin"), window, first, false);
    }

    [Fact]
    public void F11Exit_ReturnsToTheFrameOfBeforeF11_ByteForByte()
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, 1200, 800));
        var before = window.Frame();
        window.Transition(720, 480, 1.0, fullscreen: true);
        window.Frame();

        window.Transition(480, 320, 1.0, fullscreen: false);
        var firstAfter = window.Frame();

        Assert.False(window.Viewer.IsFullscreen);
        Assert.True(window.LastComposed.Degraded, "720x480 -> 480x320 shrinks the 1200x800 picture 6x in area while it moves");
        window.Settle();
        var after = window.Frame();
        Assert.False(window.LastComposed.Degraded);
        Assert.Equal(before.Image, firstAfter.Image);
        AssertSameFrame(before.Frame, after.Frame, "settled frame after F11 exit vs before F11", scaledWithHqFilter: true);
        Assert.Equal(before.Image, after.Image);
    }

    [Fact]
    public void DpiChangeInF11_WithTheSamePixels_RescalesTheSurfaceAndRefitsTheViewport()
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, 1200, 800));
        window.Transition(720, 480, 1.0, fullscreen: true);
        window.Frame();
        Assert.Equal(1.0, window.SurfaceDpi);

        // 100 % -> 150 % monitor: 720x480 px stay, the DIP size becomes 480x320.
        window.Transition(480, 320, 1.5);
        var first = window.Frame();

        Assert.Equal((720, 480), window.SurfacePixels);
        Assert.Equal(1.5, window.SurfaceDpi); // same pixels, new scale: WP25-SURFACE-DPI
        Assert.Equal(1.5, window.Viewer.DpiScale);
        AssertFirstFrameAfter(PixelGoldenExtCatalog.Frame("f11-dpi150-samepx-fit"), window, first, false);
    }

    [Fact]
    public void DpiChangeInF11_WithOtherPixels_ResizesTheSurfaceAndRefitsTheViewport()
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, 1200, 800));
        window.Transition(720, 480, 1.0, fullscreen: true);
        window.Frame();

        // The window moves to a 150 % monitor of 960x600 px: client 640x400 DIP.
        window.Transition(640, 400, 1.5);
        var first = window.Frame();

        Assert.Equal((960, 600), window.SurfacePixels);
        AssertFirstFrameAfter(PixelGoldenExtCatalog.Frame("f11-dpi150-otherpx-fit"), window, first, false);
    }

    [Fact]
    public void DpiChangeInF11_At100Percent_StillCopiesTheSourceToWholePixels()
    {
        using var window = new PixelGoldenWindowRig(Start(PixelGoldenScenes.Photo, 1200, 800, zoom: 100));
        window.Transition(720, 480, 1.0, fullscreen: true);
        window.Frame();

        window.Transition(480, 320, 1.5);
        var first = window.Frame();

        AssertFirstFrameAfter(PixelGoldenExtCatalog.Frame("f11-dpi150-samepx-z100-origin"), window, first, false);
    }

    // ---- RAW: bitmap pixel size changes under a fixed layout --------------------------------------------------

    [Theory]
    [InlineData("rawsize-fit-preview", "rawsize-fit-full")]
    [InlineData("rawsize-z100-preview", "rawsize-z100-full")]
    public void RawPreviewToFullDecode_DoesNotMoveTheLayout_AndEachBitmapMatchesItsGolden(string previewName, string fullName)
    {
        var preview = PixelGoldenExtCatalog.Frame(previewName);
        var full = PixelGoldenExtCatalog.Frame(fullName);
        using var window = new PixelGoldenWindowRig(preview);

        var first = window.Frame();
        window.ReplaceBitmap(full.ShownWidth, full.ShownHeight);
        var second = window.Frame();

        // Layout is the original's: the picture box and viewport must not change when the bitmap is swapped (no jump).
        Assert.Equal(first.Image, second.Image);
        Assert.Equal(first.Viewport, second.Viewport);
        // Both frames are the golden of their own bitmap, and the swapped frame is what a fresh full-size window draws.
        PixelGoldenTests.Verify(preview, first, new NullOutput());
        PixelGoldenTests.Verify(full, second, new NullOutput());
        AssertSameFrame(PixelGoldenShellRenderer.Render(full).Frame, second.Frame, "swapped bitmap vs fresh full-size window",
            full.Expectation == PixelGoldenExpectation.Fit);
        // Same picture at two resolutions: the frames are alike (the preview is only blurrier), never a different image.
        var all = new FrameRect(1, 1, first.Frame.Width - 2, first.Frame.Height - 2);
        Assert.True(PixelMetrics.Psnr(first.Frame, second.Frame, all) >= 22.0,
            $"preview and full frames differ too much: {PixelMetrics.Psnr(first.Frame, second.Frame, all):F1} dB");
    }

    [Fact]
    public void Catalog_ExtensionCoversTheFourAreasOfTheCard()
    {
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Selected == PixelGoldenCompareSelection.Left);
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Selected == PixelGoldenCompareSelection.Right);
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Selected == PixelGoldenCompareSelection.None);
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Left is { } l && c.Right is { } r && l.ExifOrientation != r.ExifOrientation);
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Dpi > 1.0);
        Assert.Contains(PixelGoldenExtCatalog.Compare, c => c.Right is null);
        Assert.Contains(PixelGoldenExtCatalog.Frames, c => c.Name.StartsWith("f11-", StringComparison.Ordinal) && c.Dpi == 1.0
            && c.PixelWidth == PixelGoldenExtCatalog.ScreenWidthPixels);
        Assert.Contains(PixelGoldenExtCatalog.Frames, c => c.Name.StartsWith("f11-dpi150", StringComparison.Ordinal) && c.Dpi == 1.5);
        Assert.Contains(PixelGoldenExtCatalog.Frames, c => c.BitmapWidth > 0 && c.BitmapWidth < c.ImageWidth);
        Assert.Equal(PixelGoldenExtCatalog.GoldenNames.Count(), PixelGoldenExtCatalog.GoldenNames.Distinct(StringComparer.Ordinal).Count());
        // None of the extension frames needs a screen bigger than the CI runner's (1024x768) once the ~20 px of invisible frame is added.
        Assert.All(PixelGoldenExtCatalog.Frames, c => Assert.True(c.PixelWidth <= 960 && c.PixelHeight <= 600));
    }
}
