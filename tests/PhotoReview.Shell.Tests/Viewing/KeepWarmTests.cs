using System.Diagnostics;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP21-STALL: khi nào giữ GPU ấm. Chỉ khi người dùng TƯƠNG TÁC và cửa sổ ĐANG ACTIVE; hết <c>WindowMs</c> thì thôi; chuột 1 kHz không
/// được gọi kích hoạt từng thông điệp; thông điệp thường (WM_PAINT, WM_TIMER...) không kích hoạt.
/// </summary>
public sealed class KeepWarmTests
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmMouseMove = 0x0200;
    private const uint WmPaint = 0x000F;
    private const uint WmTimer = 0x0113;
    private const uint WmActivate = 0x0006;

    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    private static KeepWarmOptions Parse(params (string Key, string? Value)[] items)
    {
        var env = Env(items);
        return KeepWarmOptions.FromEnvironment(name => env.GetValueOrDefault(name));
    }

    private sealed class Harness
    {
        public bool Active { get; set; } = true;

        public long Now { get; set; } = 1_000_000;

        public readonly List<long> Activations = [];

        public KeepWarmController Controller { get; }

        public Harness(KeepWarmOptions? options = null)
        {
            Controller = new KeepWarmController(options ?? new KeepWarmOptions(true, 5000, 20), () => Active, Activations.Add, () => Now);
        }

        public void AdvanceMs(double ms) => Now += (long)(ms * Stopwatch.Frequency / 1000);
    }

    [Fact]
    public void Defaults_AreEnabledWithFiveSecondsAndTwentyMilliseconds()
    {
        var options = Parse();

        Assert.True(options.Enabled);
        Assert.Equal(5000, options.WindowMs);
        Assert.Equal(20, options.IntervalMs);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("false")]
    [InlineData("No")]
    [InlineData(" 0 ")]
    public void DisableValues_TurnItOff(string value) => Assert.False(Parse((KeepWarmOptions.EnabledVariable, value)).Enabled);

    [Theory]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("whatever")]
    public void AnyOtherValue_KeepsItOn(string value) => Assert.True(Parse((KeepWarmOptions.EnabledVariable, value)).Enabled);

    [Theory]
    [InlineData("3000", 3000)]
    [InlineData("10", KeepWarmOptions.MinWindowMs)]
    [InlineData("999999", KeepWarmOptions.MaxWindowMs)]
    [InlineData("abc", KeepWarmOptions.DefaultWindowMs)]
    [InlineData(null, KeepWarmOptions.DefaultWindowMs)]
    public void WindowMs_IsParsedAndClamped(string? value, int expected) =>
        Assert.Equal(expected, Parse((KeepWarmOptions.WindowVariable, value)).WindowMs);

    [Theory]
    [InlineData("16", 16)]
    [InlineData("1", KeepWarmOptions.MinIntervalMs)]
    [InlineData("5000", KeepWarmOptions.MaxIntervalMs)]
    [InlineData("x", KeepWarmOptions.DefaultIntervalMs)]
    public void IntervalMs_IsParsedAndClamped(string value, int expected) =>
        Assert.Equal(expected, Parse((KeepWarmOptions.IntervalVariable, value)).IntervalMs);

    [Theory]
    [InlineData(WmKeyDown, true)]
    [InlineData(0x0104u, true)]   // WM_SYSKEYDOWN
    [InlineData(WmMouseMove, true)]
    [InlineData(0x0201u, true)]   // WM_LBUTTONDOWN
    [InlineData(0x0204u, true)]   // WM_RBUTTONDOWN
    [InlineData(0x0207u, true)]   // WM_MBUTTONDOWN
    [InlineData(0x020Au, true)]   // WM_MOUSEWHEEL
    [InlineData(0x020Eu, true)]   // WM_MOUSEHWHEEL
    [InlineData(WmActivate, true)]
    [InlineData(0x0240u, true)]   // WM_TOUCH
    [InlineData(0x0245u, true)]   // WM_POINTERUPDATE
    [InlineData(0x0246u, true)]   // WM_POINTERDOWN
    [InlineData(WmKeyUp, false)]
    [InlineData(WmPaint, false)]
    [InlineData(WmTimer, false)]
    [InlineData(0x0005u, false)]  // WM_SIZE
    [InlineData(0x0202u, false)]  // WM_LBUTTONUP
    public void IsInteraction_ClassifiesWindowMessages(uint message, bool expected) =>
        Assert.Equal(expected, KeepWarmController.IsInteraction(message));

    [Fact]
    public void Interaction_ExtendsTheWarmWindowByWindowMs()
    {
        var h = new Harness(new KeepWarmOptions(true, 3000, 20));

        h.Controller.NoteInteraction();

        var until = Assert.Single(h.Activations);
        Assert.Equal(h.Now + (3000 * Stopwatch.Frequency / 1000), until);
        Assert.Equal(1, h.Controller.Activations);
    }

    [Fact]
    public void InactiveWindow_NeverWarms()
    {
        var h = new Harness { Active = false };

        h.Controller.NoteInteraction();
        h.Controller.NoteInteraction();

        Assert.Empty(h.Activations);
        Assert.Equal(0, h.Controller.Activations);
    }

    [Fact]
    public void DisabledOptions_NeverWarm()
    {
        var h = new Harness(new KeepWarmOptions(false, 5000, 20));

        h.Controller.NoteInteraction();

        Assert.Empty(h.Activations);
    }

    [Fact]
    public void WarmAtLaunch_ExtendsTheWarmWindowWithoutAnInteraction_AndLeavesTheInteractionCountersAlone()
    {
        var h = new Harness(new KeepWarmOptions(true, 3000, 20));

        h.Controller.WarmAtLaunch();

        var until = Assert.Single(h.Activations);
        Assert.Equal(h.Now + (3000 * Stopwatch.Frequency / 1000), until);
        Assert.Equal(1, h.Controller.LaunchWarmups);
        Assert.Equal(0, h.Controller.Activations);

        // Không dùng throttle của tương tác: phím đầu tiên ngay sau đó vẫn kéo dài khoảng giữ ấm bình thường.
        h.Controller.NoteInteraction();
        Assert.Equal(2, h.Activations.Count);
        Assert.Equal(1, h.Controller.Activations);
    }

    [Fact]
    public void WarmAtLaunch_WhenDisabled_DoesNothing()
    {
        var h = new Harness(new KeepWarmOptions(false, 5000, 20));

        h.Controller.WarmAtLaunch();

        Assert.Empty(h.Activations);
        Assert.Equal(0, h.Controller.LaunchWarmups);
    }

    [Fact]
    public void DenseInteractions_AreThrottled_ToOneExtensionPerThrottleWindow()
    {
        var h = new Harness();

        for (var i = 0; i < 100; i++)
        {
            h.Controller.NoteInteraction();
            h.AdvanceMs(1); // chuột 1 kHz trong 100 ms
        }

        Assert.Single(h.Activations);

        h.AdvanceMs(KeepWarmController.ThrottleMs); // qua ngưỡng
        h.Controller.NoteInteraction();

        Assert.Equal(2, h.Activations.Count);
        Assert.True(h.Activations[1] > h.Activations[0], "lần kéo dài sau phải chốt hạn muộn hơn");
    }

    [Fact]
    public void InteractionWhileInactive_DoesNotConsumeTheThrottle()
    {
        var h = new Harness { Active = false };
        h.Controller.NoteInteraction();
        h.Active = true;

        h.Controller.NoteInteraction(); // ngay sau đó (cùng thời điểm): vẫn phải kích hoạt

        Assert.Single(h.Activations);
    }

    [Fact]
    public void TryHandle_NeverClaimsTheMessage_AndOnlyInteractionsActivate()
    {
        var h = new Harness();

        Assert.False(h.Controller.TryHandle(new WindowMessage(1, WmPaint, 0, 0), out var paintResult));
        Assert.Equal(0, paintResult);
        Assert.Empty(h.Activations);

        Assert.False(h.Controller.TryHandle(new WindowMessage(1, WmKeyDown, 0x27, 0), out var keyResult));
        Assert.Equal(0, keyResult);
        Assert.Single(h.Activations);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        var options = new KeepWarmOptions();
        Assert.Throws<ArgumentNullException>(() => new KeepWarmController(null!, () => true, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new KeepWarmController(options, null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new KeepWarmController(options, () => true, null!));
    }
}
