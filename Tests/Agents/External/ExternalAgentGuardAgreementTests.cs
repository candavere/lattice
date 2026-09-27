using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// Resolves the stage-2 open item about guard agreement: does
/// <see cref="ProtocolActionGuard.RequireWithinActionSpace"/> accept exactly
/// when <see cref="ActionSpace.Validate"/> accepts?
/// </summary>
/// <remarks>
/// <para>
/// These are two different authorities and they are supposed to agree.
/// <see cref="ProtocolActionGuard"/> is the wire-side pre-check, which sees only
/// the two bounds it is handed; <see cref="ActionSpace.Validate"/> is the
/// environment's own check, which §6.3 makes the authority at receipt. If the
/// guard rejected something the action space allows, Lattice would refuse a legal
/// action; if it accepted something the action space refuses, the agent's
/// recorded action would be one the trajectory verifier later rejects. Either
/// way an external agent would be penalised for Lattice's own disagreement, so
/// this is a fairness property as much as a correctness one.
/// </para>
/// <para>
/// The sample is seeded, so a disagreement is reproducible, and deliberately
/// spans in-range ids, out-of-range ids on both sides of the range, the
/// <c>-1</c> sentinel and negatives, an empty-resource map, and maps with more
/// resources than zones. A sample of only in-range actions would agree
/// trivially and prove nothing.
/// </para>
/// </remarks>
public class ExternalAgentGuardAgreementTests
{
    /// <summary>Sample size. Large enough to cross every boundary many times over.</summary>
    private const int SampleSize = 2_500;

    /// <summary>Fixed seed, so a disagreement is reproducible rather than a flake to be re-run.</summary>
    private const int Seed = 20260927;

    [Fact]
    public void The_Guard_Accepts_Exactly_When_The_Action_Space_Accepts()
    {
        var maps = new[]
        {
            ExternalAgentTestHost.TwoZoneMap(),
            ExternalAgentTestHost.ThreeZoneMap(),
            ExternalAgentTestHost.NoResourceMap(),
        };

        var random = new Random(Seed);
        var agreements = 0;
        var guardAccepted = 0;
        var spaceAccepted = 0;
        var disagreements = new List<string>();

        for (var i = 0; i < SampleSize; i++)
        {
            var map = maps[random.Next(maps.Length)];
            var action = Sample(random, map);

            var guardSays = GuardVerdict(action, map);
            var spaceSays = ActionSpace.Validate(ExternalWire.ToAction(action), map).Count == 0;

            if (guardSays)
            {
                guardAccepted++;
            }

            if (spaceSays)
            {
                spaceAccepted++;
            }

            if (guardSays == spaceSays)
            {
                agreements++;
            }
            else
            {
                disagreements.Add(
                    $"{Describe(action)} on a map with {map.Zones.Length} zone(s) and " +
                    $"{map.Resources.Length} resource(s): guard={guardSays} actionSpace={spaceSays}");
            }
        }

        Assert.True(
            agreements == SampleSize,
            $"the guard and the action space disagreed on {disagreements.Count} of {SampleSize} sampled actions:" +
            System.Environment.NewLine + string.Join(System.Environment.NewLine, disagreements.Take(10)));

        // The sample is only meaningful if it actually crossed the boundaries in
        // both directions; a run where everything was accepted would agree
        // perfectly and say nothing.
        Assert.True(guardAccepted > 0, "no sampled action was accepted by either side.");
        Assert.True(guardAccepted < SampleSize, "every sampled action was accepted; the sample proved nothing.");
        Assert.True(spaceAccepted > 0);
        Assert.True(spaceAccepted < SampleSize);
    }

    [Fact]
    public void The_Guard_Rejects_An_Out_Of_Range_Target_As_Illegal_Action()
    {
        // The specific disagreement that would be most damaging if it existed: an
        // action the guard waves through but the action space refuses, because
        // that is an agent being charged for the host's leniency.
        var map = ExternalAgentTestHost.TwoZoneMap();

        foreach (var action in new[]
        {
            new ActionMessage { Step = 0, Kind = ProtocolActionKind.Move, ZoneId = map.Zones.Length },
            new ActionMessage { Step = 0, Kind = ProtocolActionKind.Move, ZoneId = map.Zones.Length + 99 },
            new ActionMessage { Step = 0, Kind = ProtocolActionKind.Collect, ResourceId = map.Resources.Length },
        })
        {
            var reason = Assert.Throws<ProtocolViolation>(
                () => ProtocolActionGuard.RequireWithinActionSpace(action, map.Zones.Length, map.Resources.Length)).Reason;

            Assert.Equal(ProtocolReason.IllegalAction, reason);
            Assert.NotEmpty(ActionSpace.Validate(ExternalWire.ToAction(action), map));
        }
    }

    /// <summary>
    /// One wire action drawn to cross a boundary: the kind is uniform across the
    /// three values, the target is drawn from a range that deliberately includes
    /// both sides of the map's bound and the <c>-1</c> sentinel, and the
    /// conditional-presence rule of §6.1 is always honoured so the guard's
    /// <em>schema</em> check is not what is being sampled.
    /// </summary>
    private static ActionMessage Sample(Random random, MapGraph map)
    {
        var kind = (ProtocolActionKind)random.Next(3);

        return kind switch
        {
            ProtocolActionKind.Wait => new ActionMessage { Step = random.Next(4), Kind = kind },
            ProtocolActionKind.Move => new ActionMessage
            {
                Step = random.Next(4),
                Kind = kind,
                ZoneId = DrawTarget(random, map.Zones.Length),
            },
            _ => new ActionMessage
            {
                Step = random.Next(4),
                Kind = kind,
                ResourceId = DrawTarget(random, map.Resources.Length),
            },
        };
    }

    /// <summary>
    /// A target id drawn to straddle the map's bound: mostly in range, sometimes
    /// one or two past it, occasionally the record's <c>-1</c> sentinel or a
    /// negative. Both out-of-range directions matter, and so does the sentinel,
    /// which §6.1 forbids explicitly rather than merely rejecting as out of range.
    /// </summary>
    private static int DrawTarget(Random random, int count)
    {
        var roll = random.Next(100);
        if (roll < 60)
        {
            return random.Next(count);
        }

        if (roll < 80)
        {
            return count + random.Next(3);
        }

        if (roll < 90)
        {
            return -1;
        }

        return -random.Next(1, 4);
    }

    private static bool GuardVerdict(ActionMessage action, MapGraph map)
    {
        try
        {
            ProtocolActionGuard.RequireWithinActionSpace(action, map.Zones.Length, map.Resources.Length);
            return true;
        }
        catch (ProtocolViolation)
        {
            return false;
        }
    }

    private static string Describe(ActionMessage action) =>
        action.Kind switch
        {
            ProtocolActionKind.Move => $"Move(zone {action.ZoneId})",
            ProtocolActionKind.Collect => $"Collect(resource {action.ResourceId})",
            _ => "Wait",
        };
}
