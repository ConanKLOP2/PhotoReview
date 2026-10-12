using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// Vẽ khung đồng bộ cho test. <c>RenderNow</c> KHÔNG bảo đảm có khung: khi swap chain chưa nhận khung trước (waitable chưa báo),
/// <c>FramePacer</c> hoãn khung đó (Invalidate, vẽ ở nhịp sau), tối đa <see cref="FramePacer.MaxDeferredFrames"/> lần liên tiếp. Một
/// <c>RenderNow</c> đơn lẻ rồi khẳng định ngay trạng thái do khung đó sinh ra (số khung, DeviceGeneration, thanh cuộn đã vẽ) là
/// phụ thuộc thời gian thật; test phải gọi lại tới khi ĐIỀU KIỆN đúng (cùng lượt UI).
/// </summary>
internal static class ShellFrames
{
    /// <summary>Gọi <c>RenderNow</c> (trên UI thread) tới khi <paramref name="done"/> đúng; tối đa số lần khung có thể bị hoãn + 1.</summary>
    public static bool RenderUntil(ShellMainWindow main, Func<bool> done)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(done);
        for (var attempt = 0; attempt <= FramePacer.MaxDeferredFrames; attempt++)
        {
            main.RenderNow();
            if (done())
            {
                return true;
            }
        }

        return done();
    }
}
