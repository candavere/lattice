using Lattice.Environment;

namespace Lattice.Agents.External;

/// <summary>
/// An <see cref="IAgentFactory"/> whose agents are external processes, so an
/// external agent is registered in a team's slot list exactly like an in-process
/// one and the harness seam (<see cref="IAgentFactory.Create"/>) is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// One process per match comes for free here: the harness calls
/// <see cref="Create"/> once per (pairing, seed), so each call is one match and
/// each returned agent is one fresh process — the same per-(pairing, seed) scope
/// the in-process factory contract already has
/// (<c>IAgentFactory</c>), and the reason no agent state can carry between seeds.
/// </para>
/// <para>
/// <b>Ownership is the caller's, and this type says so.</b> Nothing in the
/// repository disposes the agents a factory hands out, so this factory tracks
/// every process it has started and kills any that is still running when
/// <see cref="Dispose"/> is called. A caller that uses this factory directly
/// <b>MUST</b> dispose it, or it will leave a process per match behind. The
/// supported path is <see cref="ExternalMatchRunner"/>, which owns the lifetime
/// itself and cannot be forgotten.
/// </para>
/// <para>
/// A protocol failure surfaces out of <see cref="IAgent.Decide"/> as an
/// <see cref="ExternalAgentFaultException"/>. <see cref="EvaluationHarness"/> does
/// not catch it, so this factory is for callers that already have a failure path
/// of their own; it is <b>not</b> a drop-in for the existing batch harness.
/// </para>
/// </remarks>
public sealed class ExternalAgentFactory : IAgentFactory, IDisposable
{
    private readonly ExternalAgentLaunch _launch;
    private readonly string _scenario;
    private readonly int _maxTicks;
    private readonly ExternalTimeLimits _limits;
    private readonly object _gate = new();
    private readonly List<ExternalAgent> _started = [];
    private bool _disposed;

    /// <summary>Creates a factory that starts one external process per created agent.</summary>
    /// <param name="name">The team label used in result reports and summaries.</param>
    /// <param name="launch">The §3.2 launch contract: a program and an argv.</param>
    /// <param name="maxTicks">The match's tick budget, sent as <c>hello.max_ticks</c>.</param>
    /// <param name="scenario">The scenario family for <c>hello.scenario</c>.</param>
    /// <param name="limits">The two named time limits; defaults to the spec §7 values for the tick budget.</param>
    public ExternalAgentFactory(
        string name,
        ExternalAgentLaunch launch,
        int maxTicks,
        string scenario = ExternalAgent.StandardScenario,
        ExternalTimeLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        Name = name;
        _launch = launch;
        _maxTicks = maxTicks;
        _scenario = scenario;
        _limits = limits ?? ExternalTimeLimits.Default(maxTicks);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>How many processes this factory has started and not yet disposed.</summary>
    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                return _started.Count;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The seed is forwarded to the child in <c>hello.seed</c>, so an agent that
    /// seeds an RNG from the handshake gets the same stream the in-process
    /// contract would have given it.
    /// </remarks>
    public IAgent Create(int agentId, ulong runSeed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var agent = new ExternalAgent(
            _launch,
            agentId,
            runSeed,
            _scenario,
            _maxTicks,
            agentCount: 2,
            _limits);

        lock (_gate)
        {
            _started.Add(agent);
        }

        return agent;
    }

    /// <summary>Terminates every process this factory started that has not already been disposed.</summary>
    public void Dispose()
    {
        List<ExternalAgent> live;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            live = [.. _started];
            _started.Clear();
        }

        foreach (var agent in live)
        {
            agent.Dispose();
        }
    }
}
