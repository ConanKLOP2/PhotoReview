namespace PhotoReview.Core.Diagnostics;

public static class BenchmarkStatistics
{
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        if (percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var ordered = values.OrderBy(x => x).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }
}
