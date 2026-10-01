using System.Globalization;
using System.Windows.Media;
using PhotoReview.App.Converters;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PhotoReview.App.Tests;

/// <summary>
/// RV-T30: the three XAML value converters of the main window. They create WPF brushes, so each test runs its body on a
/// fresh STA thread (UI category, serialized with the other global-state tests).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ConvertersTests
{
    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The converter test thread did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(ViewerStretchMode.Uniform, Stretch.Uniform)]
    [InlineData(ViewerStretchMode.None, Stretch.Fill)]
    public void ViewerStretchModeConverter_Convert_MapsUniformToUniformAndEverythingElseToFill(ViewerStretchMode mode, Stretch expected)
    {
        var result = new ViewerStretchModeConverter().Convert(mode, typeof(Stretch), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData(1)]
    [InlineData(null)]
    public void ViewerStretchModeConverter_Convert_NonEnumValue_FallsBackToFill(object? value)
    {
        var result = new ViewerStretchModeConverter().Convert(value!, typeof(Stretch), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Stretch.Fill, result);
    }

    [Fact]
    public void ViewerStretchModeConverter_ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            new ViewerStretchModeConverter().ConvertBack(Stretch.Uniform, typeof(ViewerStretchMode), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CompareBorderBrushConverter_Convert_TrueIsGreenAndFalseOrNullOrNonBoolIsDarkGray()
    {
        RunSta(() =>
        {
            var converter = new CompareBorderBrushConverter();

            var selected = Assert.IsAssignableFrom<SolidColorBrush>(converter.Convert(true, typeof(Brush), null!, CultureInfo.InvariantCulture));
            var unselected = Assert.IsAssignableFrom<SolidColorBrush>(converter.Convert(false, typeof(Brush), null!, CultureInfo.InvariantCulture));
            var forNull = Assert.IsAssignableFrom<SolidColorBrush>(converter.Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture));
            var forText = Assert.IsAssignableFrom<SolidColorBrush>(converter.Convert("true", typeof(Brush), null!, CultureInfo.InvariantCulture));

            // The brushes are process-wide statics: they must be frozen, or only the thread that first touched the
            // converter type may read them (the test run order then decides whether this test can read `.Color`).
            Assert.True(selected.IsFrozen);
            Assert.True(unselected.IsFrozen);
            Assert.Equal(Colors.LimeGreen, selected.Color);
            Assert.Equal(Color.FromRgb(0x55, 0x55, 0x55), unselected.Color);
            Assert.Same(unselected, forNull);
            Assert.Same(unselected, forText);
        });
    }

    [Fact]
    public void CompareBorderBrushConverter_ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            new CompareBorderBrushConverter().ConvertBack(Brushes.Red, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(ScalingQuality.Linear, BitmapScalingMode.Linear)]
    [InlineData(ScalingQuality.HighQuality, BitmapScalingMode.HighQuality)]
    public void ScalingQualityConverter_Convert_MapsLinearToLinearAndHighQualityToHighQuality(ScalingQuality quality, BitmapScalingMode expected)
    {
        var result = new ScalingQualityConverter().Convert(quality, typeof(BitmapScalingMode), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Linear")]
    [InlineData(0)]
    public void ScalingQualityConverter_Convert_NullOrNonEnumValue_UsesHighQuality(object? value)
    {
        var result = new ScalingQualityConverter().Convert(value!, typeof(BitmapScalingMode), null!, CultureInfo.InvariantCulture);

        Assert.Equal(BitmapScalingMode.HighQuality, result);
    }

    [Fact]
    public void ScalingQualityConverter_ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            new ScalingQualityConverter().ConvertBack(BitmapScalingMode.Linear, typeof(ScalingQuality), null!, CultureInfo.InvariantCulture));
    }
}
