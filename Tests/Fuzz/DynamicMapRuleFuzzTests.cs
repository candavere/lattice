using System.Text.Json;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of the dynamic-topology parsing and evaluation surface:
/// <see cref="DynamicMapRuleSet"/> deserialization from mutated JSON, the
/// validating-constructor rehydration the CLI's <c>--rules</c> gate applies,
/// and rule evaluation across adversarial tick counts, choke ids, and claim
/// sets. Degenerate schedules (zero-length cycles, negative capacities,
/// negative choke/trigger ids, overflowing windows) must be rejected loudly by
/// the typed domain validators or evaluate without crashing, never escaping
/// with an unhandled runtime fault.
/// </summary>
public sealed class DynamicMapRuleFuzzTests
{
    private static readonly string GoldenRules =
        File.ReadAllText(FixtureResolver.Fixture("golden_dynamic_rules.json"));

    private static readonly string[] RuleTokens =
    {
        "ruleKind", "ChokeId", "OpenTicks", "ClosedTicks", "OpenCapacity",
        "ClosedCapacity", "TriggerResourceId", "LockedCapacity", "Rules",
    };

    private static readonly GeneratorConfig DefaultGeneratorConfig =
        new(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap);

    private static readonly int[] AdversarialTicks = { int.MinValue, -1, 0, 1, 17, int.MaxValue - 1, int.MaxValue };

    private static readonly int[][] AdversarialClaims =
    {
        Array.Empty<int>(),
        new[] { 0 },
        new[] { 1, 2, 3, 3, 3 },
        new[] { int.MaxValue, -1, 0 },
        Enumerable.Range(0, 16).ToArray(),
    };

    [Theory]
    [InlineData(2024)]
    [InlineData(8675309)]
    public void RuleParsing_RejectsOrAcceptsEveryMutation_Gracefully(int baseSeed)
    {
        FuzzHarness.Run("dynamic-rules", baseSeed, FuzzHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng(caseSeed);
            var mutated = TextMutator.Mutate(GoldenRules, rng, RuleTokens);
            var map = MapGenerator.Generate((ulong)(caseSeed ^ 0x9E3779B9U), DefaultGeneratorConfig);

            DynamicMapRuleSet? rules = null;
            FuzzHarness.ExpectGraceful("DynamicMapRuleSet deserialization", () =>
            {
                rules = JsonSerializer.Deserialize<DynamicMapRuleSet>(mutated);
            });

            if (rules is null)
            {
                return;
            }

            FuzzHarness.ExpectGraceful("DynamicMapRuleSet validating rehydration", () =>
            {
                Rehydrate(rules);
            });

            FuzzHarness.ExpectGraceful("DynamicMapRuleSet.ValidateFor", () =>
            {
                rules.ValidateFor(map);
            });

            foreach (var tick in AdversarialTicks)
            {
                foreach (var claims in AdversarialClaims)
                {
                    FuzzHarness.ExpectGraceful($"ComputeChokeCapacities(tick={tick})", () =>
                    {
                        _ = rules.ComputeChokeCapacities(tick, claims, map);
                    });
                }
            }
        });
    }

    [Theory]
    [InlineData(555)]
    [InlineData(999)]
    public void RuleConstructors_NeverCrashOnAdversarialArguments(int baseSeed)
    {
        FuzzHarness.Run("rule-constructors", baseSeed, FuzzHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng(caseSeed);
            var map = MapGenerator.Generate((ulong)(caseSeed ^ 0x5EED5EEDU), DefaultGeneratorConfig);

            var chokeId = AdversarialInt(rng);
            var openTicks = AdversarialPositive(rng);
            var closedTicks = AdversarialPositive(rng);
            var openCap = AdversarialInt(rng);
            var closedCap = AdversarialInt(rng);

            TimedPortcullisRule? portcullis = null;
            FuzzHarness.ExpectGraceful("TimedPortcullisRule constructor", () =>
            {
                portcullis = new TimedPortcullisRule(chokeId, openTicks, closedTicks, openCap, closedCap);
            });

            if (portcullis is not null)
            {
                FuzzHarness.ExpectGraceful("TimedPortcullisRule card-valid evaluation", () =>
                {
                    VerifyInDirectEvaluation(portcullis, rng, map);
                });
            }

            var eventChoke = AdversarialInt(rng);
            var triggerId = AdversarialInt(rng);
            var lockedCap = AdversarialInt(rng);
            EventLockedChokeRule? eventLock = null;
            FuzzHarness.ExpectGraceful("EventLockedChokeRule constructor", () =>
            {
                eventLock = new EventLockedChokeRule(eventChoke, triggerId, lockedCap);
            });

            if (eventLock is not null)
            {
                FuzzHarness.ExpectGraceful("EventLockedChokeRule evaluation", () =>
                {
                    VerifyInDirectEvaluation(eventLock, rng, map);
                });
            }
        });
    }

    private static void Rehydrate(DynamicMapRuleSet loaded)
    {
        var rules = new List<IDynamicMapRule>();
        foreach (var rule in loaded.Rules)
        {
            rules.Add(rule switch
            {
                TimedPortcullisRule portcullis =>
                    new TimedPortcullisRule(
                        portcullis.ChokeId,
                        portcullis.OpenTicks,
                        portcullis.ClosedTicks,
                        portcullis.OpenCapacity,
                        portcullis.ClosedCapacity),
                EventLockedChokeRule eventLock =>
                    new EventLockedChokeRule(
                        eventLock.ChokeId,
                        eventLock.TriggerResourceId,
                        eventLock.LockedCapacity),
                _ => rule,
            });
        }

        _ = new DynamicMapRuleSet(rules);
    }

    private static void VerifyInDirectEvaluation(IDynamicMapRule rule, Rng rng, MapGraph map)
    {
        foreach (var tick in AdversarialTicks)
        {
            foreach (var claims in AdversarialClaims)
            {
                for (var choke = 0; choke < map.ChokePoints.Length; choke++)
                {
                    var chokeIndex = map.ChokePoints[choke].Id;
                    _ = rule.EffectiveChokeCapacity(chokeIndex, tick, claims);
                }

                _ = rule.EffectiveChokeCapacity(AdversarialInt(rng), tick, claims);
                _ = rule.EffectiveChokeCapacity(int.MaxValue, tick, claims);
            }
        }

        var rules = new DynamicMapRuleSet(new IDynamicMapRule[] { rule });
        var capacities = rules.ComputeChokeCapacities(0, Array.Empty<int>(), map);
        foreach (var tick in AdversarialTicks)
        {
            _ = rules.ComputeChokeCapacities(tick, new[] { 0, 1, 2 }, map);
        }

        _ = new DynamicMapOverrides { Rules = rules, ChokeCapacities = capacities }.Advance(int.MaxValue, new[] { 1, 2 }, map);
    }

    private static int AdversarialInt(Rng rng) => rng.Next(0, 7) switch
    {
        0 => 0,
        1 => -1,
        2 => int.MaxValue,
        3 => int.MinValue,
        4 => rng.Next(),
        5 => -(rng.Next() + 1),
        _ => rng.Next(-1000, 1000),
    };

    private static int AdversarialPositive(Rng rng) => rng.Next(0, 7) switch
    {
        0 => 1,
        1 => int.MaxValue,
        2 => int.MaxValue / 2 + rng.Next(0, 3),
        3 => rng.Next(1, 1000),
        4 => rng.Next(1, 5),
        5 => 0,
        _ => rng.Next(1, 100),
    };
}