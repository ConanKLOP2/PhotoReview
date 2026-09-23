using PhotoReview.Imaging;

namespace PhotoReview.App.Services;

/// <summary>
/// AR02a (F3): production seam so the composition root can query the real viewport size for
/// <c>ApplyInitialViewMode</c> without <c>MainViewModelCompositionRoot</c> depending on
/// <see cref="MainWindow"/> directly. <see cref="MainWindow"/>'s DI constructor sets
/// <see cref="Get"/> to its own viewport accessor right after <c>InitializeComponent()</c>;
/// until then (and in any host that never wires it, e.g. a headless test), it returns (0, 0),
/// matching the previous hard-coded behaviour.
/// </summary>
public sealed class ViewportSizeSource
{
    // Width in the high 32 bits, height in the low 32 bits: one 64-bit volatile slot so a reader
    // never sees the width of one box paired with the height of another.
    private long _packedTargetDecodeBox;

    public Func<(double Width, double Height)> Get { get; set; } = static () => (0, 0);

    /// <summary>
    /// Preview decode box (width and height, device pixels) computed by <see cref="MainWindow"/>
    /// on the UI thread whenever the viewport or DPI changes. Read lock-free from any thread
    /// (preload workers build cache keys off the UI thread, so they must never touch WPF layout).
    /// <see cref="DecodeBox.Unbounded"/> = no window wired: decode at full size, the previous
    /// headless behaviour.
    /// </summary>
    public DecodeBox TargetDecodeBox
    {
        get
        {
            var packed = Volatile.Read(ref _packedTargetDecodeBox);
            return new DecodeBox((int)(packed >> 32), unchecked((int)packed));
        }
        set => Volatile.Write(ref _packedTargetDecodeBox, ((long)value.Width << 32) | (uint)value.Height);
    }
}
