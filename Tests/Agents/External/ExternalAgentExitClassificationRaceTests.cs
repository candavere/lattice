using System.Diagnostics;
using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// The §8.4 precedence property at the exit classification: a violation the
/// pump enqueues while the consumer is provably inside the exit branch must
/// outrank the exit. The raced state (child exited AND terminal outcome not
/// yet enqueued AND consumer in the exit branch) is established by the
/// product's inert seams, synchronized on events both ways - no sleeps and no
/// timing margins.
/// </summary>
public class ExternalAgentExitClassificationRaceTests
{
    private const ulong Seed = 9002;

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Violation_Enqueued_While_Consumer_Is_In_The_Exit_Branch_Wins()
    {
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 5_000, maxTicks: 4, slackMs: 1_000);
        using var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("oversize-line"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 4, agentCount: 2, limits);
        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;

        var pumpAtGate = new ManualResetEventSlim();
        var releasePump = new ManualResetEventSlim();
        var consumerInExitBranch = new ManualResetEventSlim();
        var releaseConsumer = new ManualResetEventSlim();
        var enqueued = new ManualResetEventSlim();

        agent.BeforeTerminalEnqueue = () => { pumpAtGate.Set(); releasePump.Wait(); };
        agent.TerminalEnqueued = () => enqueued.Set();
        agent.ExitBranchProbe = () => { consumerInExitBranch.Set(); releaseConsumer.Wait(); };

        var observation = ObservationFor(ExternalAgentTestHost.TwoZoneMap(), slot: 0);
        var decide = Task.Run(() => agent.Decide(observation));

        try
        {
            // The raced state is proven, not inferred: BOTH the consumer inside
            // the exit branch AND the pump arrived at its terminal enqueue with
            // nothing enqueued yet. Each side carries its own signal.
            Assert.True(
                WaitHandle.WaitAll(
                    new WaitHandle[] { consumerInExitBranch.WaitHandle, pumpAtGate.WaitHandle },
                    TimeSpan.FromSeconds(10)),
                "raced state not reached: consumer in the exit branch and pump at its terminal enqueue");

            releasePump.Set();
            Assert.True(
                enqueued.Wait(TimeSpan.FromSeconds(10)),
                "the terminal outcome never landed after the pump was released");
            releaseConsumer.Set();

            var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                () => decide.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(ProtocolReason.LineTooLong, fault.Fault.Reason);
        }
        finally
        {
            releasePump.Set();
            releaseConsumer.Set();
            agent.Dispose();
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        }
    }

    private static Observation ObservationFor(MapGraph map, int slot)
    {
        var state = Simulation.CreateInitial(map, new SimulationConfig(AgentCount: 2, MaxTicks: 1), DynamicMapRuleSet.None);
        return new Observation(slot, state.Map, state.Agents, state.Claims, state.StepCount);
    }
}
