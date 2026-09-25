using System.Reflection;
using System.Windows.Threading;
using PhotoReview.App.Diagnostics;
using Xunit;

namespace PhotoReview.App.Tests;

public class PerfDispatcherHooksCanaryTests
{
    // Canary: the perf log names long dispatcher operations by reading WPF's private callback field. If a WPF update
    // renames it, the description silently degrades to priority-only; this makes that visible.
    [Fact]
    public void DispatcherOperation_HasThePrivateCallbackFieldThePerfLogReads()
    {
        var field = typeof(DispatcherOperation).GetField(PerfDispatcherHooks.MethodFieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.True(typeof(Delegate).IsAssignableFrom(field.FieldType), $"{field.FieldType} is not a delegate");
    }
}
