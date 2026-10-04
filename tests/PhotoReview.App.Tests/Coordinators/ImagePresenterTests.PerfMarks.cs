using System.Diagnostics.Tracing;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Stryker round 3 (App): with a PhotoReview-Perf listener attached the presenter reports the final trace of a plain image
/// and starts the compare post-phase for a pair. Needs the non-parallel GlobalState collection (the class is partial:
/// the attribute covers every part).
/// </summary>
[Collection("GlobalState")]
public sealed partial class ImagePresenterTests
{
    private sealed class PerfEnabler : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    [Fact]
    public async Task Present_WithPerfEnabled_AnImageWithoutAPairIsTracedAsFinal()
    {
        var path = CreateFakeImageFile("perf_plain.png");
        _catalog.Reset([path]);
        using var listener = new PerfEnabler();

        await CreatePresenter().PresentAsync(0);

        Assert.Contains("final", _sink.TracedKinds);
    }

    [Fact]
    public async Task Present_WithPerfEnabled_AComparePairIsNotTracedAsFinalByThePairBranch()
    {
        var first = CreateFakeImageFile("perf_pair.png");
        var second = CreateFakeImageFile("perf_pair (1).png");
        _catalog.Reset([first, second]);
        using var listener = new PerfEnabler();

        await CreatePresenter().PresentAsync(0);

        Assert.DoesNotContain("final", _sink.TracedKinds);
    }
}
