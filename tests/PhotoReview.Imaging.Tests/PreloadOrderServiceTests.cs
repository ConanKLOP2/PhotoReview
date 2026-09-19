using PhotoReview.Imaging;

namespace PhotoReview.Imaging.Tests;

public sealed class PreloadOrderServiceTests
{
    [Fact(DisplayName = "Full-folder preload prioritizes the next 32, then prior 8, and queues every other image once")]
    public void FullFolderPreloadPrioritizesNextThenPrior()
    {
        var order = PreloadOrderService.Build(center: 40, count: 100, fullFolder: true).ToArray();
        Assert.True(order[0] == 41 && order[31] == 72 && order[32] == 39
            && order.Distinct().Count() == 99 && !order.Contains(40));
    }

    [Fact(DisplayName = "Navigating changes preload priority to the new Next without duplicate jobs")]
    public void NavigatingChangesPreloadPriority()
    {
        var order = PreloadOrderService.Build(center: 44, count: 100, fullFolder: false).ToArray();
        Assert.True(order[0] == 45 && order[1] == 46 && order.Contains(43)
            && order.Length == 40 && order.Distinct().Count() == order.Length);
    }
}
