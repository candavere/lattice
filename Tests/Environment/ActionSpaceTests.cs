using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Verifies the <see cref="ActionSpace"/> shapes: which AgentActions are
/// well-formed for a given map, pure and total (every value gets an answer).
/// Uses the shared triangle map fixture: zones 0/1/2, resources 0/1/2.
/// </summary>
public class ActionSpaceTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    [Fact]
    public void Wait_IsAlwaysInSpace()
    {
        Assert.True(ActionSpace.IsValid(new AgentAction(ActionKind.Wait), Map));
        Assert.True(ActionSpace.IsValid(new AgentAction(ActionKind.Wait, ZoneId: 99, ResourceId: 99), Map));
    }

    [Fact]
    public void Move_ToExistingZone_IsInSpace()
    {
        Assert.True(ActionSpace.IsValid(new AgentAction(ActionKind.Move, ZoneId: 0), Map));
        Assert.True(ActionSpace.IsValid(new AgentAction(ActionKind.Move, ZoneId: 2), Map));
    }

    [Fact]
    public void Move_ToMissingZone_IsOutOfSpace()
    {
        var problems = ActionSpace.Validate(new AgentAction(ActionKind.Move, ZoneId: 3), Map);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("zone 3"));
    }

    [Fact]
    public void Move_ToNegativeZone_IsOutOfSpace()
    {
        Assert.False(ActionSpace.IsValid(new AgentAction(ActionKind.Move, ZoneId: -1), Map));
    }

    [Fact]
    public void Collect_OfExistingResource_IsInSpace()
    {
        Assert.True(ActionSpace.IsValid(new AgentAction(ActionKind.Collect, ResourceId: 2), Map));
    }

    [Fact]
    public void Collect_OfMissingResource_IsOutOfSpace()
    {
        var problems = ActionSpace.Validate(new AgentAction(ActionKind.Collect, ResourceId: 3), Map);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("resource 3"));
    }

    [Fact]
    public void UnknownActionKind_IsOutOfSpace()
    {
        var problems = ActionSpace.Validate(new AgentAction((ActionKind)99), Map);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("Unknown ActionKind"));
    }
}