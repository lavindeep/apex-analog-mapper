using System.Diagnostics;
using System.Globalization;

namespace Spike;

internal static class Stats
{
    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static string Summary(string name, IEnumerable<double> values, string unit = "ms")
    {
        var sorted = values.ToList();
        sorted.Sort();
        if (sorted.Count == 0)
        {
            return $"{name}: no samples";
        }
        return string.Format(CultureInfo.InvariantCulture,
            "{0}: n={1} p50={2:F3} p99={3:F3} max={4:F3} min={5:F3} mean={6:F3} {7}",
            name, sorted.Count, Percentile(sorted, 0.50), Percentile(sorted, 0.99),
            sorted[^1], sorted[0], sorted.Average(), unit);
    }

    public static string OutDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "out");
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FixturesDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "design", "fixtures"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static StreamWriter Csv(string name, string header)
    {
        var w = new StreamWriter(Path.Combine(OutDir(), name), false);
        w.WriteLine(header);
        return w;
    }

    public static string Processes(string prefix) =>
        string.Join(", ", Process.GetProcesses().Where(p => p.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.ProcessName).Distinct().Order());
}
