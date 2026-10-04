using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// <see cref="WicDirectDecoder"/> factory creation (<c>CreateFactory</c>): a failing HRESULT surfaces as the mapped exception
/// (a <see cref="COMException"/> for an unknown code), which the decoder fallback chain treats as a fallbackable decoder failure.
/// </summary>
public sealed class WicDirectFactoryFailureTests
{
    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int CreateErrorInfo(out ICreateErrorInfo errorInfo);

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(int reserved, IntPtr errorInfo);

    [ComImport, Guid("22F03340-547D-101B-8E65-08002B2BD119"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateErrorInfo
    {
        void SetGUID(ref Guid rguid);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetHelpFileContext(uint context);
        void SetHelpFile([MarshalAs(UnmanagedType.LPWStr)] string helpFile);
    }

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

    [Fact]
    public void ThrowIfFactoryFailed_StaleErrorInfoOnThread_IsIgnored()
    {
        // A stale IErrorInfo left on the thread by an earlier COM call must not leak into the exception (test-order dependence).
        const string stale = "stale error info from an earlier COM call";
        var iidErrorInfo = new Guid("1CF2B120-547D-101B-8E65-08002B2BD119");
        var guid = Guid.NewGuid();
        Assert.Equal(0, CreateErrorInfo(out var create));
        create.SetGUID(ref guid);
        create.SetSource("stale");
        create.SetDescription(stale);
        var unknown = Marshal.GetIUnknownForObject(create);
        Assert.Equal(0, Marshal.QueryInterface(unknown, in iidErrorInfo, out var errorInfo));
        try
        {
            Assert.Equal(0, SetErrorInfo(0, errorInfo));

            var ex = Assert.Throws<COMException>(() => WicDirectDecoder.ThrowIfFactoryFailed(WincodecErrNotInitialized, factoryCreated: false));

            Assert.Equal(WincodecErrNotInitialized, ex.HResult);
            Assert.DoesNotContain(stale, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            _ = SetErrorInfo(0, IntPtr.Zero); // do not leave the stale object for other tests on this thread
            Marshal.Release(errorInfo);
            Marshal.Release(unknown);
        }
    }
}
