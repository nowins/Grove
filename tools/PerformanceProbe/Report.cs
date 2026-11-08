namespace Grove.Performance;

public sealed record Sample(double ElapsedMs, double CpuMs, long AllocatedBytes, double? FirstFilesMs = null);
public sealed record Scenario(string Name, string InputHash, string ResultHash, IReadOnlyList<Sample> Samples,
    Dictionary<string, double> Metrics);
public sealed record Report(int Schema, string Suite, string Host, string Runtime, string OS, string Framework,
    int Processors, int Warmup, int Runs, string CoreHash, string ProbeHash, DateTimeOffset Created,
    IReadOnlyList<Scenario> Scenarios);
public sealed record Budgets(double RegressionTolerance, Dictionary<string, double> RegressionFloors,
    Dictionary<string, Dictionary<string, double>> Scenarios);

public static class Evaluation
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        if (!double.IsFinite(percentile) || percentile <= 0 || percentile > 1) throw new InvalidDataException("Invalid percentile.");
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0 || sorted.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("Empty, negative or non-finite measurements.");
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    public static List<string> Check(Report current, Budgets budgets, Report? baseline)
    {
        var failures = new List<string>();
        if (!double.IsFinite(budgets.RegressionTolerance) || budgets.RegressionTolerance < 0)
            throw new InvalidDataException("Invalid regression tolerance.");
        if (current.Schema != 1 || current.Runs < 5 || current.Warmup < 1 || current.Scenarios.Count == 0)
            failures.Add("Invalid or empty report.");
        if (baseline is not null && (baseline.Schema != current.Schema || baseline.Suite != current.Suite ||
            baseline.Host != current.Host || baseline.Runtime != current.Runtime || baseline.OS != current.OS ||
            baseline.Framework != current.Framework || baseline.Processors != current.Processors ||
            baseline.Warmup != current.Warmup || baseline.Runs != current.Runs)) failures.Add("Baseline environment or sampling configuration differs.");
        if (current.Scenarios.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != current.Scenarios.Count)
            failures.Add("Duplicate scenarios.");
        if (!budgets.Scenarios.Keys.Order().SequenceEqual(current.Scenarios.Select(item => item.Name).Order()))
            failures.Add("Scenario set does not match the budget file.");
        if (baseline is not null && !baseline.Scenarios.Select(item => item.Name).Order().SequenceEqual(current.Scenarios.Select(item => item.Name).Order()))
            failures.Add("Baseline scenario set differs.");
        foreach (var scenario in current.Scenarios)
        {
            if (scenario.Samples.Count != current.Runs || scenario.InputHash.Length != 64 || scenario.ResultHash.Length != 64)
                failures.Add($"{scenario.Name}: incomplete samples or fingerprints.");
            if (scenario.Samples.Any(sample => !double.IsFinite(sample.ElapsedMs) || sample.ElapsedMs < 0 ||
                !double.IsFinite(sample.CpuMs) || sample.CpuMs < 0 || sample.AllocatedBytes < 0 ||
                sample.FirstFilesMs is { } first && (!double.IsFinite(first) || first < 0 || first > sample.ElapsedMs)))
                failures.Add($"{scenario.Name}: invalid raw samples.");
            var previous = baseline?.Scenarios.FirstOrDefault(item => item.Name == scenario.Name);
            if (previous is not null && (previous.InputHash != scenario.InputHash || previous.ResultHash != scenario.ResultHash))
                failures.Add($"{scenario.Name}: input/result fingerprint differs; review behavior before comparing speed.");
            if (!budgets.Scenarios.TryGetValue(scenario.Name, out var limits)) continue;
            if (limits.Count == 0 || !limits.Keys.Order().SequenceEqual(scenario.Metrics.Keys.Order()))
                failures.Add($"{scenario.Name}: incomplete metric budget set.");
            foreach (var (metric, limit) in limits)
            {
                if (!double.IsFinite(limit) || limit < 0 || !scenario.Metrics.TryGetValue(metric, out var value) ||
                    !double.IsFinite(value) || value < 0) { failures.Add($"{scenario.Name}.{metric}: missing or invalid metric/budget."); continue; }
                if (value > limit) failures.Add($"{scenario.Name}.{metric}: {value:F2} > budget {limit:F2}.");
                if (previous is null) continue;
                if (!previous.Metrics.TryGetValue(metric, out var old) || !double.IsFinite(old) || old < 0 ||
                    !budgets.RegressionFloors.TryGetValue(metric, out var floor) || !double.IsFinite(floor) || floor < 0)
                { failures.Add($"{scenario.Name}.{metric}: invalid baseline or regression floor."); continue; }
                var allowed = old + Math.Max(old * budgets.RegressionTolerance, floor);
                if (value > allowed) failures.Add($"{scenario.Name}.{metric}: {value:F2} > baseline allowance {allowed:F2}.");
            }
        }
        return failures;
    }
}
