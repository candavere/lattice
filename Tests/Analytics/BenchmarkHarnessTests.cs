using Lattice.Agents;
using Lattice.Analytics.Benchmarking;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Analytics;

public class BenchmarkHarnessTests
{
    [Fact]
    public void WorkloadCatalog_MapShapes_MatchTheMatrix()
    {
        var workloads = WorkloadCatalog.BuildAll();

        var micro = workloads[0];
        Assert.Equal(3, micro.Map.Zones.Length);
        Assert.True(micro.Map.ChokePoints.Length is >= 2 and <= 5, "micro map should have 2-5 chokes");
        Assert.Equal("3z/" + micro.Map.ChokePoints.Length + "c/" + micro.Map.Resources.Length + "r", micro.MapShape);

        var facility = workloads[1];
        Assert.Equal(10, facility.Map.Zones.Length);
        Assert.Equal(4, facility.Config.AgentCount);
        Assert.False(facility.Descriptor.DynamicTopology);

        var stress = workloads[3];
        Assert.Equal(30, stress.Map.Zones.Length);
        Assert.Equal(4, stress.Config.AgentCount);
        Assert.True(stress.Map.ChokePoints.Length >= 29, "a 30-zone map has at least a 29-edge spine");
    }

    [Fact]
    public void WorkloadCatalog_DynamicContention_CarriesValidRules()
    {
        var dynamic = WorkloadCatalog.BuildAll()[2];

        Assert.True(dynamic.Descriptor.DynamicTopology);
        Assert.NotEmpty(dynamic.Rules.Rules);
        Assert.Contains(dynamic.Rules.Rules, rule => rule is TimedPortcullisRule);
        Assert.Contains(dynamic.Rules.Rules, rule => rule is EventLockedChokeRule);

        // The rules must validate against the exact topology they govern.
        dynamic.Rules.ValidateFor(dynamic.Map);

        // The first tick's overrides must be derivable — the rules apply.
        var overrides = dynamic.Rules.ComputeChokeCapacities(0, Array.Empty<int>(), dynamic.Map);
        Assert.True(overrides.Count >= 1);
    }

    [Fact]
    public void WorkloadCatalog_PolicyWorkload_UsesMctsAt32Rollouts()
    {
        var policy = WorkloadCatalog.BuildAll()[4];

        Assert.Equal(WorkloadKind.PolicyLookahead, policy.Descriptor.Kind);
        Assert.Equal(WorkloadCatalog.ThroughputDecisionsPerSecond, policy.Descriptor.ThroughputMetric);
        Assert.Equal(2, policy.Config.AgentCount);

        var roster = policy.RosterFactory();
        Assert.All(roster, agent => Assert.True(agent is MctsAgent));
    }

    [Fact]
    public void BenchmarkMath_PercentileNearestRank_KnownValues()
    {
        var sorted = Enumerable.Range(1, 100).Select(value => (long)value).ToArray();

        Assert.Equal(50, BenchmarkMath.Percentile(sorted, 0.5), 6);
        Assert.Equal(95, BenchmarkMath.Percentile(sorted, 0.95), 6);
        Assert.Equal(1, BenchmarkMath.Percentile(sorted, 0.01), 6);
        Assert.Equal(100, BenchmarkMath.Percentile(sorted, 1.0), 6);
    }

    [Fact]
    public void BenchmarkMath_SampleStandardDeviation_KnownValues()
    {
        double[] samples = { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 };

        Assert.Equal(5.0, BenchmarkMath.Mean(samples), 6);
        Assert.Equal(2.138089935, BenchmarkMath.SampleStandardDeviation(samples), 6);
    }

    [Fact]
    public void EnvironmentSample_Capture_ExposesProvenance()
    {
        var metadata = EnvironmentSample.Capture(commitOverride: null, cpuOverride: null);

        Assert.Equal("Release", metadata.Configuration);
        Assert.True(metadata.Cores > 0);
        Assert.True(metadata.RamBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Runtime));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Os));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Cpu));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Architecture));
        Assert.True(metadata.Timestamp.Length > 0);
    }

    [Fact]
    public void EnvironmentSample_Overrides_TakePrecedence()
    {
        var metadata = EnvironmentSample.Capture(commitOverride: "abcdef", cpuOverride: "Test CPU Model");

        Assert.Equal("abcdef", metadata.Commit);
        Assert.Equal("Test CPU Model", metadata.Cpu);
    }

    [Fact]
    public void Harness_RunsSingleWorkload_ReportsStatistics()
    {
        var workload = WorkloadCatalog.BuildAll(stepsOverride: 40)[1];

        var result = BenchmarkHarness.Run(workload, runs: 2, warmupSteps: 100);

        Assert.Equal("facility_static_4agent", result.Name);
        Assert.Equal(2, result.Iterations);
        Assert.True(result.MedianThroughputPerSecond > 0);
        Assert.True(result.MeanThroughputPerSecond > 0);
        Assert.True(result.MedianStepLatencyMicros >= 0);
        Assert.True(result.P95StepLatencyMicros >= result.MedianStepLatencyMicros);
        Assert.True(result.AllocationsPerStepBytes >= 0);
        Assert.True(result.TotalGcGen0 >= 0);
        Assert.True(result.TotalGcGen1 >= 0);
        Assert.True(result.TotalGcGen2 >= 0);
    }

    [Fact]
    public void Harness_DeterministicWorkload_ReproducesAnchorOnEveryIteration()
    {
        // The harness throws when a measured iteration's step digest diverges
        // from the warm-up anchor. A rule-based workload is deterministic, so
        // a multi-run pass must complete without raising.
        var workload = WorkloadCatalog.BuildAll(stepsOverride: 40)[0];

        var result = BenchmarkHarness.Run(workload, runs: 3, warmupSteps: 200);

        Assert.Equal(3, result.Iterations);
        Assert.True(result.MedianThroughputPerSecond > 0);
    }

    [Fact]
    public void Harness_NondeterministicEpisode_FailsTheVerification()
    {
        var workload = WorkloadCatalog.BuildAll(stepsOverride: 40)[0];
        var divergent = workload with
        {
            RosterFactory = () => new IAgent[]
            {
                new AmbientCounterAgent(),
                new GreedyCollectorAgent(1),
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkHarness.Run(divergent, runs: 2, warmupSteps: 100));

        Assert.Contains("diverged", exception.Message);
    }

    [Fact]
    public void Harness_RunAll_RunsEveryWorkloadInOrder()
    {
        var workloads = WorkloadCatalog.BuildAll(stepsOverride: 40);

        var result = BenchmarkHarness.RunAll(
            workloads,
            EnvironmentSample.Capture(commitOverride: "deadbeef", cpuOverride: null),
            runs: 1,
            warmupSteps: 40);

        Assert.Equal(5, result.Workloads.Count);
        for (var i = 0; i < result.Workloads.Count; i++)
        {
            Assert.Equal(workloads[i].Descriptor.Name, result.Workloads[i].Name);
            Assert.True(result.Workloads[i].MedianThroughputPerSecond > 0);
        }

        Assert.Equal("deadbeef", result.Metadata.Commit);
    }

    /// <summary>
    /// A deliberately nondeterministic agent: its decision stream depends on
    /// an ambient call counter that never resets between iterations, so a
    /// second iteration's step digests can never match the warm-up anchor.
    /// </summary>
    private sealed class AmbientCounterAgent : IAgent
    {
        private static int _calls;

        public int AgentId => 0;

        public AgentAction Decide(Observation observation)
        {
            var value = _calls++;
            return value % 2 == 0
                ? new AgentAction(ActionKind.Wait)
                : new AgentAction(ActionKind.Move, ZoneId: (int)(value % observation.Map.Zones.Length));
        }
    }
}