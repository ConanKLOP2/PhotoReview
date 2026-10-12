using System.Runtime.InteropServices;

namespace PhotoReview.App.Tests.Services;

/// <summary>WP-34: Get/SetWindowPlacement are [LibraryImport] over a blittable struct; the class stays the file schema, copied around each call.</summary>
public sealed class WindowPlacementNativeMappingTests
{
    private static WindowPlacementService.WindowPlacement Sample() => new()
    {
        Length = 44,
        Flags = 2,
        ShowCommand = 7,
        MinPosition = new WindowPlacementService.Point { X = -1, Y = -2 },
        MaxPosition = new WindowPlacementService.Point { X = -3, Y = -4 },
        NormalPosition = new WindowPlacementService.Rectangle { Left = 10, Top = 20, Right = 1210, Bottom = 820 },
    };

    [Fact(DisplayName = "WP-34: the native WINDOWPLACEMENT struct is 44 bytes, the size Length must carry")]
    public void NativeStruct_Is44Bytes() => Assert.Equal(44, Marshal.SizeOf<WindowPlacementService.NativeWindowPlacement>());

    [Fact(DisplayName = "WP-34: class -> native copies every field")]
    public void ToNative_CopiesEveryField()
    {
        var n = WindowPlacementService.ToNative(Sample());

        Assert.Equal((44, 2, 7), (n.Length, n.Flags, n.ShowCommand));
        Assert.Equal((-1, -2, -3, -4), (n.MinPosition.X, n.MinPosition.Y, n.MaxPosition.X, n.MaxPosition.Y));
        Assert.Equal((10, 20, 1210, 820), (n.NormalPosition.Left, n.NormalPosition.Top, n.NormalPosition.Right, n.NormalPosition.Bottom));
    }

    [Fact(DisplayName = "WP-34: native -> class copies every field back (the old [In, Out] behaviour)")]
    public void CopyFromNative_CopiesEveryField()
    {
        var target = new WindowPlacementService.WindowPlacement();

        WindowPlacementService.CopyFromNative(WindowPlacementService.ToNative(Sample()), target);

        var expected = Sample();
        Assert.Equal((expected.Length, expected.Flags, expected.ShowCommand), (target.Length, target.Flags, target.ShowCommand));
        Assert.Equal((expected.MinPosition.X, expected.MinPosition.Y), (target.MinPosition.X, target.MinPosition.Y));
        Assert.Equal((expected.MaxPosition.X, expected.MaxPosition.Y), (target.MaxPosition.X, target.MaxPosition.Y));
        Assert.Equal((10, 20, 1210, 820),
            (target.NormalPosition.Left, target.NormalPosition.Top, target.NormalPosition.Right, target.NormalPosition.Bottom));
    }
}
