using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// <see cref="WicDirectDecoder"/> factory creation (<c>CreateFactory</c>): a failing HRESULT surfaces as the mapped exception
/// (a <see cref="COMException"/> for an unknown code), which the decoder fallback chain treats as a fallbackable decoder failure.
/// </summary>
public sealed class WicDirectFactoryFailureTests
{
    private const int EFail = unchecked((int)0x80004005);
    private const int WincodecErrNotInitialized = unchecked((int)0x88982F0C);

    [Fact]
    public void ThrowIfFactoryFailed_FailureHresultWithNoFactory_ThrowsComExceptionWithThatHresult()
    {
        var ex = Assert.Throws<COMException>(() => WicDirectDecoder.ThrowIfFactoryFailed(WincodecErrNotInitialized, factoryCreated: false));

        Assert.Equal(WincodecErrNotInitialized, ex.HResult);
    }

    [Fact]
    public void ThrowIfFactoryFailed_FailureHresultEvenWhenAFactoryCameBack_StillThrows()
    {
        var ex = Assert.Throws<COMException>(() => WicDirectDecoder.ThrowIfFactoryFailed(EFail, factoryCreated: true));

        Assert.Equal(EFail, ex.HResult);
    }

    [Fact]
    public void ThrowIfFactoryFailed_SuccessWithAFactory_DoesNotThrow()
    {
        var exception = Record.Exception(() => WicDirectDecoder.ThrowIfFactoryFailed(0, factoryCreated: true));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfFactoryFailed_SuccessHresultButNoFactory_DoesNotThrowBecauseNoErrorCodeExistsToMap()
    {
        // Documents the contract: only an HRESULT can be mapped to an exception; S_OK with a null factory is left to the caller.
        var exception = Record.Exception(() => WicDirectDecoder.ThrowIfFactoryFailed(0, factoryCreated: false));

        Assert.Null(exception);
    }
}