using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Integration.Tests.Hosting;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialHandleCountTests
{
    public const string Name = "Shell handle counts (serial)";
}

/// <summary>
/// WP-14: mở/đóng trọn vòng (dispatcher + SynchronizationContext + vòng lặp + đồng hồ khung + cửa sổ hiện không kích hoạt +
/// vẽ một khung + Close) 50 lần trên cùng một thread không rò USER (HWND) hay GDI handle của tiến trình. Chạy tuần tự với
/// mọi test khác trong assembly để số đếm không lẫn cửa sổ của test song song.
/// </summary>
[Collection(SerialHandleCountTests.Name)]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellWindowLifecycleLeakTests
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;
    private const int Cycles = 50;

    [Fact]
    public void OpenRunClose_FiftyTimesOnOneThread_DoesNotLeakUserOrGdiHandles()
    {
        Exception? failure = null;
        (uint User, uint Gdi) before = default, after = default;
        var renders = 0L;
        var thread = new Thread(() =>
        {
            try
            {
                renders += Cycle() + Cycle(); // khởi động: đăng ký lớp cửa sổ, nạp con trỏ hệ thống
                before = Counts();
                for (var i = 0; i < Cycles; i++)
                {
                    renders += Cycle();
                }

                after = Counts();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(110)), "open/close cycles did not finish");
        Assert.Null(failure);
        Assert.True(renders >= Cycles + 2, $"only {renders} frames rendered"); // mỗi vòng thật sự vẽ khung trước khi đóng
        Assert.True(after.User <= before.User + 2, $"USER objects grew from {before.User} to {after.User} over {Cycles} cycles");
        Assert.True(after.Gdi <= before.Gdi + 2, $"GDI objects grew from {before.Gdi} to {after.Gdi} over {Cycles} cycles");
    }

    private static long Cycle()
    {
        using var dispatcher = new Win32UiDispatcher();
        using var context = Win32UiSynchronizationContext.Install(dispatcher);
        var loop = new MessageLoop(dispatcher);
        using var clock = new TimerFrameClock();
        loop.AddFrameClock(clock);
        using var window = new ShellWindow(new ShellWindowOptions(), clock);
        window.Closed += (_, _) => MessageLoop.Quit(0);
        window.Render += (_, _) => dispatcher.Post(window.Close, UiPriority.Background);
        window.Show(activate: false);
        window.Invalidate();

        Assert.Equal(0, loop.Run());
        Assert.True(window.IsDestroyed);
        return window.RenderCount;
    }

    private static (uint User, uint Gdi) Counts()
    {
        var process = GetCurrentProcess();
        return (GetGuiResources(process, GrUserObjects), GetGuiResources(process, GrGdiObjects));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetGuiResources")]
    private static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint GetCurrentProcess();
}
