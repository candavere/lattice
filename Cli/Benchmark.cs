using System.Text;
using Lattice.Environment;

namespace Lattice.Cli;

/// <summary>
/// The aggregate of one benchmark run: five stopped-clock batches of the pure
/// <see cref="Simulation.Step"/> loop after a 50k-tick warmup, with per-batch
/// throughput dispersion (mean/min/max/stddev), total managed allocation,
/// and process-wide GC collection counts for generations 0-2 across the whole
/// measurement window. This is the driver-program twin of the regression
/// harness in Lattice.Tests. Kept as a separate small copy rather than a new
/// library so the reference graph stays untouched (tooling may depend on the
/// core, but the core never hosts benchmarking helpers).
/// </summary>
internal sealed record BenchmarkReport(
    int Runs,
    int Ticks,
    int TotalTicks,
    int WarmupTicks,
    double MeanStepsPerSecond,
    double MinStepsPerSecond,
    double MaxStepsPerSecond,
    double StdDevStepsPerSecond,
    double TotalElapsedMs,
    long AllocatedBytes,
    double BytesPerTick,
    long GcGen0,
    long GcGen1,
    long GcGen2);

/// <summary>
/// Pumps the pure <see cref="Simulation.Step"/> function without agent,
/// episode, or serialization overhead so `benchmark` measures the core the
/// same way the performance regression gate does: one 50k-tick warmup batch,
/// then five stopped-clock batches around exactly N ticks of a Wait-only
/// turn, with heap growth read from the current thread only. The reported
/// throughput is the dispersion (mean ± stddev, min/max) across those five
/// batches, not a single sample; GC counts are the process-wide collection
/// deltas over the measured batches.
/// </summary>
internal static class Benchmark
{
    private const int WarmupTicks = 50_000;
    private const int MeasurementRuns = 5;

    public static BenchmarkReport Run(MapGraph map, SimulationConfig config, AgentAction[] turn, int ticks)
    {
        Pump(Simulation.CreateInitial(map, config), config, turn, WarmupTicks);

        var gcGen0Before = GC.CollectionCount(0);
        var gcGen1Before = GC.CollectionCount(1);
        var gcGen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var elapsedTotalMs = 0.0;
        var perRunStepsPerSecond = new double[MeasurementRuns];
        for (var run = 0; run < MeasurementRuns; run++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Pump(Simulation.CreateInitial(map, config), config, turn, ticks);
            stopwatch.Stop();
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            elapsedTotalMs += elapsedMs;
            perRunStepsPerSecond[run] = elapsedMs <= 0 ? 0 : ticks / (elapsedMs / 1000.0);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var totalTicks = ticks * MeasurementRuns;
        var mean = perRunStepsPerSecond.Average();
        var stdDev = perRunStepsPerSecond.Length < 2
            ? 0.0
            : Math.Sqrt(perRunStepsPerSecond.Sum(value =>
                (value - mean) * (value - mean)) / (perRunStepsPerSecond.Length - 1));

        return new BenchmarkReport(
            MeasurementRuns,
            ticks,
            totalTicks,
            WarmupTicks,
            mean,
            perRunStepsPerSecond.Min(),
            perRunStepsPerSecond.Max(),
            stdDev,
            elapsedTotalMs,
            allocated,
            allocated / (double)totalTicks,
            GC.CollectionCount(0) - gcGen0Before,
            GC.CollectionCount(1) - gcGen1Before,
            GC.CollectionCount(2) - gcGen2Before);
    }

    private static void Pump(SimulationState state, SimulationConfig config, AgentAction[] turn, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            state = Simulation.Step(state, turn, config).NextState;
        }
    }
}