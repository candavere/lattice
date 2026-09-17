using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Performance;

/// <summary>
/// Regression gates on simulation throughput and allocation behavior.
/// These are deliberately coarse, generous bounds (a 1000-tick batch in under
/// one second, allocations growing linearly with tick count) so they measure
/// real regressions — a step loop that explodes in cost or leaks across
/// batches — without being flaky on any sane machine. The measurement harness
/// (<see cref="BenchmarkRunner"/>) warms JIT first and probes per-thread
/// allocation so parallel test execution can't skew the numbers.
/// </summary>
public class SimulationBenchmarkTests
{
    private static readonly SimulationConfig Config = new(AgentCount: 2, MaxTicks: 10_000);

    private static readonly AgentAction[] WaitTurn =
    {
        new(ActionKind.Wait),
        new(ActionKind.Wait),
    };

    private static MapGraph GeneratedMap() =>
        MapGenerator.Generate(42, new GeneratorConfig(3, 5, 1, 1, 3, 50));

    [Fact]
    public void ThousandTickBatch_CompletesWellUnderOneSecond()
    {
        var result = BenchmarkRunner.MeasureTicks(GeneratedMap(), Config, WaitTurn, 1000);

        Assert.True(
            result.ElapsedMs < 1000,
            $"A 1000-tick batch took {result.ElapsedMs:0.##}ms (limit: 1000ms).");
    }

    [Fact]
    public void Throughput_MeetsBaselineStepsPerSecondConsistently()
    {
        // Three fresh 1000-tick batches back to back; each must clear a
        // modest steps/second floor chosen far below what even a slow laptop
        // produces, so this only trips on a genuine throughput collapse.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var result = BenchmarkRunner.MeasureTicks(GeneratedMap(), Config, WaitTurn, 1000);
            Assert.True(
                result.StepsPerSecond >= 5_000,
                $"Throughput collapsed to {result.StepsPerSecond:0.#} steps/sec.");
        }
    }

    [Fact]
    public void AllocationGrowsLinearlyWithTicks()
    {
        var map = GeneratedMap();
        var shortBatch = BenchmarkRunner.MeasureTicks(map, Config, WaitTurn, 500);
        var longBatch = BenchmarkRunner.MeasureTicks(map, Config, WaitTurn, 1000);

        Assert.True(shortBatch.AllocatedBytes > 0, "Expected the batch to allocate heap memory.");
        var growth = longBatch.BytesPerTick / shortBatch.BytesPerTick;
        Assert.True(
            growth < 1.6,
            $"Per-tick allocation grew {growth:0.##}x from 500 to 1000 ticks; expected ~1.0x (linear).");
    }

    [Fact]
    public void RepeatedBatches_DoNotAccumulateAllocations()
    {
        var map = GeneratedMap();

        var first = BenchmarkRunner.MeasureTicks(map, Config, WaitTurn, 1000);
        var third = BenchmarkRunner.MeasureTicks(map, Config, WaitTurn, 1000);
        BenchmarkRunner.MeasureTicks(map, Config, WaitTurn, 1000);

        Assert.True(
            third.BytesPerTick <= first.BytesPerTick * 1.3,
            $"Per-tick allocation grew {third.BytesPerTick / first.BytesPerTick:0.##}x across repeated batches; " +
            "expected no accumulation (unbounded growth).");
    }
}