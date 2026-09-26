using System.Text.Json;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Verifies the stateful <see cref="LatticeEnvironment"/> wrapper, the
/// <see cref="SimulationDriver"/> playback loop, and the end-to-end
/// determinism guarantee (same seed + same actions -> byte-identical
/// serialized trajectory when re-generated from scratch on the same host
/// under the normalized JSONL contract — docs/adr/0003-simultaneous-actions-step-contract.md).
/// </summary>
public class EnvironmentLoopTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static string TrajectoryJson(List<StepResult> trajectory) =>
        string.Join("\n", trajectory.Select(r => JsonSerializer.Serialize(r)));

    private static AgentAction[][] BuildScript(MapGraph map, int steps, int agentCount)
    {
        var resourceCount = Math.Max(1, map.Resources.Length);
        var script = new AgentAction[steps][];
        for (var step = 0; step < steps; step++)
        {
            var turn = new AgentAction[agentCount];
            turn[0] = step % 2 == 0
                ? new AgentAction(ActionKind.Move, ZoneId: step % map.Zones.Length)
                : new AgentAction(ActionKind.Collect, ResourceId: step % resourceCount);
            for (var agent = 1; agent < agentCount; agent++)
            {
                turn[agent] = new AgentAction(ActionKind.Wait);
            }

            script[step] = turn;
        }

        return script;
    }

    [Fact]
    public void Environment_StepsSequentially_UntilTerminal()
    {
        var config = new SimulationConfig(2, 3);
        var env = new LatticeEnvironment(Map, config);

        var first = env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        Assert.False(env.IsTerminal);
        Assert.False(first.Info.IsTerminal);

        var second = env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        Assert.False(env.IsTerminal);

        var third = env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        Assert.True(env.IsTerminal);
        Assert.True(third.Info.IsTerminal);
        Assert.Equal("tick-limit", third.Info.Reason);
    }

    [Fact]
    public void Environment_Reset_RestoresInitialBehavior()
    {
        var config = new SimulationConfig(2, 20);
        var env = new LatticeEnvironment(Map, config);
        env.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) });

        env.Reset();

        var first = env.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) });
        var fresh = new LatticeEnvironment(Map, config).Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) });
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(fresh));
    }

    [Fact]
    public void Driver_StopsAtTheTerminalTick()
    {
        var config = new SimulationConfig(2, 3);
        var script = BuildScript(Map, steps: 10, agentCount: 2);

        var trajectory = SimulationDriver.Play(Map, config, script);

        Assert.Equal(3, trajectory.Count);
        Assert.True(trajectory[^1].Info.IsTerminal);
        Assert.Equal("tick-limit", trajectory[^1].Info.Reason);
    }

    [Fact]
    public void Driver_RunningOutOfActions_EndsTheTrajectory()
    {
        var config = new SimulationConfig(2, 20);
        var script = BuildScript(Map, steps: 2, agentCount: 2);

        var trajectory = SimulationDriver.Play(Map, config, script);

        Assert.Equal(2, trajectory.Count);
        Assert.False(trajectory[^1].Info.IsTerminal);
    }

    [Fact]
    public void SameSeedAndActions_ProduceByteIdenticalTrajectories()
    {
        const ulong seed = 0xC0FFEEUL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var simulationConfig = new SimulationConfig(3, 50);

        var map = MapGenerator.Generate(seed, generatorConfig);
        var script = BuildScript(map, steps: 40, agentCount: 3);
        var expected = TrajectoryJson(SimulationDriver.Play(map, simulationConfig, script));

        for (var run = 0; run < 25; run++)
        {
            var regeneratedMap = MapGenerator.Generate(seed, generatorConfig);
            var trajectory = SimulationDriver.Play(regeneratedMap, simulationConfig, script);
            Assert.Equal(expected, TrajectoryJson(trajectory));
        }
    }

    [Fact]
    public void SimulationConfig_RejectsOutOfRangeAgents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(2, 0));
    }

    [Fact]
    public void SimulationConfig_RejectsInvalidTransitSpeed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(2, 10, TransitSpeed: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(2, 10, TransitSpeed: int.MinValue));

        _ = new SimulationConfig(2, 10, TransitSpeed: SimulationConfig.InstantTransit);
        _ = new SimulationConfig(2, 10, TransitSpeed: 1);
    }

    [Fact]
    public void Environment_Reset_ClearsTheTerminalFlag()
    {
        var config = new SimulationConfig(2, 2);
        var env = new LatticeEnvironment(Map, config);

        env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        Assert.True(env.IsTerminal);

        env.Reset();

        // The reset must lift the terminal latch: a fresh episode starts
        // cleanly rather than answering "terminal" until its first step.
        Assert.False(env.IsTerminal);
        var first = env.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });
        Assert.Equal(1, first.Info.StepNumber);
    }
}