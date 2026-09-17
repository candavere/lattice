using System.Diagnostics;
using Lattice.Environment;

namespace Lattice.Tests.Performance;

/// <summary>
/// The outcome of timing one batch of ticks through the pure step function:
/// wall-clock elapsed, throughput in steps/second, and total managed bytes
/// allocated while it ran. Allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> so a concurrent test
/// running on another thread can never inflate a measurement.
/// </summary>
internal sealed record TickBenchmark(
    int Ticks,
    double ElapsedMs,
    double StepsPerSecond,
    long AllocatedBytes,
    double BytesPerTick);

/// <summary>
/// A lightweight, allocation- and time-measuring driver for the simulation
/// core. It pumps the pure <see cref="Simulation.Step"/> exactly
/// <paramref name="ticks"/> times with a fixed turn — the same operation a
/// full episode performs, minus trajectory bookkeeping — after a short warmup
/// so JIT/startup effects don't pollute the first measured batch. This is a
/// harness for asserting throughput and allocation regressions, not a
/// microbenchmarking library: no heap of machinery, just repeatable numbers
/// with generous interpretation.
/// </summary>
internal static class BenchmarkRunner
{
    private const int WarmupTicks = 100;

    public static TickBenchmark MeasureTicks(
        MapGraph map,
        SimulationConfig config,
        AgentAction[] turn,
        int ticks)
    {
        Pump(Simulation.CreateInitial(map, config), config, turn, WarmupTicks);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        Pump(Simulation.CreateInitial(map, config), config, turn, ticks);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        return new TickBenchmark(
            ticks,
            elapsedMs,
            elapsedMs <= 0 ? 0 : ticks / (elapsedMs / 1000.0),
            allocated,
            allocated / (double)ticks);
    }

    private static void Pump(SimulationState state, SimulationConfig config, AgentAction[] turn, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            state = Simulation.Step(state, turn, config).NextState;
        }
    }
}