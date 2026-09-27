using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// <c>hello</c> — Lattice to agent; opens the session (spec §3.1).
/// </summary>
public sealed record HelloMessage : ProtocolMessage
{
    /// <summary>Initializes the message with its <c>hello</c> discriminator.</summary>
    public HelloMessage()
        : base(ProtocolMessageTypes.Hello)
    {
    }

    /// <summary>The wire version integer; exactly <see cref="ProtocolLimits.Version"/> (spec §2).</summary>
    [JsonPropertyName("protocol")]
    [JsonPropertyOrder(10)]
    public required int Protocol { get; init; }

    /// <summary>
    /// The evaluation scenario family that selected the map. The v3.0 closed set
    /// is <c>"standard"</c> and <c>"bottleneck"</c>; <c>infiltration</c> is not
    /// reachable through the external-agent path (spec §3.1).
    /// </summary>
    [JsonPropertyName("scenario")]
    [JsonPropertyOrder(20)]
    public required string Scenario { get; init; }

    /// <summary>The run seed, an unsigned 64-bit value (spec §3.1).</summary>
    [JsonPropertyName("seed")]
    [JsonPropertyOrder(30)]
    public required long Seed { get; init; }

    /// <summary>
    /// The agent slot this process plays, in <c>0..agent_count-1</c>. Always
    /// <c>0</c> or <c>1</c> on the <c>evaluate</c> path, because evaluation
    /// pairings are head-to-head (spec §3.1).
    /// </summary>
    [JsonPropertyName("agent_slot")]
    [JsonPropertyOrder(40)]
    public required int AgentSlot { get; init; }

    /// <summary>
    /// The match's tick budget, from <c>SimulationConfig.MaxTicks</c>
    /// (<c>Environment/Simulation.cs:28</c>). The horizon the agent plans
    /// against, and the multiplier in §7's
    /// <c>match_timeout_ms ≥ step_timeout_ms × MaxTicks</c> constraint.
    /// </summary>
    [JsonPropertyName("max_ticks")]
    [JsonPropertyOrder(50)]
    public required int MaxTicks { get; init; }

    /// <summary>
    /// The number of agents in the match, from <c>SimulationConfig.AgentCount</c>
    /// (<c>Environment/Simulation.cs:25</c>), which is bounded to 2..4. Always
    /// <c>2</c> on the <c>evaluate</c> path.
    /// </summary>
    [JsonPropertyName("agent_count")]
    [JsonPropertyOrder(60)]
    public required int AgentCount { get; init; }

    /// <summary>The two named per-match time limits (spec §3.1, §7).</summary>
    [JsonPropertyName("limits")]
    [JsonPropertyOrder(70)]
    public required MatchLimitsWire Limits { get; init; }
}

/// <summary>
/// <c>hello.limits</c> — exactly two fields and no others (spec §3.1).
/// </summary>
/// <remarks>
/// The values are stage-3 constants (spec §14, U-2); this type fixes the field
/// names, types, and semantics and asserts no number. Both are integers ≥ 1.
/// </remarks>
public sealed record MatchLimitsWire
{
    /// <summary>
    /// Wall-clock milliseconds Lattice will wait for one <c>action</c> after
    /// writing an <c>observation</c>, and for one <c>hello_ack</c> after writing
    /// <c>hello</c>. Breaching the former is <c>timeout_step</c> and the latter
    /// is <c>timeout_handshake</c> (spec §7).
    /// </summary>
    [JsonPropertyName("step_timeout_ms")]
    [JsonPropertyOrder(10)]
    public required int StepTimeoutMs { get; init; }

    /// <summary>
    /// Wall-clock milliseconds Lattice will allow for the whole match, measured
    /// from the moment <c>hello</c> is written. Breaching it is
    /// <c>timeout_match</c> (spec §7).
    /// </summary>
    [JsonPropertyName("match_timeout_ms")]
    [JsonPropertyOrder(20)]
    public required int MatchTimeoutMs { get; init; }
}

/// <summary>
/// <c>hello_ack</c> — agent to Lattice; confirms the exact protocol version
/// (spec §3.1).
/// </summary>
public sealed record HelloAckMessage : ProtocolMessage
{
    /// <summary>Initializes the message with its <c>hello_ack</c> discriminator.</summary>
    public HelloAckMessage()
        : base(ProtocolMessageTypes.HelloAck)
    {
    }

    /// <summary>
    /// The wire version integer. Any value other than exactly
    /// <see cref="ProtocolLimits.Version"/> — including a string, a float, and a
    /// missing field — is <c>protocol_mismatch</c> (spec §2).
    /// </summary>
    [JsonPropertyName("protocol")]
    [JsonPropertyOrder(10)]
    public required int Protocol { get; init; }
}

/// <summary>
/// <c>error</c> — Lattice to agent; optional, announces a named failure before
/// termination (spec §8.1).
/// </summary>
public sealed record ErrorMessage : ProtocolMessage
{
    /// <summary>Initializes the message with its <c>error</c> discriminator.</summary>
    public ErrorMessage()
        : base(ProtocolMessageTypes.Error)
    {
    }

    /// <summary>One of the fourteen closed reason codes (spec §8).</summary>
    [JsonPropertyName("reason")]
    [JsonPropertyOrder(10)]
    public required ProtocolReason Reason { get; init; }

    /// <summary>
    /// Human-readable text. Diagnostic only: an agent MUST NOT parse it and MUST
    /// NOT branch on it (spec §8.1).
    /// </summary>
    [JsonPropertyName("detail")]
    [JsonPropertyOrder(20)]
    public required string Detail { get; init; }
}
