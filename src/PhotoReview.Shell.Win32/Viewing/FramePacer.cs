namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Cách <c>Present</c> của một khung.</summary>
internal enum PresentMode
{
    /// <summary><c>Present(0)</c>: trả về ngay, DWM lấy khung mới nhất ở lần ghép kế tiếp (windowed flip-model: không xé hình).</summary>
    Immediate = 0,

    /// <summary><c>Present(1)</c>: chờ vblank (chống xé hình ở chế độ phủ cả màn hình), có thể chặn UI thread.</summary>
    VSync = 1,
}

/// <summary>
/// WP-21: chính sách nhịp khung - làm sao <c>Present</c> không bao giờ chặn UI thread vài trăm ms.
/// <para>
/// Số đo trên máy dev (GPU thật, hybrid Intel/NVIDIA, cửa sổ thường trên desktop tương tác; NOWPF-WP21): mọi
/// <c>Present(1)</c> chặn P50 ~222 ms (P95 tới 700 ms; cả pan, crossfade, zoom), còn <c>Present(0)</c> ~1,2 ms; chờ waitable object của
/// swap chain cũng ~220 ms (DWM trả buffer rất chậm). Vì vậy: cửa sổ thường (DWM luôn ghép trọn khung, <c>Present(0)</c> không xé hình)
/// dùng <see cref="PresentMode.Immediate"/>; chỉ khi phủ cả màn hình (F11: có thể thành independent flip, mới xé) mới dùng
/// <see cref="PresentMode.VSync"/> kèm chờ waitable không chặn (bỏ khung nếu chưa sẵn sàng, tối đa
/// <see cref="MaxDeferredFrames"/> lần liên tiếp). VSync mà vẫn chặn &gt;= <see cref="BlockThresholdMs"/> ms
/// <see cref="BlockedFramesToDegrade"/> khung liên tiếp thì hạ cấp vĩnh viễn (phiên) xuống Immediate.
/// </para>
/// Chỉ dùng trên UI thread.
/// </summary>
internal sealed class FramePacer
{
    /// <summary>Present mất chừng này (ms) trở lên được coi là bị chặn.</summary>
    internal const double BlockThresholdMs = 40.0;

    internal const int BlockedFramesToDegrade = 2;

    /// <summary>Số khung liên tiếp tối đa được hoãn vì swap chain chưa sẵn sàng (sau đó vẽ luôn).</summary>
    internal const int MaxDeferredFrames = 3;

    private int _consecutiveBlocked;
    private int _deferred;

    /// <summary>Đã hạ cấp vĩnh viễn xuống Immediate vì VSync chặn UI thread.</summary>
    internal bool IsDegraded { get; private set; }

    /// <summary>Số khung đã hoãn (chẩn đoán/test).</summary>
    internal long DeferredTotal { get; private set; }

    /// <summary>Phát khi hạ cấp (log một lần).</summary>
    internal event EventHandler? Degraded;

    /// <summary>
    /// Mode cho khung kế tiếp. <paramref name="awaitingFirstImage"/>: khung ảnh đầu chưa vẽ - <c>Present(1)</c> của nó chặn 250-500 ms
    /// trên GPU thật ngay sau khi bỏ cloak (NOWPF-WP20), nên luôn Immediate.
    /// </summary>
    internal PresentMode ModeFor(bool fullscreen, bool awaitingFirstImage)
    {
        if (IsDegraded || awaitingFirstImage || !fullscreen)
        {
            return PresentMode.Immediate;
        }

        return PresentMode.VSync;
    }

    /// <summary>
    /// Swap chain chưa nhận được khung mới (waitable chưa báo: DWM chưa lấy khung trước, hàng đợi đầy) thì hoãn khung này (người
    /// gọi Invalidate lại, vẽ ở nhịp sau) thay vì để <c>EndDraw/Present</c> chặn UI thread tới lần ghép kế của DWM - đo được
    /// 2 x 222 ms trên máy dev. Hoãn tối đa <see cref="MaxDeferredFrames"/> khung liên tiếp (cửa sổ bị che/thu nhỏ không bao giờ
    /// báo sẵn sàng), rồi vẽ luôn. Khung ảnh đầu không đi qua đây (người gọi bỏ qua).
    /// </summary>
    internal bool ShouldDefer(bool swapChainReady)
    {
        if (swapChainReady)
        {
            _deferred = 0;
            return false;
        }

        if (_deferred >= MaxDeferredFrames)
        {
            _deferred = 0;
            return false;
        }

        _deferred++;
        DeferredTotal++;
        return true;
    }

    /// <summary>Báo thời gian <c>EndDrawAndPresent</c> (ms) của một khung đã vẽ ở <paramref name="mode"/>.</summary>
    internal void ObservePresent(PresentMode mode, double presentMs)
    {
        if (mode != PresentMode.VSync)
        {
            _consecutiveBlocked = 0;
            return;
        }

        if (presentMs < BlockThresholdMs)
        {
            _consecutiveBlocked = 0;
            return;
        }

        if (++_consecutiveBlocked >= BlockedFramesToDegrade && !IsDegraded)
        {
            IsDegraded = true;
            Degraded?.Invoke(this, EventArgs.Empty);
        }
    }
}
