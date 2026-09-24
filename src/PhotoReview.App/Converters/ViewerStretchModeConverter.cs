using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PhotoReview.App.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace PhotoReview.App.Converters;

/// <summary>
/// Chuyển đổi ViewerStretchMode sang System.Windows.Media.Stretch trong WPF.
/// </summary>
/// <remarks>
/// feat(zoom): outside Fit (<see cref="ViewerStretchMode.None"/> = "no viewport fitting") the image
/// element gets an explicit original-relative size (<see cref="ViewerState.ImageWidth"/>), and the
/// bitmap -- preview or full-resolution decode -- is stretched to fill exactly that size, so swapping
/// one for the other never changes layout. While the size is unknown (NaN), Fill with an unbounded
/// ScrollViewer slot measures to the bitmap's natural size, i.e. the old Stretch.None behaviour.
/// </remarks>
public sealed class ViewerStretchModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewerStretchMode mode)
        {
            return mode == ViewerStretchMode.Uniform
                ? System.Windows.Media.Stretch.Uniform
                : System.Windows.Media.Stretch.Fill;
        }

        return System.Windows.Media.Stretch.Fill;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Chuyển đổi cờ IsLeftSelected / IsRightSelected thành màu viền cho chế độ Compare.
/// </summary>
public sealed class CompareBorderBrushConverter : IValueConverter
{
    private static readonly Brush SelectedBrush = Brushes.LimeGreen;
    private static readonly Brush UnselectedBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? SelectedBrush : UnselectedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Chuyển đổi ScalingQuality sang BitmapScalingMode của WPF.
/// </summary>
public sealed class ScalingQualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PhotoReview.Core.Model.ScalingQuality.Linear
            ? System.Windows.Media.BitmapScalingMode.Linear
            : System.Windows.Media.BitmapScalingMode.HighQuality;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
