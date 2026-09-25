using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Abstractions;

[Trait("Category", "HotPath")]
public class NullLogTests
{
    [Fact]
    public void NullLogMethodsDoNotThrow()
    {
        var log = NullLog.Instance;

        var thrown = Record.Exception(() =>
        {
            log.Info("test info");
            log.Warn("test warn");
            log.Error("test error", new InvalidOperationException());
        });

        Assert.Null(thrown);
    }
}

