using Grove.Performance;
using Xunit;

namespace GitX.Core.Tests;

public sealed class PerformanceReportTests
{
    private static Report Run(double elapsed = 10) => new(1, "test-v1", "host", "win-x64", "os", "runtime", 8, 3, 5,
        "core", "probe", DateTimeOffset.UnixEpoch, [new("search", new('a', 64), new('b', 64),
            Enumerable.Range(0, 5).Select(_ => new Sample(elapsed, 1, 100)).ToArray(), new() { ["elapsed.p95Ms"] = elapsed })]);
    private static Budgets Limits(double max = 100) => new(.2, new() { ["elapsed.p95Ms"] = 5 },
        new() { ["search"] = new() { ["elapsed.p95Ms"] = max } });

    [Fact]
    public void Percentiles_use_nearest_rank_and_reject_empty_or_non_finite_samples()
    {
        Assert.Equal(5, Evaluation.Percentile(Enumerable.Range(1, 10).Select(i => (double)i), .5));
        Assert.Equal(10, Evaluation.Percentile(Enumerable.Range(1, 10).Select(i => (double)i), .95));
        Assert.Throws<InvalidDataException>(() => Evaluation.Percentile([], .95));
        Assert.Throws<InvalidDataException>(() => Evaluation.Percentile([double.NaN], .95));
    }

    [Fact]
    public void Budget_and_relative_regression_fail_but_small_absolute_noise_is_allowed()
    {
        Assert.Empty(Evaluation.Check(Run(14), Limits(), Run(10)));
        Assert.Contains(Evaluation.Check(Run(16), Limits(), Run(10)), failure => failure.Contains("baseline allowance"));
        Assert.Contains(Evaluation.Check(Run(101), Limits(), null), failure => failure.Contains("budget"));
    }

    [Fact]
    public void Missing_scenarios_metrics_samples_and_invalid_values_never_pass()
    {
        Assert.NotEmpty(Evaluation.Check(Run() with { Scenarios = [] }, Limits(), null));
        var scenario = Run().Scenarios[0];
        Assert.NotEmpty(Evaluation.Check(Run() with { Scenarios = [scenario with { Metrics = [] }] }, Limits(), null));
        Assert.NotEmpty(Evaluation.Check(Run() with { Scenarios = [scenario with { Samples = [] }] }, Limits(), null));
        Assert.NotEmpty(Evaluation.Check(Run(double.NaN), Limits(), null));
        Assert.NotEmpty(Evaluation.Check(Run(), Limits(double.NaN), null));
    }

    [Fact]
    public void Baselines_require_same_environment_inputs_results_and_sampling()
    {
        var current = Run();
        Assert.NotEmpty(Evaluation.Check(current, Limits(), Run() with { Host = "other" }));
        Assert.NotEmpty(Evaluation.Check(current, Limits(), Run() with { Runs = 9 }));
        Assert.NotEmpty(Evaluation.Check(current, Limits(), Run() with { Scenarios = [Run().Scenarios[0] with { InputHash = new('c', 64) }] }));
        Assert.NotEmpty(Evaluation.Check(current, Limits(), Run() with { Scenarios = [Run().Scenarios[0] with { ResultHash = new('c', 64) }] }));
        Assert.Empty(Evaluation.Check(current, Limits(), Run() with { CoreHash = "new-build" }));
    }
}
