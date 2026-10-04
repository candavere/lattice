using System.Globalization;
using System.Text;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// The committed behaviour of <see cref="ScenarioRunner"/>, captured before its
/// loop was extracted and asserted after, so "the refactor changed nothing" is a
/// comparison against a fixture and not a comparison of two paths that were
/// written by the same hand.
/// </summary>
/// <remarks>
/// <para>
/// Each setup is captured in the two forms the format already has: the
/// trajectory JSONL <see cref="TrajectoryWriter"/> writes — which carries every
/// turn, every serialized per-step result, every per-tick state hash and the
/// episode's final metrics — plus a short text block for the aggregates the
/// trajectory's own final line does not carry (contended ticks, contention rate
/// and the per-agent move and efficiency counts).
/// </para>
/// <para>
/// The two setups are the ones the repository ships a recording for: the generic
/// collection skirmish (<c>site/demo.jsonl</c>'s shape — a generated map, a greedy
/// collector against a seeded random rival, instantaneous transit) and the
/// dungeon infiltration episode (<c>site/infiltration.jsonl</c>'s — capacity-one
/// portcullises, multi-tick transit, fog-of-war rosters that record their
/// decision-time perceptions).
/// </para>
/// </remarks>
public class ScenarioRunnerSteppingTests
{
    /// <summary>The seed both setups run at.</summary>
    private const ulong Seed = 42;

    /// <summary>The generic skirmish's tick budget.</summary>
    private const int DemoSteps = 30;

    /// <summary>
    /// The infiltration episode's tick budget. The same value the site recording
    /// script uses; the episode ends on its own at 20 ticks, which is the point —
    /// a step budget the run never reaches exercises the terminal-stop path.
    /// </summary>
    private const int InfiltrationSteps = 100;

    [Fact]
    public void TheGenericSkirmishRunnerStillProducesTheCommittedEpisode()
    {
        var (map, config, roster, maxSteps, label, roles) = Demo();
        var result = ScenarioRunner.Run(map, config, roster, maxSteps);

        AssertCommitted("scenario_runner_baseline_demo", map, config, result, label, roles);
    }

    [Fact]
    public void TheInfiltrationRunnerStillProducesTheCommittedEpisode()
    {
        var (map, config, roster, maxSteps) = Infiltration();
        var result = ScenarioRunner.Run(map, config, roster, maxSteps, recordPerceptions: true);

        AssertCommitted("scenario_runner_baseline_infiltration", map, config, result, InfiltrationScenario.ScenarioName, Roles());
    }

    [Fact]
    public void TheInfiltrationSetupIsTheOnesTheScenarioItselfRuns()
    {
        // The roster above is rebuilt from the scenario's own constants so a
        // reader can see the episode is the real one and not a lookalike.
        var (map, config, roster, maxSteps) = Infiltration();
        var theirs = InfiltrationScenario.Run(Seed, InfiltrationSteps);

        Assert.Equal(SerializeMap(theirs.Map), SerializeMap(map));

        // SimulationConfig is a reference type, so the four values are compared
        // rather than the two objects.
        Assert.Equal(theirs.Config.AgentCount, config.AgentCount);
        Assert.Equal(theirs.Config.MaxTicks, config.MaxTicks);
        Assert.Equal(theirs.Config.Vision, config.Vision);
        Assert.Equal(theirs.Config.TransitSpeed, config.TransitSpeed);
        Assert.Equal(maxSteps, theirs.Config.MaxTicks);
        Assert.Equal(2, roster.Length);
        Assert.Equal(
            FormatMetrics(theirs.Base.Metrics),
            FormatMetrics(ScenarioRunner.Run(map, config, roster, maxSteps, recordPerceptions: true).Metrics));
    }

    [Fact]
    public void TheCommittedEpisodesStillVerifyAsRecordings()
    {
        // The fixtures are trajectories, so the repository's own reader and
        // verifier must accept them: the baseline is checked with the same gate
        // the shipped recordings pass, not with an assertion written for it.
        foreach (var name in new[] { "scenario_runner_baseline_demo", "scenario_runner_baseline_infiltration" })
        {
            var verification = TrajectoryReplay.VerifyDetailed(
                TrajectoryReader.Read(new StringReader(Fixture(name + ".jsonl"))));

            Assert.Empty(verification.Problems);
        }
    }

    /// <summary>
    /// One setup's result, against the two committed files: the trajectory the
    /// writer produces must be the committed bytes, and the aggregates must be the
    /// committed lines.
    /// </summary>
    private static void AssertCommitted(
        string name,
        MapGraph map,
        SimulationConfig config,
        ScenarioResult result,
        string label,
        string[]? roles)
    {
        Assert.Equal(Fixture(name + ".jsonl"), Record(map, config, result, label, roles));
        Assert.Equal(Fixture(name + "-metrics.txt"), FormatMetrics(result.Metrics));
    }

    /// <summary>
    /// The trajectory the writer produces for a run: the existing serialisation,
    /// byte for byte, from the turns the runner recorded.
    /// </summary>
    private static string Record(
        MapGraph map,
        SimulationConfig config,
        ScenarioResult result,
        string label,
        string[]? roles)
    {
        var jsonl = new StringBuilder();
        using (var sink = new StringWriter(jsonl))
        {
            TrajectoryWriter.Record(
                map,
                config,
                Seed,
                result.Turns,
                sink,
                scenario: label,
                agentRoles: roles,
                perceptions: result.Perceptions);
        }

        return jsonl.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The aggregates, one per line, with round-trip formatting so a double is
    /// compared at full precision rather than to a shortened form that could hide
    /// a change in the last bits.
    /// </summary>
    private static string FormatMetrics(ScenarioMetrics metrics)
    {
        var lines = new List<string>
        {
            $"termination-reason={metrics.TerminationReason ?? "none"}",
            $"terminated={Invariant(metrics.Terminated)}",
            $"total-steps={Invariant(metrics.TotalSteps)}",
            $"max-steps={Invariant(metrics.MaxSteps)}",
            $"contended-ticks={Invariant(metrics.ContendedTicks)}",
            $"contention-rate={Invariant(metrics.ContentionRate)}",
            "agents=" + string.Join(
                "|",
                metrics.Agents.Select(agent =>
                    $"{agent.AgentId}:{agent.Score}:{agent.Moves}:{Invariant(agent.Efficiency)}")),
        };

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The generic collection skirmish: a generated map under the CLI's own
    /// generator settings, a greedy collector in slot 0 and a seeded random rival
    /// in slot 1, instantaneous transit.
    /// </summary>
    private static (MapGraph Map, SimulationConfig Config, IAgent[] Roster, int MaxSteps, string Label, string[] Roles) Demo()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: DemoSteps);
        var map = MapGenerator.Generate(Seed, new GeneratorConfig(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap));
        var roster = new IAgent[] { new GreedyCollectorAgent(0), new RandomAgent(1, new Rng(Seed)) };

        return (map, config, roster, DemoSteps, "collection-skirmish", new[] { "Collector", "Random" });
    }

    /// <summary>
    /// The dungeon infiltration roster, rebuilt from the scenario's own constants:
    /// a sentry and an infiltrator, each with its own vision bound, so the
    /// stepper and the runner are handed the same two agents the scenario hands
    /// itself.
    /// </summary>
    private static (MapGraph Map, SimulationConfig Config, IAgent[] Roster, int MaxSteps) Infiltration()
    {
        var map = DungeonMapBuilder.Build(Seed);
        var config = InfiltrationScenario.DefaultConfig(InfiltrationSteps);
        var roster = new IAgent[]
        {
            new SentryPatrolAgent(
                InfiltrationScenario.SentryAgentId,
                InfiltrationScenario.InfiltratorAgentId,
                vision: SentryPatrolAgent.DefaultVision),
            new InfiltratorAgent(
                InfiltrationScenario.InfiltratorAgentId,
                InfiltrationScenario.SentryAgentId,
                vision: InfiltratorAgent.DefaultVision),
        };

        return (map, config, roster, InfiltrationSteps);
    }

    private static string[] Roles() =>
        new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole };

    private static string SerializeMap(MapGraph map) =>
        string.Join(
            ";",
            map.Zones.Select(zone => $"{zone.Id}:{zone.Position.X},{zone.Position.Y}:{zone.MaxOccupancy}"),
            map.Resources.Select(resource => $"{resource.Id}@{resource.ZoneId}:{resource.Position.X},{resource.Position.Y}"),
            map.ChokePoints.Select(choke => $"{choke.Id}:{choke.FromZoneId}-{choke.ToZoneId}:{choke.MaxOccupancy}"));

    private static string Invariant(bool value) => value ? "true" : "false";

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Fixture(string name) =>
        File.ReadAllText(Committed(name)).TrimEnd('\n');

    private static string Committed(string name) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(new[] { "Tests", "fixtures", name }).ToArray());

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}