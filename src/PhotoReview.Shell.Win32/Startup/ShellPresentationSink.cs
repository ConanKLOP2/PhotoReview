using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// Cầu nối giữa <c>ImagePresenter</c> (qua <see cref="IPresentationSink"/>, dựng trong composition root dùng chung TRƯỚC
/// khi cửa sổ tồn tại) và <see cref="ShellMainWindow"/> (đăng ký sau khi có). Một singleton trong DI. WP-21 thay phần vẽ
/// (<c>Win32PresentationSink</c> + lớp ảnh) bằng cách đăng ký lại <see cref="IPresentationSinkFactory"/> trong
/// <c>ShellCompositionRoot.RegisterFeatures</c>; hub này chỉ giữ ảnh hiện tại + các mốc đo.
/// </summary>
internal sealed class ShellPresentationHub
{
    private readonly List<(long Token, string Kind, long AssignedTimestamp)> _pendingTraces = [];
    private bool _currentImagePresented;

    /// <summary>Ảnh hiện tại (nền tảng = <c>PixelBuffer</c> với <c>PixelBufferImageCodec</c>); null = trống/Compare.</summary>
    public object? CurrentImage { get; private set; }

    /// <summary>Ảnh hiện tại vừa đổi (đã ở UI thread). <c>isFileChange</c> theo <see cref="IPresentationSink.SetCurrentImage"/>.</summary>
    public event Action<object?, bool>? ImageChanged;

    /// <summary>Số lần ảnh đổi (chẩn đoán/test).</summary>
    public int ImageChangeCount { get; private set; }

    internal void SetCurrentImage(object? image, bool isFileChange)
    {
        CurrentImage = image;
        _currentImagePresented = false;
        ImageChangeCount++;
        ImageChanged?.Invoke(image, isFileChange);
    }

    internal void AddTrace(long token, string kind, long assignedTimestamp)
    {
        if (_currentImagePresented)
        {
            // Ảnh vừa gán đã nằm trong một khung đã Present (ảnh đầu được vẽ ngay trong lượt gán, trước khi presenter kịp
            // đăng mốc): phát luôn, không chờ khung sau (có thể không bao giờ tới khi màn hình tĩnh).
            Emit(token, kind, assignedTimestamp);
            return;
        }

        // Một việc chờ mỗi token: cửa sổ thu nhỏ không bao giờ vẽ thì không được tích luỹ vô hạn.
        if (_pendingTraces.Count >= 16)
        {
            _pendingTraces.RemoveAt(0);
        }

        _pendingTraces.Add((token, kind, assignedTimestamp));
    }

    /// <summary>
    /// Một khung vừa được Present: các ảnh đã gán trước đó nay đã hiện (C-18: <c>RenderedFrame</c> của shell = Present của khung
    /// có ảnh trả về). Phát cùng tên mốc với bản WPF để dùng chung probe.
    /// </summary>
    /// <param name="containedImage">Khung vừa Present có vẽ ảnh hiện tại (khung chỉ có canvas không tính).</param>
    public void NoteFramePresented(bool containedImage = true)
    {
        if (!containedImage)
        {
            return;
        }

        _currentImagePresented = true;
        if (_pendingTraces.Count == 0)
        {
            return;
        }

        var traces = _pendingTraces.ToArray();
        _pendingTraces.Clear();
        foreach (var (token, kind, assigned) in traces)
        {
            Emit(token, kind, assigned);
        }
    }

    private static void Emit(long token, string kind, long assignedTimestamp)
    {
        if (!PhotoReviewPerf.Log.IsEnabled())
        {
            return;
        }

        PhotoReviewPerf.Log.Rendered(token, PhotoReviewPerf.Ms(assignedTimestamp));
        PhotoReviewPerf.Log.Presented(token, kind);
        PhotoReviewPerf.Log.RenderedFrame(token, PhotoReviewPerf.Ms(assignedTimestamp));
    }
}
