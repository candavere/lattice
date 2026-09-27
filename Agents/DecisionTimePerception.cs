using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// An agent that decides from its own <see cref="PerceptionFilter"/> and
/// therefore knows exactly what it perceived at the moment it chose its action.
/// <see cref="LastPerception"/> is that value: the very object the filter
/// produced inside <see cref="IAgent.Decide"/>, at that decision's tick — not a
/// projection rebuilt afterwards by a caller. Rebuilding it elsewhere would be a
/// second, independent reading of the world, and the recording's whole claim is
/// that the fog in the file is the fog the agent acted on.
/// </summary>
public interface IDecidesFromPerception
{
    /// <summary>
    /// The <see cref="PartialObservation"/> this agent's own filter produced
    /// during its most recent <see cref="IAgent.Decide"/>, or null before the
    /// first decision of the episode. Replaced on every decision, so it always
    /// describes the latest tick.
    /// </summary>
    PartialObservation? LastPerception { get; }
}
