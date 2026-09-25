using System.Globalization;
using System.Reflection;
using System.Diagnostics.Tracing;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

[Collection("GlobalState")] // switches CurrentCulture
public sealed class PhotoReviewPerfTests
{
    [Theory(DisplayName = "PathId is 8 lower-case hex chars, case-insensitive on the path and independent of the machine culture")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("")]
    public void PathId_StableAcrossCulturesAndCase(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            var a = PhotoReviewPerf.PathId(@"C:\Photos\Ilhan\img.jpg");
            var b = PhotoReviewPerf.PathId(@"c:\PHOTOS\ILHAN\IMG.JPG");
            var c = PhotoReviewPerf.PathId(@"C:\Photos\Ilhan\img2.jpg");

            Assert.Matches("^[0-9a-f]{8}$", a);
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "PathId of a relative path equals the id of its full path")]
    public void PathId_RelativeEqualsFull()
    {
        Assert.Equal(PhotoReviewPerf.PathId(Path.GetFullPath("some.jpg")), PhotoReviewPerf.PathId("some.jpg"));
    }

    [Fact(DisplayName = "Event ids are unique, positive and every event is Informational (wire contract for external tools)")]
    public void EventIds_UniqueAndInformational()
    {
        var events = typeof(PhotoReviewPerf).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<EventAttribute>()).Where(a => a is not null).Select(a => a!).ToList();

        Assert.NotEmpty(events);
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
        Assert.All(events, e => Assert.True(e.EventId > 0));
        Assert.All(events, e => Assert.Equal(EventLevel.Informational, e.Level));
    }

    [Fact(DisplayName = "MsSinceProcessStart from many threads is always a plausible small non-negative number")]
    public void MsSinceProcessStart_Plausible_UnderContention()
    {
        var values = new double[64];
        Parallel.For(0, values.Length, i => values[i] = PhotoReviewPerf.MsSinceProcessStart());

        // A torn cached start time would give ~6e13 ms (year 0001); the test host has been running for well under a day.
        Assert.All(values, v => Assert.InRange(v, 0, 24 * 3600 * 1000.0));
    }

    [Fact(DisplayName = "Ms is monotonic and non-negative for a past timestamp")]
    public void Ms_NonNegative()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.True(PhotoReviewPerf.Ms(start) >= 0);
        Assert.True(PhotoReviewPerf.Ms(start - System.Diagnostics.Stopwatch.Frequency) >= 999);
    }
}
