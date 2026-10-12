using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>WP21-STALL: lệnh giữ ấm trên thiết bị D3D/D2D THẬT (WARP offscreen trên CI; GPU nếu có) - không ném, không rò bitmap, không Present.</summary>
[Trait("Category", "Native")]
public sealed class DeviceKeepAliveNativeTests
{
    [Fact]
    public void TouchGpu_OnWarpOffscreenDevice_RepeatsWithoutLeakingOrThrowing()
    {
        using var resources = DeviceResources.CreateOffscreen(16, 16, 1.0, preferWarp: true);
        var bitmapsBefore = resources.LiveBitmapCount;
        var generation = resources.Generation;

        for (var i = 0; i < 200; i++)
        {
            resources.TouchGpu();
        }

        Assert.Equal(bitmapsBefore, resources.LiveBitmapCount);
        Assert.Equal(generation, resources.Generation);
        Assert.True(resources.IsWarp);
    }

    [Fact]
    public void TouchGpu_AfterDispose_Throws()
    {
        var resources = DeviceResources.CreateOffscreen(16, 16, 1.0, preferWarp: true);
        resources.Dispose();

        Assert.Throws<ObjectDisposedException>(resources.TouchGpu);
    }

    [Fact]
    public void OpenHardwareWork_IsNullWithoutARealGpu_OtherwiseARunnableDevice()
    {
        var work = GpuKeepAlive.OpenHardwareWork();
        if (work is null)
        {
            return; // runner CI: chỉ có WARP, giữ ấm không có nghĩa
        }

        using (work)
        {
            work.Touch();
            work.Touch();
        }
    }

    [Fact]
    public void RealKeepAlive_OnThisMachine_EitherRunsOrDisablesItself_AndDisposesCleanly()
    {
        using var keepAlive = new GpuKeepAlive(10, GpuKeepAlive.OpenHardwareWork);

        keepAlive.Activate(System.Diagnostics.Stopwatch.GetTimestamp() + (System.Diagnostics.Stopwatch.Frequency / 5));

        Assert.True(SpinWait.SpinUntil(() => keepAlive.IsDisabled || keepAlive.Ticks >= 2, TimeSpan.FromSeconds(20)),
            "luồng giữ ấm thật không chạy cũng không tự tắt");
    }
}
