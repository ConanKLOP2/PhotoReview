using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Input;

/// <summary>C-06 primitives: <see cref="RectD.Contains"/> keeps <see cref="Rect.Contains(Point)"/> semantics, and the WPF adapter is lossless.</summary>
public class InputPrimitivesTests
{
    [Fact(DisplayName = "RectD.Contains agrees with System.Windows.Rect.Contains on edges, corners and zero-size rectangles; a negative size is empty")]
    public void Contains_MatchesWpfRect()
    {
        double[] origins = [-5, 0, 10.5];
        double[] sizes = [0, 0.5, 20];
        double[] probes = [-6, -5, -0.0001, 0, 5, 10.5, 15.5, 30.5, 31, 100];
        var checkedCount = 0;
        foreach (var x in origins)
        foreach (var y in origins)
        foreach (var w in sizes)
        foreach (var h in sizes)
        foreach (var px in probes)
        foreach (var py in probes)
        {
            var expected = new Rect(x, y, w, h).Contains(new Point(px, py));
            var actual = new RectD(x, y, w, h).Contains(new PointD(px, py));
            Assert.True(expected == actual, $"rect ({x},{y},{w},{h}) point ({px},{py})");
            checkedCount++;
        }

        Assert.True(checkedCount > 2000);
    }

    [Theory(DisplayName = "A RectD with a negative width or height is empty: it contains no point (WPF Rect.Empty)")]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(-1, -1)]
    public void Contains_NegativeSize_IsEmpty(double width, double height)
    {
        Assert.False(new RectD(0, 0, width, height).Contains(new PointD(0, 0)));
        Assert.False(new RectD(0, 0, width, height).Contains(new PointD(-0.5, -0.5)));
    }

    [Fact(DisplayName = "RectD.Right and Bottom are the far edges")]
    public void RightAndBottom_AreTheFarEdges()
    {
        var rect = new RectD(10, 20, 30, 40);
        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
    }

    [Fact(DisplayName = "The WPF adapter keeps coordinates, and key / button / modifier numbers")]
    public void WpfInputAdapters_AreLossless()
    {
        Assert.Equal(new PointD(1.25, -3.5), new Point(1.25, -3.5).ToPointD());
        Assert.Equal(new Point(1.25, -3.5), new PointD(1.25, -3.5).ToWpfPoint());
        Assert.Equal(KeyId.F11, Key.F11.ToKeyId());
        Assert.Equal(PointerButton.XButton2, MouseButton.XButton2.ToPointerButton());
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, (ModifierKeys.Control | ModifierKeys.Shift).ToKeyModifiers());
    }
}
