using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// IMG-04: only a failure while evaluating the lazily-run colour transform (CopyPixels) is an ICC
/// failure; the guard must not be active for any other COM error.
/// </summary>
public sealed class WicIccDiagnosticsTests
{
    [Fact]
    public void CopyPixelsGuarded_TransformActive_ReportsIccFailureAsFallbackable()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            WicDirectDecoder.CopyPixelsGuarded(Throwing, colorTransformActive: true));
        Assert.Contains("ICC", ex.Message, StringComparison.Ordinal);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public void CopyPixelsGuarded_TransformInactive_LeavesComFailureUntouched()
    {
        var ex = Assert.Throws<COMException>(() =>
            WicDirectDecoder.CopyPixelsGuarded(Throwing, colorTransformActive: false));
        Assert.DoesNotContain("ICC", ex.Message, StringComparison.Ordinal);
    }

    // CA2201: a COMException is exactly what a failing WIC call raises; that is the type under test.
#pragma warning disable CA2201
    private static void Throwing() =>
        throw new COMException("converter exploded", unchecked((int)0x88982F50));
#pragma warning restore CA2201
}
