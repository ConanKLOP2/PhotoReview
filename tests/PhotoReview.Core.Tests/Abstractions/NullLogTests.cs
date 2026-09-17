using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Abstractions;

public class NullLogTests
{
    [Fact]
    public void NullLogMethodsDoNotThrow()
    {
        var log = NullLog.Instance;

        log.Info("test info");
        log.Warn("test warn");
        log.Error("test error", new InvalidOperationException());
    }
}
