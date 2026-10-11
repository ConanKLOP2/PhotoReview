namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>Chờ đúng một khoảng thời gian thật (đo hiệu năng/probe: nhịp cố định là chính nội dung phép đo), không phải để chờ điều kiện.</summary>
internal static class Pacing
{
    public static void Pause(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        using var never = new ManualResetEventSlim();
        never.Wait(duration);
    }

    public static void PauseMs(double milliseconds) => Pause(TimeSpan.FromMilliseconds(milliseconds));
}
