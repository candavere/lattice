using System.Diagnostics;
using Lattice.Environment;

namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// Runs one or more workloads through a fixed, reproducible protocol: a
/// JIT-settling warm-up pass that anchors the step digests, then
/// <c>runs</c> measured iterations with a forced GC sweep before each, per-step
/// latency sampling into a combined histogram, per-iteration throughput
/// dispersion, per-step managed allocation from
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, and process-wide GC
/// collection deltas across the measured window. Every measured iteration must
/// reproduce exactly the step-digest stream the warm-up anchored — if it does
/// not, the episode went off-script and the numbers are meaningless, so the
/// harness fails loudly instead of reporting them.
/// </summary>
public static class BenchmarkHarness
{
    private static readonly double MicrosecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Runs every workload and packages the suite result.</summary>
    public static BenchmarkSuiteResult RunAll(
        IReadOnlyList<WorkloadRuntime> workloads,
        BenchmarkMetadata metadata,
        int runs,
        int warmupSteps)
    {
        if (workloads is null)
        {
            throw new ArgumentNullException(nameof(workloads));
        }

        var results = new WorkloadResult[workloads.Count];
        for (var i = 0; i < workloads.Count; i++)
        {
            results[i] = Run(workloads[i], runs, warmupSteps);
        }

        return new BenchmarkSuiteResult(metadata, results);
    }

    /// <summary>
    /// Runs a single workload under the warm-up budget
    /// <paramref name="warmupSteps"/> and <paramref name="runs"/> measured
    /// iterations, returning every recorded statistic.
    /// </summary>
    public static WorkloadResult Run(WorkloadRuntime workload, int runs, int warmupSteps)
    {
        if (workload is null)
        {
            throw new ArgumentNullException(nameof(workload));
        }

        if (runs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runs), runs, "A benchmark needs at least one measured iteration.");
        }

        if (warmupSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupSteps), warmupSteps, "Warm-up steps cannot be negative.");
        }

        var descriptor = workload.Descriptor;
        var steps = workload.StepsPerIteration;

        // The warm-up budget is turned into whole iterations: at least one full
        // pass (so the exact measured code path is JIT-settled and the digests
        // anchored) and at most two, so a workload with a tiny per-iteration
        // budget (e.g. the MCTS policy case) never burns the whole run warming
        // up a path that settles after one pass.
        var warmupRuns = Math.Clamp((int)Math.Ceiling(warmupSteps / (double)steps), 1, 2);

        // Full GC sweep before the whole protocol so no pre-existing garbage
        // contaminates either the anchored digests or the allocation window.
        CollectGarbage();

        // Warm-up: settle the JIT and anchor the reference step digests. Every
        // warm-up run after the first must match the anchor too, so warm-up
        // itself doubles as a determinism probe.
        ulong referenceHash = 0;
        for (var run = 0; run < warmupRuns; run++)
        {
            var warmup = Measure(workload, steps, null);
            if (run == 0)
            {
                referenceHash = warmup.Hash;
            }
            else
            {
                Verify(referenceHash, warmup.Hash, descriptor.Name, run);
            }
        }

        // Measurement window: forced sweep before each measured iteration
        // (so each run starts from a clean heap), allocation and GC deltas
        // across the full window.
        var latencies = new long[checked(steps) * runs];
        var perRunRates = new double[runs];

        CollectGarbage();
        var gcGen0Before = GC.CollectionCount(0);
        var gcGen1Before = GC.CollectionCount(1);
        var gcGen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        long totalElapsedTicks = 0;
        long totalDecisions = 0;
        for (var run = 0; run < runs; run++)
        {
            CollectGarbage();
            var sample = latencies.AsSpan(run * steps, steps);
            var measurement = Measure(workload, steps, sample);
            Verify(referenceHash, measurement.Hash, descriptor.Name, warmupRuns + run);

            totalElapsedTicks += measurement.ElapsedTicks;
            totalDecisions += measurement.Decisions;
            perRunRates[run] = Rate(measurement, descriptor, steps);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var totalSteps = (long)steps * runs;

        Array.Sort(latencies);
        var micros = new double[latencies.Length];
        for (var i = 0; i < latencies.Length; i++)
        {
            micros[i] = latencies[i] * MicrosecondsPerTick;
        }

        var meanRate = BenchmarkMath.Mean(perRunRates);
        var medianRate = Median(perRunRates);
        return new WorkloadResult(
            descriptor.Name,
            descriptor.MapScale.ToString(),
            descriptor.AgentCount,
            descriptor.DynamicTopology,
            workload.MapShape,
            runs,
            steps,
            descriptor.ThroughputMetric,
            medianRate,
            meanRate,
            BenchmarkMath.SampleStandardDeviation(perRunRates),
            BenchmarkMath.Percentile(latencies, 0.5) * MicrosecondsPerTick,
            BenchmarkMath.Mean(micros),
            BenchmarkMath.Percentile(latencies, 0.95) * MicrosecondsPerTick,
            totalSteps <= 0 ? 0 : allocated / (double)totalSteps,
            GC.CollectionCount(0) - gcGen0Before,
            GC.CollectionCount(1) - gcGen1Before,
            GC.CollectionCount(2) - gcGen2Before);
    }

    private static double Median(double[] perRunRates)
    {
        var sorted = (double[])perRunRates.Clone();
        Array.Sort(sorted);
        if (sorted.Length % 2 == 1)
        {
            return sorted[sorted.Length / 2];
        }

        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private static double Rate(IterationMeasurement measurement, WorkloadDescriptor descriptor, int steps)
    {
        var seconds = measurement.ElapsedTicks / (double)Stopwatch.Frequency;
        if (seconds <= 0)
        {
            return 0;
        }

        var units = descriptor.ThroughputMetric == WorkloadCatalog.ThroughputDecisionsPerSecond
            ? measurement.Decisions
            : steps;
        return units / seconds;
    }

    private static void Verify(ulong expected, ulong actual, string name, int run)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"Benchmark workload '{name}' diverged on iteration {run}: the step digest no longer matches the warm-up anchor. " +
                "The episode went off-script (nondeterministic agent or state), so the measurement is invalid.");
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static IterationMeasurement Measure(WorkloadRuntime workload, int steps, Span<long> latencies)
    {
        var config = workload.Config;
        var state = Simulation.CreateInitial(workload.Map, config, workload.Rules);
        var roster = workload.RosterFactory();
        var actions = new AgentAction[config.AgentCount];
        var observations = new Observation[config.AgentCount];

        var hash = StepHasher.Initial;
        long elapsedTicks = 0;
        long decisions = 0;

        for (var tick = 0; tick < steps; tick++)
        {
            for (var i = 0; i < config.AgentCount; i++)
            {
                observations[i] = new Observation(i, workload.Map, state.Agents, state.Claims, state.StepCount);
            }

            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < config.AgentCount; i++)
            {
                actions[i] = roster[i].Decide(observations[i]);
                decisions++;
            }

            var outcome = Simulation.Step(state, actions, config);
            var stepTicks = Stopwatch.GetTimestamp() - start;
            elapsedTicks += stepTicks;
            if (latencies.Length > 0)
            {
                latencies[tick] = stepTicks;
            }

            hash = StepHasher.Mix(hash, outcome.Result, outcome.NextState);
            state = outcome.NextState;
        }

        return new IterationMeasurement(hash, elapsedTicks, decisions);
    }

    private sealed record IterationMeasurement(ulong Hash, long ElapsedTicks, long Decisions);
}