using System.Text;
using Lattice.Environment;

namespace Lattice.Cli;

/// <summary>
/// Wall-clock and allocation measurements for one pumped batch of ticks, the
/// driver-program twin of the regression harness in Lattice.Tests. The CLI's
/// `benchmark` subcommand reports these; the test suite asserts on them. Kept
/// as a separate small copy rather than a new library so the reference graph
/// stays untouched (tooling may depend on the core, but the core never hosts
/// benchmarking helpers).
/// </summary>
internal sealed record TickBenchmark(int Ticks, double ElapsedMs, double StepsPerSecond, long AllocatedBytes, double BytesPerTick);

/// <summary>
/// Pumps the pure <see cref="Simulation.Step"/> function without agent,
/// episode, or serialization overhead so `benchmark --ticks N` measures the
/// core the same way the performance regression gate does: one warmup batch, then a
/// stopped clock around exactly N ticks of a Wait-only turn, with heap growth
/// read from the current thread only.
/// </summary>
internal static class Benchmark
{
    private const int WarmupTicks = 100;

    public static TickBenchmark Run(MapGraph map, SimulationConfig config, AgentAction[] turn, int ticks)
    {
        Pump(Simulation.CreateInitial(map, config), config, turn, WarmupTicks);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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