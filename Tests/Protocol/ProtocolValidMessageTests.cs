using System.Text;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// One valid example per message type, and the requirement that the §4.1 example
/// lines in the normative spec parse <b>verbatim</b> — both that the committed
/// fixtures still equal the document's text, and that re-encoding a parsed
/// example reproduces the document's bytes exactly.
/// </summary>
public class ProtocolValidMessageTests
{
    /// <summary>
    /// The fixtures are the §4.1 example lines, and this asserts they still are.
    /// Reading the document at test time is what makes the claim checkable: a
    /// duplicated constant in a test only asserts the test agrees with itself,
    /// whereas this fails the build the moment an example is edited without its
    /// fixture.
    /// </summary>
    [Theory]
    [InlineData("hello", "hello.jsonl")]
    [InlineData("hello_ack", "hello_ack.jsonl")]
    [InlineData("observation", "observation.jsonl")]
    [InlineData("action", "action.jsonl")]
    [InlineData("error", "error.jsonl")]
    public void Spec_Example_Line_Matches_Its_Fixture_Verbatim(string type, string fixture)
    {
        var expected = ProtocolFixtures.SpecExample(type);
        var actual = Encoding.UTF8.GetString(ProtocolFixtures.Line(fixture));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The same claim in the other direction: parsing a spec example and writing
    /// it back must reproduce the example byte for byte. This is the strongest
    /// available statement that the writer is faithful — it pins member order,
    /// null omission, the discriminator's position, number spelling, and the
    /// absence of a BOM all at once, because any drift in any of them changes
    /// these bytes.
    /// </summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("hello_ack")]
    [InlineData("observation")]
    [InlineData("action")]
    [InlineData("error")]
    public void Spec_Example_Line_Survives_A_Write_Round_Trip(string type)
    {
        var line = Encoding.UTF8.GetBytes(ProtocolFixtures.SpecExample(type));
        var message = ProtocolParser.Parse(line);

        // Encode without the terminator, so this compares against the spec's own
        // text; the framing properties of the terminated form are asserted
        // separately in ProtocolWriterTests.
        Assert.Equal(line, ProtocolWriter.Encode(message));
        Assert.Equal(type, message.Type);
    }

    [Fact]
    public void Hello_Parses_Every_Handshake_Field()
    {
        var hello = ProtocolParser.ParseHello(ProtocolFixtures.Line("hello.jsonl"));

        Assert.Equal("hello", hello.Type);
        Assert.Equal(1, hello.Protocol);
        Assert.Equal("standard", hello.Scenario);
        Assert.Equal(1001L, hello.Seed);
        Assert.Equal(0, hello.AgentSlot);

        // The two fields that settle the largest usability gap in the contract
        // (spec §14, U-3): an agent learns its horizon and its episode size from
        // the handshake rather than by running out of steps.
        Assert.Equal(500, hello.MaxTicks);
        Assert.Equal(2, hello.AgentCount);
        Assert.Equal(5000, hello.Limits.StepTimeoutMs);
        Assert.Equal(1200000, hello.Limits.MatchTimeoutMs);
    }

    [Fact]
    public void HelloAck_Parses_And_Confirms_The_Exact_Version()
    {
        var ack = ProtocolParser.ParseHelloAck(ProtocolFixtures.Line("hello_ack.jsonl"));

        Assert.Equal("hello_ack", ack.Type);
        Assert.Equal(ProtocolLimits.Version, ack.Protocol);
    }

    [Fact]
    public void Observation_Parses_The_Flattened_Projection()
    {
        var observation = ProtocolParser.ParseObservation(ProtocolFixtures.Line("observation.jsonl"));

        Assert.Equal(0, observation.Step);
        Assert.Equal(0, observation.AgentId);
        Assert.Equal(0, observation.StepNumber);

        Assert.Equal(2, observation.Map.Zones.Length);
        Assert.Equal(2147483647, observation.Map.Zones[0].MaxOccupancy);
        Assert.Equal(0, observation.Map.Zones[0].Position.X);
        Assert.Equal(0, observation.Map.Zones[0].Position.Y);
        Assert.Equal(3, observation.Map.Zones[1].Position.X);
        Assert.Equal(0, observation.Map.Zones[1].Position.Y);

        // max_occupancy is an explicit capacity, never omitted (spec §5.3).
        Assert.All(observation.Map.Zones, zone => Assert.True(zone.MaxOccupancy > 0));

        Assert.Single(observation.Map.Resources);
        Assert.Equal(1, observation.Map.Resources[0].ZoneId);

        Assert.Single(observation.Map.ChokePoints);
        Assert.Equal(0, observation.Map.ChokePoints[0].FromZoneId);
        Assert.Equal(1, observation.Map.ChokePoints[0].ToZoneId);
        Assert.Equal(1, observation.Map.ChokePoints[0].MaxOccupancy);

        // Full observability: every agent in the episode, not just the observer.
        Assert.Equal(2, observation.AgentStates.Length);
        Assert.Equal([], observation.Claims);
    }

    [Fact]
    public void Observation_Parses_Optional_Roles_And_Transit()
    {
        var observation = ProtocolParser.ParseObservation(ProtocolFixtures.Line("observation_full.jsonl"));

        Assert.Equal("depot", observation.Map.Zones[0].Role);
        Assert.Equal("sink", observation.Map.Zones[1].Role);
        Assert.Equal("fuel", observation.Map.Resources[0].Role);
        Assert.Equal("gate", observation.Map.ChokePoints[0].Role);

        // An agent mid-edge is not at a node: zone_id stays the departure node
        // and transit describes the crossing (spec §5.4).
        var crossing = observation.AgentStates[0];
        Assert.Equal(0, crossing.ZoneId);
        Assert.NotNull(crossing.Transit);
        Assert.Equal(0, crossing.Transit!.FromZoneId);
        Assert.Equal(1, crossing.Transit.ToZoneId);
        Assert.Equal(1, crossing.Transit.RemainingTicks);

        var parked = observation.AgentStates[1];
        Assert.Null(parked.Transit);

        Assert.Equal([0], observation.Claims);
    }

    /// <summary>
    /// A null <c>string?</c> is omitted, never emitted as <c>null</c> (spec §5.2,
    /// rule 5). The spec's own observation example has absent <c>role</c> fields
    /// because they are null, and the round-trip test above only holds if the
    /// writer omits rather than writes null — so this states it directly.
    /// </summary>
    [Fact]
    public void Null_Roles_Are_Omitted_Not_Written_As_Null()
    {
        var text = Encoding.UTF8.GetString(ProtocolWriter.Encode(
            ProtocolParser.ParseObservation(ProtocolFixtures.Line("observation.jsonl"))));

        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"role\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Action_Parses_And_Carries_Its_Conditional_Target()
    {
        var action = ProtocolParser.ParseAction(ProtocolFixtures.Line("action.jsonl"), expectedStep: 0);

        Assert.Equal("action", action.Type);
        Assert.Equal(0, action.Step);
        Assert.Equal(ProtocolActionKind.Move, action.Kind);
        Assert.Equal(1, action.ZoneId);

        // The wire carries no field the record would ignore (spec §6.1).
        Assert.Null(action.ResourceId);
    }

    [Theory]
    [InlineData("action_wait.jsonl", ProtocolActionKind.Wait, 4)]
    [InlineData("action_collect.jsonl", ProtocolActionKind.Collect, 5)]
    [InlineData("action.jsonl", ProtocolActionKind.Move, 0)]
    public void Every_Action_Kind_Round_Trips(string fixture, ProtocolActionKind kind, int step)
    {
        var line = ProtocolFixtures.Line(fixture);
        var action = ProtocolParser.ParseAction(line, expectedStep: step);

        Assert.Equal(kind, action.Kind);
        Assert.Equal(line, ProtocolWriter.Encode(action));
    }

    [Fact]
    public void Error_Parses_Its_Reason_Code()
    {
        var error = ProtocolParser.ParseError(ProtocolFixtures.Line("error.jsonl"));

        Assert.Equal("error", error.Type);
        Assert.Equal(ProtocolReason.StepMismatch, error.Reason);
        Assert.Equal("expected step 7, received step 3", error.Detail);
    }

    /// <summary>
    /// The context-free dispatcher routes all five types, so a caller that has not
    /// yet decided what it is reading still gets the right record.
    /// </summary>
    /// <summary>
    /// Every fixture in the conforming corpus really is conforming. The corpus
    /// doubles as the fuzz seed set and as the input to the §4.1 verbatim
    /// comparisons, so a line that is not actually valid would quietly weaken
    /// both — and a fixture carrying a field the schema does not declare would
    /// surface here as <c>unknown_field</c> rather than as a confusing failure
    /// somewhere downstream.
    /// </summary>
    [Fact]
    public void The_Conforming_Corpus_Contains_No_Unknown_Fields()
    {
        foreach (var name in ProtocolFixtures.ValidNames)
        {
            var violation = Record.Exception(() => ProtocolParser.Parse(ProtocolFixtures.Line(name)));

            Assert.True(
                violation is null,
                $"{name} is in the conforming corpus but was refused: {violation?.Message}");
        }
    }

    [Fact]
    public void Dispatch_Routes_All_Five_Types()
    {
        Assert.IsType<HelloMessage>(ProtocolParser.Parse(ProtocolFixtures.Line("hello.jsonl")));
        Assert.IsType<HelloAckMessage>(ProtocolParser.Parse(ProtocolFixtures.Line("hello_ack.jsonl")));
        Assert.IsType<ObservationMessage>(ProtocolParser.Parse(ProtocolFixtures.Line("observation.jsonl")));
        Assert.IsType<ActionMessage>(ProtocolParser.Parse(ProtocolFixtures.Line("action.jsonl")));
        Assert.IsType<ErrorMessage>(ProtocolParser.Parse(ProtocolFixtures.Line("error.jsonl")));
    }
}
