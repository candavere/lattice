using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Lattice.Tui;
using Lattice.Visualization;

namespace Lattice.Cli.Presentation;

/// <summary>
/// One episode to play live: the map, the config, the roster and the budget
/// <c>lattice simulate</c> would build from the same arguments, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The setups here are the ones the CLI itself constructs, through the same public
/// constructors and the same generator settings, so <c>lattice tui simulate</c>
/// plays the episode <c>lattice simulate</c> would have recorded. A test asserts
/// that by recording both and comparing the artifacts.
/// </para>
/// <para>
/// Every agent is constructed fresh from the seed, exactly as the batch path does:
/// an agent holding a seeded generator advances with the episode, so reusing one
/// across two runs would produce a second episode that the same arguments do not
/// describe.
/// </para>
/// </remarks>
public sealed record LiveEpisodeSetup(
    MapGraph Map,
    SimulationConfig Config,
    Func<IAgent[]> NewRoster,
    int MaxSteps,
    ulong Seed,
    string Label,
    string[] Roles,
    DynamicMapRuleSet Rules)
{
    /// <summary>
    /// A fresh roster for this episode.
    /// <para>
    /// Always new: an agent that holds a seeded generator advances with the episode,
    /// so a second run of the same setup with the same instances would be an
    /// episode the arguments do not describe. A restart therefore builds a new
    /// roster too, and is the same episode rather than a continuation of it.
    /// </para>
    /// </summary>
    public IAgent[] Roster => NewRoster();

    /// <summary>
    /// The one collection skirmish <c>lattice simulate</c> runs with no scenario: a
    /// generated map, a greedy collector in slot 0, and a seeded random rival in
    /// slot 1. The map generator settings are the CLI's own.
    /// </summary>
    public static LiveEpisodeSetup Skirmish(ulong seed, int steps, string agent, DynamicMapRuleSet rules)
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: steps);
        var map = MapGenerator.Generate(seed, new GeneratorConfig(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap));

        return new LiveEpisodeSetup(
            map,
            config,
            () => new IAgent[] { Contender(agent, config, seed, rules), new RandomAgent(1, new Rng(seed)) },
            steps,
            seed,
            "collection skirmish",
            new[] { "Collector", "Random" },
            rules);
    }

    private static IAgent Contender(string agent, SimulationConfig config, ulong seed, DynamicMapRuleSet rules) =>
        agent switch
        {
            "greedy" => new GreedyCollectorAgent(0),
            "random" => new RandomAgent(0, new Rng(seed)),
            "mcts" => new MctsAgent(0, config, seed, new MctsSearchConfig(), rules),
            _ => throw new UsageError(
                $"invalid --agent '{agent}' (expected 'greedy', 'random', or 'mcts')."),
        };

    /// <summary>
    /// The dungeon infiltration episode, whose roster and vision bounds belong to
    /// the scenario itself. The seed and the tick budget are the caller's; nothing
    /// else about the episode is.
    /// </summary>
    public static LiveEpisodeSetup Infiltration(ulong seed, int steps)
    {
        var map = DungeonMapBuilder.Build(seed);
        var config = InfiltrationScenario.DefaultConfig(steps);

        return new LiveEpisodeSetup(
            map,
            config,
            () => new IAgent[]
            {
                new SentryPatrolAgent(
                    InfiltrationScenario.SentryAgentId,
                    InfiltrationScenario.InfiltratorAgentId,
                    vision: SentryPatrolAgent.DefaultVision),
                new InfiltratorAgent(
                    InfiltrationScenario.InfiltratorAgentId,
                    InfiltrationScenario.SentryAgentId,
                    vision: InfiltratorAgent.DefaultVision),
            },
            steps,
            seed,
            "dungeon infiltration & sentry patrol",
            new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
            DynamicMapRuleSet.None);
    }

    /// <summary>A stepper over this episode, with nothing run yet.</summary>
    public ScenarioStepper NewStepper() =>
        new(Map, Config, Roster, MaxSteps, Rules, recordPerceptions: RecordsPerceptions);

    /// <summary>
    /// Whether this episode's roster can carry decision-time perceptions, which is
    /// a property of the roster rather than a per-slot choice.
    /// </summary>
    public bool RecordsPerceptions =>
        Roster.All(agent => agent is IDecidesFromPerception);
}

/// <summary>
/// A live episode: the same simulation the batch path runs, advanced one tick at a
/// time on a thread of its own, with the produced ticks projected into the frames
/// the cockpit already draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>The thread is the point.</b> Stepping runs here, on a background thread the
/// render and key loop never touches, so a slow agent — an MCTS search, an external
/// process — delays a tick and nothing else. The keys stay live throughout.
/// </para>
/// <para>
/// <b>Cancellation is cooperative at tick boundaries.</b> <see cref="IAgent.Decide"/>
/// is synchronous and takes no token, and
/// <see cref="MctsAgent.Decide"/> runs a bounded batch of rollouts with no cancel
/// check of its own, so a decide already in progress is not interrupted: no thread
/// is aborted, no process is killed, and the app is never taken down from here. The
/// stop flag is read between agents and before the step is applied, and
/// <see cref="Stop"/> reports whether the thread was actually joined — which is the
/// only thing that makes "stopped" true.
/// </para>
/// <para>
/// <b>Nothing is precomputed.</b> One request produces one tick, and the cursor
/// asks for at most one tick beyond what it has shown, so there is never a queue
/// ahead of the viewer. A paused episode computes nothing at all.
/// </para>
/// <para>
/// <b>One stepper at a time.</b> A restart is performed by the same thread that was
/// doing the deciding: it can only reach the top of its loop once the running turn
/// has returned, which is precisely the point at which the old stepper can be
/// dropped. Two steppers are therefore never alive at once, and a restart pressed
/// while an agent is still thinking waits rather than racing.
/// </para>
/// </remarks>
public sealed class LiveEpisode : ILiveEpisode, IDisposable
{
    /// <summary>The line a closed viewer prints when an agent was still deciding.</summary>
    public const string StillDecidingNotice =
        "lattice tui: an agent was still deciding when the viewer closed";

    /// <summary>How long a close waits for the stepper thread before giving up on it.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly LiveEpisodeSetup _setup;
    private readonly WorldMap _map;
    private readonly object _gate = new();
    private readonly List<ReplayFrame> _frames = new();
    private readonly Thread _thread;
    private ScenarioStepper _stepper;
    private int _requested;
    private bool _restartPending;
    private volatile bool _stopRequested;
    private LiveStepperStatus _status;
    private string? _finishedReason;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>Opens an episode and starts its stepper thread. Nothing is computed yet.</summary>
    public LiveEpisode(LiveEpisodeSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);

        _setup = setup;
        _map = ReplaySource.ProjectMap(setup.Map);
        _stepper = setup.NewStepper();
        _frames.Add(StartFrame(_stepper, setup));
        _status = LiveStepperStatus.Running;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "lattice-tui-simulate",
        };
        _thread.Start();
    }

    /// <summary>The episode being played, as the CLI built it.</summary>
    public LiveEpisodeSetup Setup => _setup;

    /// <inheritdoc />
    public WorldMap Map => _map;

    /// <inheritdoc />
    public IReadOnlyList<ReplayFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public int MaximumTicks => _setup.MaxSteps;

    /// <inheritdoc />
    public ulong? Seed => _setup.Seed;

    /// <inheritdoc />
    public string Label => _setup.Label;

    /// <inheritdoc />
    public IReadOnlyList<string> AgentRoles => _setup.Roles;

    /// <inheritdoc />
    public string? FinishedReason
    {
        get
        {
            lock (_gate)
            {
                return _finishedReason;
            }
        }
    }

    /// <inheritdoc />
    public bool HasStopped
    {
        get
        {
            lock (_gate)
            {
                return _finishedReason is not null || _failure is not null;
            }
        }
    }

    /// <inheritdoc />
    public LiveStepperStatus StepperStatus
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public string? Notice
    {
        get
        {
            lock (_gate)
            {
                // A restart asked for while an agent is still deciding waits here,
                // and says so. It is acted on — and this notice cleared — only once
                // the deciding turn has returned, because the thread that acts on it
                // is the thread that was deciding.
                if (_restartPending)
                {
                    return LivePlayback.RestartWaitingNotice;
                }

                return _status switch
                {
                    LiveStepperStatus.Stopping => LivePlayback.StoppingNotice,
                    LiveStepperStatus.Stopped => LivePlayback.StoppedNotice,
                    _ => null,
                };
            }
        }
    }

    /// <summary>
    /// What went wrong, if anything did. A failure is reported once the terminal is
    /// back, as one line on stderr, and the run ends.
    /// </summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>Whether the stepper thread was joined by the last <see cref="Stop"/>.</summary>
    public bool IsJoined => StepperStatus == LiveStepperStatus.Stopped;

    /// <inheritdoc />
    /// <remarks>
    /// A slot of one, not a queue. A caller that asks again while a request is
    /// outstanding is asking for a second tick nobody is waiting for, and honouring
    /// it would put the simulation arbitrarily far ahead of the viewer. The slot is
    /// cleared when the pump takes it, so a request made after a tick has been
    /// picked up is a fresh one.
    /// </remarks>
    public void RequestTick()
    {
        lock (_gate)
        {
            if (_finishedReason is not null || _failure is not null || _stopRequested)
            {
                return;
            }

            _requested = 1;
            Monitor.PulseAll(_gate);
        }
    }

    /// <inheritdoc />
    public void RequestRestart()
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                return;
            }

            // Only ever a flag: the thread doing the deciding is the thread that
            // carries it out, so it cannot be replaced while a turn is in progress.
            _restartPending = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Asks the stepper to stop and waits, off the key thread, for the thread to
    /// finish. Reports whether it was joined.
    /// </summary>
    /// <remarks>
    /// The wait is bounded and the caller keeps painting and reading keys while it
    /// happens. A stepper that does not join inside the bound is left to finish on
    /// its own: it is a background thread, the process is about to end, and killing
    /// it would mean killing an agent mid-decision.
    /// </remarks>
    public bool Stop(TimeSpan? joinTimeout = null)
    {
        lock (_gate)
        {
            if (_status != LiveStepperStatus.Stopped)
            {
                _stopRequested = true;
                _status = LiveStepperStatus.Stopping;
                Monitor.PulseAll(_gate);
            }
        }

        return Join(joinTimeout);
    }

    /// <summary>
    /// Waits, bounded, for the stepper thread to finish, and reports whether it was
    /// joined. "Stopped" is reported only when it was.
    /// </summary>
    public bool Join(TimeSpan? joinTimeout = null)
    {
        var joined = _thread.Join(joinTimeout ?? JoinTimeout) || !_thread.IsAlive;

        if (joined)
        {
            lock (_gate)
            {
                _status = LiveStepperStatus.Stopped;
            }
        }

        return joined;
    }

    /// <summary>
    /// Stops the stepper, joins it within the bound, and disposes every agent that
    /// owns an external resource. An agent's process is never terminated while a
    /// decide of that agent may still be running, which is why disposal comes after
    /// the join and not before it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Join();

        foreach (var agent in _setup.Roster)
        {
            if (agent is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    // Disposal happens on the way out and a failure here cannot
                    // change the outcome of the run: the episode is already over.
                    // Recorded so it is not silently dropped.
                    lock (_gate)
                    {
                        _failure ??= exception;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The stepper's own loop: wait for one request, run one tick, project it.
    /// Every wait is bounded by a wake-up, and the only reasons to leave are a stop,
    /// a failure, or the episode's own end.
    /// </summary>
    private void Pump()
    {
        while (true)
        {
            lock (_gate)
            {
                while (_requested == 0 && !_restartPending && !_stopRequested && _finishedReason is null && _failure is null)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopRequested)
                {
                    return;
                }

                if (_restartPending)
                {
                    // Reached only between turns, so nothing is deciding.
                    _restartPending = false;
                    _requested = 0;
                    _stepper = _setup.NewStepper();
                    _frames.Clear();
                    _frames.Add(StartFrame(_stepper, _setup));
                    _finishedReason = null;
                    Monitor.PulseAll(_gate);
                }

                if (_finishedReason is not null || _failure is not null)
                {
                    return;
                }

                if (_requested == 0)
                {
                    continue;
                }

                _requested--;
            }

            try
            {
                var stepper = _stepper;
                if (!stepper.CanStep)
                {
                    Complete();
                    return;
                }

                var result = stepper.Step(() => _stopRequested);
                if (result is null)
                {
                    // Stopped between agents: the tick did not happen and the
                    // episode is left exactly as it stood.
                    return;
                }

                lock (_gate)
                {
                    _frames.Add(StepFrame(_stepper, result, _setup));
                    if (!stepper.CanStep)
                    {
                        _finishedReason = EndReason(result);
                        Monitor.PulseAll(_gate);
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _failure = exception;
                    Monitor.PulseAll(_gate);
                }

                return;
            }
        }
    }

    /// <summary>
    /// The recorded reason an episode ended, in the engine's own words: the
    /// terminal tick's reason, or the budget the run was cut short by. Nothing here
    /// substitutes a reason of its own.
    /// </summary>
    private static string EndReason(StepResult result) =>
        CockpitEpisodes.HasReason(result.Info.Reason) ? result.Info.Reason! : "tick-limit";

    /// <summary>
    /// Ends the episode where it stands, when the stepper says there is nothing
    /// left to do. The reason is the budget: a stepper that cannot step has either
    /// spent it or reached a terminal tick, and the terminal tick's own reason is
    /// recorded on the last frame.
    /// </summary>
    private void Complete()
    {
        lock (_gate)
        {
            _finishedReason ??= "tick-limit";
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The frame before any step: the state the episode starts from, at tick 0,
    /// with nothing claimed and no digest, because no step produced one.
    /// </summary>
    private static ReplayFrame StartFrame(ScenarioStepper stepper, LiveEpisodeSetup setup)
    {
        var state = stepper.State;

        return new ReplayFrame(
            tick: 0,
            isStart: true,
            state.Agents.Select(agent => Project(agent, setup)).ToArray(),
            state.Claims,
            actions: null,
            stateDigest: null,
            isTerminal: false,
            terminalReason: null,
            winnerSlot: null);
    }

    /// <summary>
    /// One produced tick, projected exactly as the replay reader projects a
    /// recorded one: the step's own agents and claims, its tick and terminal facts
    /// from the engine's own info, the turn formatted by the same formatter the
    /// plain playback uses, and the same per-tick state digest the recording of
    /// this episode would carry.
    /// </summary>
    private static ReplayFrame StepFrame(
        ScenarioStepper stepper,
        StepResult result,
        LiveEpisodeSetup setup)
    {
        // Full observability is the step contract's default, so every observation
        // of a step states the same world; the first is the one the playback reads.
        var observation = result.Observations is { Length: > 0 } stated ? stated[0] : null;
        var turn = stepper.Turns[stepper.Turns.Count - 1];

        return new ReplayFrame(
            result.Info.StepNumber,
            isStart: false,
            observation?.AgentStates.Select(agent => Project(agent, setup)).ToArray()
                ?? Array.Empty<WorldAgent>(),
            observation?.Claims ?? Array.Empty<int>(),
            TrajectoryPlayback.FormatActions(turn),
            SimulationStateHash.Compute(stepper.State, setup.Seed),
            result.Info.IsTerminal,
            result.Info.Reason,
            result.Info.WinnerAgentId);
    }

    private static WorldAgent Project(AgentState agent, LiveEpisodeSetup setup)
    {
        var transit = agent.Transit is { } crossing
            ? new WorldTransit(
                crossing.FromZoneId,
                crossing.ToZoneId,
                crossing.RemainingTicks,
                TotalTicks(setup, crossing.FromZoneId, crossing.ToZoneId))
            : null;

        return new WorldAgent(agent.AgentId, agent.ZoneId, agent.Score, transit);
    }

    /// <summary>
    /// The whole crossing time of an edge, as the episode's own transit speed
    /// resolves it — the same call, and the same value, the replay reader makes.
    /// </summary>
    private static int TotalTicks(LiveEpisodeSetup setup, int fromZoneId, int toZoneId) =>
        Simulation.TransitTicks(setup.Map, fromZoneId, toZoneId, setup.Config.TransitSpeed);
}