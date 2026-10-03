using System.Reflection;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// COM interfaces are called by vtable slot, so the declaration order must match wincodec.h; a wrong
/// order calls the wrong native method. Guards IWICBitmapSourceTransform (F-IMG-6).
/// </summary>
public class WicInteropVtableTests
{
    private static readonly string[] Expected = { "CopyPixels", "GetClosestSize", "GetClosestPixelFormat", "DoesSupportTransform" };

    [Fact]
    public void IWICBitmapSourceTransform_MethodOrder_MatchesWincodecHeader()
    {
        var order = typeof(IWICBitmapSourceTransform)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.MetadataToken)
            .Select(m => m.Name)
            .ToArray();

        Assert.Equal(Expected, order);
    }
}
