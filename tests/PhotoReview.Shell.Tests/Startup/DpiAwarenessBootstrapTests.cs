using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

[Trait("Category", "HotPath")]
public sealed class DpiAwarenessBootstrapTests
{
    private sealed class FakeDpi : IDpiAwarenessNative
    {
        public bool V2 { get; set; }

        public bool AllowSet { get; set; } = true;

        public int SetCalls { get; private set; }

        public bool Throws { get; set; }

        public bool IsPerMonitorV2() => V2;

        public bool TrySetPerMonitorV2()
        {
            SetCalls++;
            if (Throws)
            {
                throw new EntryPointNotFoundException();
            }

            if (AllowSet)
            {
                V2 = true;
            }

            return AllowSet;
        }
    }

    [Fact]
    public void AlreadyPerMonitorV2_FromTheManifest_IsLeftAlone()
    {
        var dpi = new FakeDpi { V2 = true };

        Assert.True(DpiAwarenessBootstrap.EnsurePerMonitorV2(dpi));
        Assert.Equal(0, dpi.SetCalls);
    }

    [Fact]
    public void NotPerMonitorV2_IsSetOnce()
    {
        var dpi = new FakeDpi();

        Assert.True(DpiAwarenessBootstrap.EnsurePerMonitorV2(dpi));
        Assert.Equal(1, dpi.SetCalls);
    }

    [Fact]
    public void WhenWindowsRefuses_ReportsFalseWithoutThrowing()
    {
        var dpi = new FakeDpi { AllowSet = false };

        Assert.False(DpiAwarenessBootstrap.EnsurePerMonitorV2(dpi));
    }

    [Fact]
    public void OnAnOsWithoutTheApi_ReportsFalseWithoutThrowing()
    {
        var dpi = new FakeDpi { Throws = true };

        Assert.False(DpiAwarenessBootstrap.EnsurePerMonitorV2(dpi));
    }
}
