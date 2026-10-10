using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Input;

namespace PhotoReview.App.Services;

/// <summary>
/// C-06 adapter: WPF input types -> WPF-free input types of <c>PhotoReview.App.Input</c>. The numeric values are identical by
/// construction (<c>KeyId</c> is a 1:1 copy of <see cref="Key"/>, <see cref="PointerButton"/> of <see cref="MouseButton"/>,
/// <see cref="KeyModifiers"/> of <see cref="ModifierKeys"/>), which <c>ContractMirrorTests</c> and <c>WpfInputAdaptersTests</c> pin.
/// </summary>
internal static class WpfInputAdapters
{
    public static PointD ToPointD(this Point point) => new(point.X, point.Y);

    public static Point ToWpfPoint(this PointD point) => new(point.X, point.Y);

    public static KeyId ToKeyId(this Key key) => (KeyId)(int)key;

    public static PointerButton ToPointerButton(this MouseButton button) => (PointerButton)(int)button;

    public static KeyModifiers ToKeyModifiers(this ModifierKeys modifiers) => (KeyModifiers)(int)modifiers;
}
