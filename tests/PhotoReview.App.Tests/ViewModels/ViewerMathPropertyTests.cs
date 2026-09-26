using System;
using System.Collections.Generic;
using System.Linq;
using PhotoReview.App;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Property-style tests (seeded, reproducible: the seed is part of the theory row) for the zoom / Fit / pan math:
/// invariants that must hold for ANY input including NaN, infinities, zero-size viewports, tiny and huge images,
/// extreme aspect ratios and 100-300 % display scaling.
/// </summary>
public sealed class ViewerMathPropertyTests
{
    private const int Iterations = 400;

    public static TheoryData<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Mostly ordinary values, with a steady share of the values that break naive math.</summary>
    private static double Wild(Random rng, double scale = 5000)
    {
        switch (rng.Next(14))
        {
            case 0: return double.NaN;
            case 1: return double.PositiveInfinity;
            case 2: return double.NegativeInfinity;
            case 3: return 0;
            case 4: return -0.0;
            case 5: return double.MaxValue;
            case 6: return double.Epsilon;
            case 7: return -rng.NextDouble() * scale;
            default: return rng.NextDouble() * scale;
        }
    }

    private static double Finite(Random rng, double scale = 5000) => rng.NextDouble() * scale;

    // ---- pan / zoom offsets ----

    [Theory]
    [MemberData(nameof(Seeds))]
    public void PanOffsets_AlwaysFiniteAndInsideTheScrollableRange(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var extentW = Finite(rng, 20000);
            var extentH = Finite(rng, 20000);
            var viewW = Finite(rng, 4000);
            var viewH = Finite(rng, 4000);

            var o = MainWindowHelpers.CalculatePanOffsets(Wild(rng), Wild(rng), Wild(rng), Wild(rng), extentW, extentH, viewW, viewH);

            AssertWithinRange(o, extentW, extentH, viewW, viewH);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void OffsetClamp_WithNonFiniteExtentOrViewport_NeverEscapesOrProducesNaN(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var extentW = Wild(rng);
            var extentH = Wild(rng);
            var viewW = Wild(rng);
            var viewH = Wild(rng);
            var o = MainWindowHelpers.CalculatePanOffsets(Finite(rng), Finite(rng), Finite(rng, 200), Finite(rng, 200), extentW, extentH, viewW, viewH);

            Assert.True(double.IsFinite(o.Horizontal) && double.IsFinite(o.Vertical), $"non-finite offset {o}");
            // A non-finite scrollable range is unusable: the only safe offset is the origin, never an unclamped value.
            Assert.InRange(o.Horizontal, 0, ScrollableRange(extentW, viewW));
            Assert.InRange(o.Vertical, 0, ScrollableRange(extentH, viewH));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZoomViewportOffsets_AlwaysFiniteAndInsideTheScrollableRange(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var extentW = Finite(rng, 20000);
            var extentH = Finite(rng, 20000);
            var viewW = Finite(rng, 4000);
            var viewH = Finite(rng, 4000);

            var o = MainWindowHelpers.CalculateZoomViewportOffsets(
                Wild(rng, 8), Wild(rng, 8), Wild(rng), Wild(rng), Wild(rng), Wild(rng), extentW, extentH, viewW, viewH);

            AssertWithinRange(o, extentW, extentH, viewW, viewH);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZoomViewportOffsets_KeepThePointUnderTheMouse_WhenNothingIsClamped(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var oldZoom = 0.1 + rng.NextDouble() * 7.9;
            var newZoom = 0.1 + rng.NextDouble() * 7.9;
            var viewW = 200 + Finite(rng, 1800);
            var viewH = 200 + Finite(rng, 1000);
            var mouseX = Finite(rng, viewW);
            var mouseY = Finite(rng, viewH);
            var oldH = Finite(rng, 3000);
            var oldV = Finite(rng, 3000);
            var extentW = 1_000_000; // never clamps at the far edge
            var extentH = 1_000_000;

            var o = MainWindowHelpers.CalculateZoomViewportOffsets(oldZoom, newZoom, mouseX, mouseY, oldH, oldV, extentW, extentH, viewW, viewH);

            var wantH = (oldH + mouseX) * newZoom / oldZoom - mouseX;
            var wantV = (oldV + mouseY) * newZoom / oldZoom - mouseY;
            if (wantH < 0 || wantV < 0) continue; // clamped at the origin: the anchor cannot be kept there
            // Image coordinate (offset + mouse) / zoom under the mouse is unchanged.
            Assert.Equal((oldH + mouseX) / oldZoom, (o.Horizontal + mouseX) / newZoom, 6);
            Assert.Equal((oldV + mouseY) / oldZoom, (o.Vertical + mouseY) / newZoom, 6);
        }
    }

    [Fact]
    public void ZoomViewportOffsets_ZoomThereAndBack_ReturnsToTheStartingOffset()
    {
        var rng = new Random(11);
        for (var i = 0; i < Iterations; i++)
        {
            var zoom = 0.25 + rng.NextDouble() * 3;
            var factor = 1.1 + rng.NextDouble() * 3;
            var mouseX = Finite(rng, 1000);
            var mouseY = Finite(rng, 800);
            var startH = Finite(rng, 2000);
            var startV = Finite(rng, 2000);

            var zoomed = MainWindowHelpers.CalculateZoomViewportOffsets(zoom, zoom * factor, mouseX, mouseY, startH, startV, 1e7, 1e7, 1000, 800);
            var back = MainWindowHelpers.CalculateZoomViewportOffsets(zoom * factor, zoom, mouseX, mouseY, zoomed.Horizontal, zoomed.Vertical, 1e7, 1e7, 1000, 800);

            Assert.Equal(startH, back.Horizontal, 6);
            Assert.Equal(startV, back.Vertical, 6);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void UniformImagePoint_StaysInsideTheSourceImage_ForAnyPointer(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var sourceW = 1 + Finite(rng, 100000);
            var sourceH = 1 + Finite(rng, 100000);
            var p = MainWindowHelpers.CalculateUniformImagePoint(
                Wild(rng), Wild(rng), sourceW, sourceH, Wild(rng, 8000), Wild(rng, 8000));

            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), $"non-finite point {p}");
            Assert.InRange(p.X, 0, sourceW);
            Assert.InRange(p.Y, 0, sourceH);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void UniformImagePoint_TheElementCenterMapsToTheSourceCenter(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var elementW = 1 + Finite(rng, 4000);
            var elementH = 1 + Finite(rng, 4000);
            var sourceW = 1 + Finite(rng, 100000);
            var sourceH = 1 + Finite(rng, 100000);

            var p = MainWindowHelpers.CalculateUniformImagePoint(elementW, elementH, sourceW, sourceH, elementW / 2, elementH / 2);

            Assert.Equal(sourceW / 2, p.X, 6);
            Assert.Equal(sourceH / 2, p.Y, 6);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NormalizeImagePoint_NeverReturnsNonFinite(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < Iterations; i++)
        {
            var p = MainWindowHelpers.NormalizeImagePoint(Wild(rng), Wild(rng), Wild(rng), Wild(rng));

            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), $"non-finite anchor {p}");
        }
    }

    private static double ScrollableRange(double extent, double viewport) =>
        double.IsFinite(extent - viewport) ? Math.Max(0, extent - viewport) : 0;

    private static void AssertWithinRange(MainWindowHelpers.ZoomViewportOffsets o, double extentW, double extentH, double viewW, double viewH)
    {
        Assert.True(double.IsFinite(o.Horizontal) && double.IsFinite(o.Vertical), $"non-finite offset {o}");
        Assert.InRange(o.Horizontal, 0, Math.Max(0, extentW - viewW));
        Assert.InRange(o.Vertical, 0, Math.Max(0, extentH - viewH));
    }

    // ---- ViewerState ----

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ViewerState_RandomOperationSequence_KeepsEveryInvariant(int seed)
    {
        var rng = new Random(seed);
        var state = new ViewerState();
        var events = 0;
        state.ZoomModeChanged += (_, _) => events++;

        for (var step = 0; step < 600; step++)
        {
            // The zoom actually on screen: in Fit that is FitZoom, not the conventional Zoom = 1.0 (StepZoom starts there).
            var before = state.IsFit && state.FitZoom > 0 ? state.FitZoom : state.Zoom;
            var op = rng.Next(11);
            switch (op)
            {
                case 0:
                    state.ZoomIn();
                    if (!state.IsFit) Assert.True(state.Zoom >= before || before > ViewerState.MaxStepZoom, $"ZoomIn lowered {before} -> {state.Zoom}");
                    break;
                case 1:
                    state.ZoomOut();
                    if (!state.IsFit) Assert.True(state.Zoom <= before || before < ViewerState.MinStepZoom, $"ZoomOut raised {before} -> {state.Zoom}");
                    break;
                case 2: state.WheelZoom(rng.Next(-240, 241)); break;
                case 3: state.SetZoom(Wild(rng, 12)); break;
                case 4: state.ResetFit(Wild(rng), Wild(rng)); break;
                case 5: state.SetSourceSize(rng.Next(-5, 200_000), rng.Next(-5, 200_000)); break;
                case 6: state.DpiScale = Wild(rng, 4); break;
                case 7: state.UpdateViewport(Wild(rng), Wild(rng), force: rng.Next(2) == 0); break;
                case 8: state.ZoomToActualSize(); break;
                case 9: state.ApplyInitialViewMode((InitialViewMode)rng.Next(0, 4), Wild(rng), Wild(rng)); break;
                default: state.SetSourceSize(1 + rng.Next(64), 1 + rng.Next(64)); break;
            }

            AssertInvariants(state, $"seed {seed} step {step} op {op}");
        }
    }

    private static void AssertInvariants(ViewerState state, string context)
    {
        Assert.True(double.IsFinite(state.Zoom), $"{context}: Zoom = {state.Zoom}");
        Assert.InRange(state.Zoom, ViewerState.MinZoom, ViewerState.MaxZoom);
        if (state.IsFit)
        {
            Assert.Null(state.EffectiveZoom);
            Assert.True(double.IsNaN(state.ImageWidth) && double.IsNaN(state.ImageHeight), $"{context}: Fit must leave the element auto-sized");
        }
        else
        {
            Assert.Equal(state.Zoom, state.EffectiveZoom);
            if (state.SourcePixelWidth > 0 && state.SourcePixelHeight > 0)
            {
                Assert.True(double.IsFinite(state.ImageWidth) && state.ImageWidth > 0, $"{context}: ImageWidth = {state.ImageWidth}");
                Assert.True(double.IsFinite(state.ImageHeight) && state.ImageHeight > 0, $"{context}: ImageHeight = {state.ImageHeight}");
            }
        }
        Assert.True(double.IsFinite(state.FitZoom) && state.FitZoom >= 0, $"{context}: FitZoom = {state.FitZoom}");
        Assert.False(double.IsNaN(state.MaxImageWidth) || double.IsNaN(state.MaxImageHeight), $"{context}: NaN viewport limit");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1e300)]
    public void SetZoom_WithUnusableValue_StaysWithinTheAllowedRange(double value)
    {
        var state = new ViewerState();
        state.SetSourceSize(4000, 3000);
        state.SetZoom(2.0);

        state.SetZoom(value);

        Assert.True(double.IsFinite(state.Zoom));
        Assert.InRange(state.Zoom, ViewerState.MinZoom, ViewerState.MaxZoom);
        Assert.True(double.IsFinite(state.ImageWidth) && double.IsFinite(state.ImageHeight));
    }

    [Fact]
    public void ZoomIn_ThenZoomOut_FromAnyStepZoom_IsMonotonicUntilTheLimits()
    {
        var state = new ViewerState();
        state.SetZoom(ViewerState.MinStepZoom);
        var previous = state.Zoom;
        var ascending = new List<double> { previous };
        for (var i = 0; i < 40; i++)
        {
            state.ZoomIn();
            Assert.True(state.Zoom >= previous, $"ZoomIn went down {previous} -> {state.Zoom}");
            previous = state.Zoom;
            ascending.Add(previous);
        }
        Assert.Equal(ViewerState.MaxStepZoom, state.Zoom);
        Assert.Equal(ascending.Distinct().Count(), ascending.Distinct().OrderBy(z => z).Count());

        for (var i = 0; i < 40; i++)
        {
            state.ZoomOut();
            Assert.True(state.Zoom <= previous, $"ZoomOut went up {previous} -> {state.Zoom}");
            previous = state.Zoom;
        }
        Assert.Equal(ViewerState.MinStepZoom, state.Zoom);
    }

    [Theory]
    [InlineData(1, 1, 1920, 1080, 1.0)]
    [InlineData(1, 100000, 1920, 1080, 1.0)]
    [InlineData(100000, 1, 1920, 1080, 1.0)]
    [InlineData(100000, 100000, 1920, 1080, 1.0)]
    [InlineData(6000, 4000, 1920, 1080, 1.0)]
    [InlineData(6000, 4000, 1920, 1080, 2.5)]
    [InlineData(6000, 4000, 1920, 1080, 3.0)]
    [InlineData(1, 1, 3, 3, 3.0)]
    public void FitZoom_TouchesOneViewportEdgeAtEveryDisplayScale(int width, int height, double viewportW, double viewportH, double dpi)
    {
        var state = new ViewerState { DpiScale = dpi };
        state.SetSourceSize(width, height);
        state.ResetFit(viewportW, viewportH);

        var (displayW, displayH) = ViewerState.CalculateDisplaySize(width, height, state.FitZoom, dpi);

        // Image never larger than the viewport and touches it on the limiting axis (aspect ratio preserved).
        Assert.True(displayW <= viewportW + 1e-6 && displayH <= viewportH + 1e-6, $"{displayW}x{displayH} exceeds {viewportW}x{viewportH}");
        Assert.True(Math.Abs(displayW - viewportW) < 1e-6 || Math.Abs(displayH - viewportH) < 1e-6, $"{displayW}x{displayH} touches neither edge of {viewportW}x{viewportH}");
        Assert.Equal((double)width / height, displayW / displayH, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 800)]
    [InlineData(1200, 0)]
    [InlineData(1, 1)]
    [InlineData(-5, -5)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, 800)]
    public void FitZoom_WithUnusableViewport_IsZeroMeaningUnknown_NeverNaNOrInfinity(double viewportW, double viewportH)
    {
        var state = new ViewerState();
        state.SetSourceSize(4000, 3000);

        state.ResetFit(viewportW, viewportH);

        Assert.Equal(0, state.FitZoom);
        Assert.True(state.IsFit);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void ActualSize_IsOneSourcePixelPerDevicePixel_AtEveryDisplayScale(double dpi)
    {
        var state = new ViewerState { DpiScale = dpi };
        state.SetSourceSize(4000, 3000);

        state.ZoomToActualSize();

        Assert.Equal(4000 / dpi, state.ImageWidth, 6);
        Assert.Equal(3000 / dpi, state.ImageHeight, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.PositiveInfinity)]
    public void DisplaySize_WithBrokenDpi_FallsBackToUnscaled(double dpi)
    {
        var (w, h) = ViewerState.CalculateDisplaySize(4000, 3000, 1.0, dpi);

        Assert.Equal(4000, w, 6);
        Assert.Equal(3000, h, 6);
    }
}
