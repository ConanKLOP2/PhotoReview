using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>WP-22: số lần nhấn liên tiếp (<c>ClickCount</c>): cùng nút, trong thời gian double-click, trong hình chữ nhật slop.</summary>
public sealed class ClickCounterTests
{
    private const int Time = 500; // GetDoubleClickTime mặc định
    private const int Slop = 4;   // SM_CXDOUBLECLK/SM_CYDOUBLECLK ở bài test (nửa = 2)

    private static int Register(ClickCounter c, PointerButton b, int x, int y, int t) => c.Register(b, x, y, t, Time, Slop, Slop);

    [Fact]
    public void FirstClick_IsOne_AndTheSecondWithinTimeAndSlopIsTwo_ThirdIsThree()
    {
        var counter = new ClickCounter();
        Assert.Equal(1, Register(counter, PointerButton.Left, 100, 100, 1000));
        Assert.Equal(2, Register(counter, PointerButton.Left, 100, 100, 1100));
        Assert.Equal(3, Register(counter, PointerButton.Left, 101, 101, 1200));
    }

    [Theory]
    [InlineData(500, 2)]   // đúng GetDoubleClickTime vẫn là double-click
    [InlineData(501, 1)]
    [InlineData(0, 2)]
    [InlineData(-1, 1)]    // dấu thời gian lùi: không phải chuỗi
    public void TimeWindow_IsInclusive(int elapsed, int expected)
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, 1000);
        Assert.Equal(expected, Register(counter, PointerButton.Left, 10, 10, 1000 + elapsed));
    }

    [Theory]
    [InlineData(2, 0, 2)]   // nửa slop (4/2) là biên trong
    [InlineData(-2, 0, 2)]
    [InlineData(0, 2, 2)]
    [InlineData(0, -2, 2)]
    [InlineData(3, 0, 1)]
    [InlineData(-3, 0, 1)]
    [InlineData(0, 3, 1)]
    [InlineData(0, -3, 1)]
    public void SlopRectangle_IsCenteredOnTheFirstClick_BothAxes(int dx, int dy, int expected)
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 50, 50, 1000);
        Assert.Equal(expected, Register(counter, PointerButton.Left, 50 + dx, 50 + dy, 1100));
    }

    [Fact]
    public void ADifferentButton_StartsANewChain()
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, 1000);
        Assert.Equal(1, Register(counter, PointerButton.Middle, 10, 10, 1050));
        Assert.Equal(2, Register(counter, PointerButton.Middle, 10, 10, 1100));
        Assert.Equal(1, Register(counter, PointerButton.Left, 10, 10, 1150));
    }

    [Fact]
    public void ASlowSecondClick_RestartsTheChain_AndTheNextOneCountsFromIt()
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, 0);
        Assert.Equal(1, Register(counter, PointerButton.Left, 10, 10, 2000));
        Assert.Equal(2, Register(counter, PointerButton.Left, 10, 10, 2100));
    }

    [Fact]
    public void ChainContinuesFromTheLastClick_NotFromTheFirst()
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, 0);
        Register(counter, PointerButton.Left, 10, 10, 400);
        Assert.Equal(3, Register(counter, PointerButton.Left, 10, 10, 800)); // 800 ms từ lần đầu, 400 ms từ lần trước
    }

    [Fact]
    public void TickCountWrap_StillCountsAsConsecutive()
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, int.MaxValue - 50);
        Assert.Equal(2, Register(counter, PointerButton.Left, 10, 10, int.MinValue + 100)); // cách 151 ms qua điểm tràn
    }

    [Fact]
    public void Reset_AndSeed_ControlTheChain()
    {
        var counter = new ClickCounter();
        Register(counter, PointerButton.Left, 10, 10, 0);
        counter.Reset();
        Assert.Equal(1, Register(counter, PointerButton.Left, 10, 10, 10));

        counter.Seed(PointerButton.Left, 10, 10, 100, count: 4);
        Assert.Equal(5, Register(counter, PointerButton.Left, 10, 10, 100));
    }
}
