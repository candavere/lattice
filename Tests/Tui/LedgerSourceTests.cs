using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Reading an evaluation artifact into the values the Ledger draws: what a clean
/// artifact yields, how a match outcome is decoded from the enum the writer used,
/// and how a file that is not a readable artifact is refused in one line.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures here are written as the bytes <c>evaluate --out</c> writes, by hand
/// rather than by calling the command, so a test cannot pass because the writer and
/// the reader were changed together. The one exception is the committed-artifact
/// case at the end, which exists to prove the reader parses what is really in the
/// tree.
/// </para>
/// <para>
/// <b>Absent is absent.</b> The external-agent fields are omitted entirely unless
/// <c>--agent-cmd</c> was used, so the model treats a missing block as "this run had
/// no external agent to report on" rather than as zeroes.
/// </para>
/// </remarks>
public class LedgerSourceTests
{
    // ---------------------------------------------------------------------
    // The fixtures.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The whole artifact, exactly as an in-process <c>evaluate --out</c> writes it:
    /// the seven top-level fields and no external-agent block at all.
    /// </summary>
    private const string InProcessArtifact = """
        {
          "CommitSha": "abc1234",
          "CreatedAtUtc": "2026-09-25T19:41:20Z",
          "Runtime": ".NET 10.0.0",
          "Os": "TestOS 1.0",
          "Cores": 4,
          "Architecture": "X64",
          "Studies": [
            {
              "Suite": "heldout",
              "TargetPolicy": "MCTS",
              "BaselinePolicy": "Scout",
              "RolloutsPerAction": 32,
              "MaxStepsPerMatch": 200,
              "PerSeed": [
                {
                  "Seed": 2001,
                  "PolicyScoreAtSeat0": 1,
                  "PolicyScoreAtSeat1": 5,
                  "BaselineScoreAtSeat0": 4,
                  "BaselineScoreAtSeat1": 0,
                  "MeanDelta": 1.0,
                  "Match0Outcome": 1,
                  "Match1Outcome": 1
                },
                {
                  "Seed": 2002,
                  "PolicyScoreAtSeat0": 3,
                  "PolicyScoreAtSeat1": 2,
                  "BaselineScoreAtSeat0": 3,
                  "BaselineScoreAtSeat1": 2,
                  "MeanDelta": 0.0,
                  "Match0Outcome": 2,
                  "Match1Outcome": 2
                }
              ],
              "Statistics": {
                "Seeds": 2,
                "Matches": 4,
                "MeanDelta": 0.5,
                "MedianDelta": 0.5,
                "StdDevDelta": 0.7071067811865476,
                "IqrDelta": 0.5,
                "CiLower95": -0.5,
                "CiUpper95": 1.5,
                "Wins": 1,
                "Draws": 2,
                "Losses": 1,
                "Timeouts": 0,
                "WinRate": 0.25,
                "DrawRate": 0.5,
                "LossRate": 0.25,
                "TimeoutRate": 0.0,
                "MeanContentionSaturation": 0.1586165454915455
              },
              "Passed": true,
              "Decision": "Pass: mean paired delta 0.5 > 0 and the 95% CI lower bound -0.5 > 0 on 2 seeds."
            }
          ]
        }
        """;

    /// <summary>
    /// An artifact written with <c>--agent-cmd</c>: the external-agent block is
    /// present, with failures by reason, void runs, the argv, the limits, and two
    /// forfeited matches.
    /// </summary>
    private const string ExternalArtifact = """
        {
          "CommitSha": "def5678",
          "CreatedAtUtc": "2026-09-26T08:00:00Z",
          "Runtime": ".NET 10.0.0",
          "Os": "TestOS 1.0",
          "Cores": 2,
          "Architecture": "Arm64",
          "Studies": [
            {
              "Suite": "dev",
              "TargetPolicy": "MCTS",
              "BaselinePolicy": "Scout",
              "RolloutsPerAction": 8,
              "MaxStepsPerMatch": 100,
              "PerSeed": [
                {
                  "Seed": 1001,
                  "PolicyScoreAtSeat0": 0,
                  "PolicyScoreAtSeat1": 0,
                  "BaselineScoreAtSeat0": 0,
                  "BaselineScoreAtSeat1": 0,
                  "MeanDelta": 0.0,
                  "Match0Outcome": 3,
                  "Match1Outcome": 3
                }
              ],
              "Statistics": {
                "Seeds": 1,
                "Matches": 2,
                "MeanDelta": 0.0,
                "MedianDelta": 0.0,
                "StdDevDelta": 0.0,
                "IqrDelta": 0.0,
                "CiLower95": 0.0,
                "CiUpper95": 0.0,
                "Wins": 0,
                "Draws": 0,
                "Losses": 0,
                "Timeouts": 2,
                "WinRate": 0.0,
                "DrawRate": 0.0,
                "LossRate": 0.0,
                "TimeoutRate": 1.0,
                "MeanContentionSaturation": 0.0
              },
              "Passed": false,
              "Decision": "Fail: mean paired delta 0 and/or the 95% CI lower bound 0 did not clear 0 on 1 seeds."
            }
          ],
          "AgentFailures": { "timeout_step": 1, "agent_crashed": 2 },
          "VoidRuns": 3,
          "AgentCommand": ["python3", "agent.py"],
          "AgentLimits": { "StepTimeoutMs": 5000, "MatchTimeoutMs": 20000 },
          "AgentForfeits": [
            {
              "Seed": 1001,
              "ExternalSeat": 0,
              "Reason": "timeout_step",
              "PartialScoreAtSlot0": 4,
              "PartialScoreAtSlot1": 1,
              "ScoredExternalScore": 0,
              "ScoredOpponentScore": 3
            },
            {
              "Seed": 1001,
              "ExternalSeat": 1,
              "Reason": "agent_crashed",
              "PartialScoreAtSlot0": 2,
              "PartialScoreAtSlot1": 0,
              "ScoredExternalScore": 0,
              "ScoredOpponentScore": 5
            }
          ]
        }
        """;

    /// <summary>
    /// Both suites in one artifact, so the reader has to keep two studies apart
    /// rather than collapsing them into one set of numbers.
    /// </summary>
    private const string TwoSuiteArtifact = """
        {
          "CommitSha": "aaa0000",
          "CreatedAtUtc": "2026-09-26T08:00:00Z",
          "Runtime": ".NET 10.0.0",
          "Os": "TestOS 1.0",
          "Cores": 1,
          "Architecture": "X64",
          "Studies": [
            {
              "Suite": "dev",
              "TargetPolicy": "MCTS",
              "BaselinePolicy": "Scout",
              "RolloutsPerAction": 4,
              "MaxStepsPerMatch": 50,
              "PerSeed": [],
              "Statistics": {
                "Seeds": 0, "Matches": 0, "MeanDelta": 0.0, "MedianDelta": 0.0,
                "StdDevDelta": 0.0, "IqrDelta": 0.0, "CiLower95": 0.0, "CiUpper95": 0.0,
                "Wins": 0, "Draws": 0, "Losses": 0, "Timeouts": 0,
                "WinRate": 0.0, "DrawRate": 0.0, "LossRate": 0.0, "TimeoutRate": 0.0,
                "MeanContentionSaturation": 0.0
              },
              "Passed": false,
              "Decision": "Not graded: 0 seeds is below the 30-seed floor of the decision rule (the canonical suites run 50)."
            },
            {
              "Suite": "heldout",
              "TargetPolicy": "MCTS",
              "BaselinePolicy": "Scout",
              "RolloutsPerAction": 4,
              "MaxStepsPerMatch": 50,
              "PerSeed": [],
              "Statistics": {
                "Seeds": 0, "Matches": 0, "MeanDelta": 0.0, "MedianDelta": 0.0,
                "StdDevDelta": 0.0, "IqrDelta": 0.0, "CiLower95": 0.0, "CiUpper95": 0.0,
                "Wins": 0, "Draws": 0, "Losses": 0, "Timeouts": 0,
                "WinRate": 0.0, "DrawRate": 0.0, "LossRate": 0.0, "TimeoutRate": 0.0,
                "MeanContentionSaturation": 0.0
              },
              "Passed": false,
              "Decision": "Not graded: 0 seeds is below the 30-seed floor of the decision rule (the canonical suites run 50)."
            }
          ]
        }
        """;

    // ---------------------------------------------------------------------
    // An artifact from an in-process run.
    // ---------------------------------------------------------------------

    [Fact]
    public void AnInProcessArtifactYieldsItsProvenanceItsStudyAndItsVerdict()
    {
        var artifact = LedgerSource.Read("results.json", Bytes(InProcessArtifact));

        Assert.Equal("results.json", artifact.Label);
        Assert.Equal("abc1234", artifact.CommitSha);
        Assert.Equal(new DateTime(2026, 9, 25, 19, 41, 20, DateTimeKind.Utc), artifact.CreatedAtUtc);
        Assert.Equal(".NET 10.0.0", artifact.Runtime);
        Assert.Equal("TestOS 1.0", artifact.Os);
        Assert.Equal(4, artifact.Cores);
        Assert.Equal("X64", artifact.Architecture);

        var study = Assert.Single(artifact.Studies);
        Assert.Equal("heldout", study.Suite);
        Assert.Equal("MCTS", study.TargetPolicy);
        Assert.Equal("Scout", study.BaselinePolicy);
        Assert.Equal(32, study.RolloutsPerAction);
        Assert.Equal(200, study.MaxStepsPerMatch);

        // The artifact's own verdict, carried as the artifact's: the reader does not
        // re-grade the study and the screen does not either.
        Assert.True(study.Passed);
        Assert.StartsWith("Pass:", study.Decision, StringComparison.Ordinal);

        Assert.Equal(2, study.Statistics.Seeds);
        Assert.Equal(4, study.Statistics.Matches);
        Assert.Equal(0.5, study.Statistics.MeanDelta);
        Assert.Equal(-0.5, study.Statistics.CiLower95);
        Assert.Equal(1.5, study.Statistics.CiUpper95);
        Assert.Equal(1, study.Statistics.Wins);
        Assert.Equal(2, study.Statistics.Draws);
        Assert.Equal(1, study.Statistics.Losses);
        Assert.Equal(0, study.Statistics.Timeouts);
        Assert.Equal(0.25, study.Statistics.WinRate);
        Assert.Equal(0.1586165454915455, study.Statistics.MeanContentionSaturation);
    }

    /// <summary>
    /// An in-process artifact omits the external-agent block entirely, and the reader
    /// must not turn that absence into zeroes: it means this run had no external
    /// agent, which is a different statement from "no failure, no void run, no
    /// forfeit".
    /// </summary>
    [Fact]
    public void AnArtifactWithNoExternalAgentBlockSaysSoRatherThanShowingZeroes()
    {
        var artifact = LedgerSource.Read("results.json", Bytes(InProcessArtifact));

        Assert.Null(artifact.Agent);
    }

    [Fact]
    public void AnExternalAgentArtifactKeepsItsFailuresItsVoidRunsItsLimitsAndItsForfeits()
    {
        var agent = LedgerSource.Read("results.json", Bytes(ExternalArtifact)).Agent;

        Assert.NotNull(agent);

        // Failures by reason, ordered by reason so two loads of the same file draw
        // the same rows.
        Assert.Equal(
            [("agent_crashed", 2), ("timeout_step", 1)],
            agent!.Failures.Select(failure => (failure.Reason, failure.Count)).ToArray());

        Assert.Equal(3, agent.VoidRuns);
        Assert.Equal(["python3", "agent.py"], agent.Command);
        Assert.Equal(5000, agent.Limits.StepTimeoutMs);
        Assert.Equal(20000, agent.Limits.MatchTimeoutMs);

        Assert.Equal(2, agent.Forfeits.Count);
        Assert.Equal(1001UL, agent.Forfeits[0].Seed);
        Assert.Equal(0, agent.Forfeits[0].ExternalSeat);
        Assert.Equal("timeout_step", agent.Forfeits[0].Reason);
        Assert.Equal(4, agent.Forfeits[0].PartialScoreAtSlot0);
        Assert.Equal(1, agent.Forfeits[0].PartialScoreAtSlot1);
        Assert.Equal(0, agent.Forfeits[0].ScoredExternalScore);
        Assert.Equal(3, agent.Forfeits[0].ScoredOpponentScore);
        Assert.Equal(1, agent.Forfeits[1].ExternalSeat);
        Assert.Equal("agent_crashed", agent.Forfeits[1].Reason);
    }

    /// <summary>
    /// The block is present only when the run had an external agent, so its presence
    /// is what distinguishes "scored an external agent" from "did not". A block whose
    /// forfeit array was omitted carries no forfeits rather than an empty one, because
    /// the writer omits that array by design on a clean run.
    /// </summary>
    [Fact]
    public void AnExternalAgentBlockWithNoForfeitsCarriesNone()
    {
        const string json = """
            {
              "CommitSha": "aaa0000",
              "CreatedAtUtc": "2026-09-26T08:00:00Z",
              "Runtime": ".NET 10.0.0",
              "Os": "TestOS 1.0",
              "Cores": 1,
              "Architecture": "X64",
              "Studies": [],
              "AgentFailures": {},
              "VoidRuns": 0,
              "AgentCommand": ["agent"],
              "AgentLimits": { "StepTimeoutMs": 10, "MatchTimeoutMs": 40 }
            }
            """;

        var agent = LedgerSource.Read("results.json", Bytes(json)).Agent;

        Assert.NotNull(agent);
        Assert.Empty(agent!.Failures);
        Assert.Empty(agent.Forfeits);
        Assert.Equal(0, agent.VoidRuns);
    }

    [Fact]
    public void ATwoSuiteArtifactKeepsBothStudiesApart()
    {
        var artifact = LedgerSource.Read("results.json", Bytes(TwoSuiteArtifact));

        Assert.Equal(["dev", "heldout"], artifact.Studies.Select(study => study.Suite).ToArray());
    }

    // ---------------------------------------------------------------------
    // Outcome decoding, from the enum the writer used.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A match's outcome is stored from Team A's side, and the two mirrored matches
    /// put the target policy in opposite seats — so the same stored value means
    /// opposite things in the two columns. This is the mapping
    /// <c>PairedStudy.PolicyOutcome</c> applies, and getting it backwards would
    /// report a loss as a win on every seed.
    /// </summary>
    [Fact]
    public void AMatchOutcomeIsDecodedFromThePolicysOwnSeat()
    {
        Assert.Equal(LedgerOutcome.PolicyLoss, LedgerOutcomes.ForPolicySeat(1, policyAtSeat: 0));
        Assert.Equal(LedgerOutcome.PolicyWin, LedgerOutcomes.ForPolicySeat(1, policyAtSeat: 1));

        Assert.Equal(LedgerOutcome.PolicyWin, LedgerOutcomes.ForPolicySeat(0, policyAtSeat: 0));
        Assert.Equal(LedgerOutcome.PolicyLoss, LedgerOutcomes.ForPolicySeat(0, policyAtSeat: 1));

        // Draw and Timeout do not depend on the seat.
        Assert.Equal(LedgerOutcome.Draw, LedgerOutcomes.ForPolicySeat(2, policyAtSeat: 0));
        Assert.Equal(LedgerOutcome.Draw, LedgerOutcomes.ForPolicySeat(2, policyAtSeat: 1));
        Assert.Equal(LedgerOutcome.Timeout, LedgerOutcomes.ForPolicySeat(3, policyAtSeat: 0));
        Assert.Equal(LedgerOutcome.Timeout, LedgerOutcomes.ForPolicySeat(3, policyAtSeat: 1));
    }

    [Fact]
    public void ThePerSeedRowsCarryTheDecodedOutcomeOfEachMatch()
    {
        var seeds = LedgerSource.Read("results.json", Bytes(InProcessArtifact)).Studies[0].PerSeed;

        Assert.Equal(2, seeds.Count);
        Assert.Equal(2001UL, seeds[0].Seed);
        Assert.Equal(1, seeds[0].PolicyScoreAtSeat0);
        Assert.Equal(5, seeds[0].PolicyScoreAtSeat1);
        Assert.Equal(4, seeds[0].BaselineScoreAtSeat0);
        Assert.Equal(0, seeds[0].BaselineScoreAtSeat1);
        Assert.Equal(1.0, seeds[0].MeanDelta);

        // Stored 1 (TeamBWin) both times: a loss in the seat the target took first,
        // a win in the seat it took second.
        Assert.Equal(LedgerOutcome.PolicyLoss, seeds[0].Match0);
        Assert.Equal(LedgerOutcome.PolicyWin, seeds[0].Match1);

        // Stored 2 (Draw) both times.
        Assert.Equal(LedgerOutcome.Draw, seeds[1].Match0);
        Assert.Equal(LedgerOutcome.Draw, seeds[1].Match1);
    }

    /// <summary>
    /// The decode has to agree with the analyzer that produced the artifact, not with
    /// a reading of the enum's names. The artifact's own wins, draws, losses and
    /// timeouts are recounted from the per-seed rows under the rule the study used,
    /// and the two sets must be the same four numbers.
    /// </summary>
    [Fact]
    public void TheDecodedOutcomesReproduceTheArtifactsOwnOutcomeCounts()
    {
        var study = LedgerSource.Read("results.json", Bytes(ExternalArtifact)).Studies[0];
        var outcomes = study.PerSeed.SelectMany(seed => new[] { seed.Match0, seed.Match1 }).ToArray();

        Assert.Equal(study.Statistics.Wins, outcomes.Count(outcome => outcome == LedgerOutcome.PolicyWin));
        Assert.Equal(study.Statistics.Losses, outcomes.Count(outcome => outcome == LedgerOutcome.PolicyLoss));
        Assert.Equal(study.Statistics.Draws, outcomes.Count(outcome => outcome == LedgerOutcome.Draw));
        Assert.Equal(study.Statistics.Timeouts, outcomes.Count(outcome => outcome == LedgerOutcome.Timeout));
    }

    /// <summary>
    /// A stored value the enum does not have cannot be decoded, and inventing a
    /// meaning for it would put a word on screen that no analyzer ever produced.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void AnOutcomeTheEnumDoesNotHaveIsRefusedRatherThanGuessedAt(int stored)
    {
        var json = InProcessArtifact.Replace("\"Match0Outcome\": 1", $"\"Match0Outcome\": {stored}", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => LedgerSource.Read("results.json", Bytes(json)));

        Assert.Contains("Match0Outcome", error.Message, StringComparison.Ordinal);
        Assert.Contains("results.json", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Malformed, wrong-shaped and oversize files.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A file that is not JSON at all is refused in one line that names the file.
    /// It is a runtime failure, not a usage error: the invocation named a file, and
    /// it turned out not to be readable.
    /// </summary>
    [Fact]
    public void AMalformedFileIsRefusedInOneLineNamingIt()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => LedgerSource.Read("broken.json", Bytes("{ not json at all")));

        // One line: a refusal that wrapped would put several lines of parser shape on
        // a reader's terminal where one line of reason belongs.
        Assert.DoesNotContain('\n', error.Message);
        Assert.Contains("broken.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("lattice tui ledger", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWhoseStudiesIsNotAnArrayIsRefusedByName()
    {
        var json = InProcessArtifact.Replace(
            "\"Studies\": [",
            "\"Studies\": \"heldout\",\n  \"Ignored\": [",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => LedgerSource.Read("wrong.json", Bytes(json)));

        Assert.Contains("wrong.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("Studies", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Valid JSON that is not an evaluation artifact is refused by the first thing
    /// that is missing, rather than loading as an artifact with no studies — which
    /// would put an empty screen in front of a reader who named a real file.
    /// </summary>
    [Fact]
    public void AJsonFileThatIsNotAnEvaluationArtifactIsRefusedByWhatIsMissing()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => LedgerSource.Read("other.json", Bytes("""{"CommitSha":"abc"}""")));

        Assert.Contains("other.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("Studies", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A study with no statistics cannot be shown, so it is refused by name rather
    /// than drawn as a row of zeroes.
    /// </summary>
    [Fact]
    public void AStudyMissingItsStatisticsIsRefusedByName()
    {
        var json = InProcessArtifact.Replace("\"Statistics\": {", "\"NotStatistics\": {", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => LedgerSource.Read("nostats.json", Bytes(json)));

        Assert.Contains("nostats.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("Statistics", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file past the cap is refused by its size and not read into memory. A viewer
    /// that opened an arbitrarily large file would be a way to run a machine out of
    /// memory, not a way to read a result.
    /// </summary>
    [Fact]
    public void AFileLargerThanTheCapIsRefusedAndSaysWhatTheCapIs()
    {
        var oversized = new byte[LedgerSource.MaxArtifactBytes + 1];

        var error = Assert.Throws<InvalidDataException>(() => LedgerSource.Read("huge.json", oversized));

        Assert.Contains("huge.json", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            LedgerSource.MaxArtifactBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("bytes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file exactly on the cap is read, not refused: the cap is a bound, and a file
    /// at the bound is inside it.
    /// </summary>
    [Fact]
    public void AFileExactlyOnTheCapIsStillRead()
    {
        var padding = LedgerSource.MaxArtifactBytes - InProcessArtifact.Length;
        var bytes = Encoding.UTF8.GetBytes(InProcessArtifact + new string(' ', padding));

        Assert.Equal(LedgerSource.MaxArtifactBytes, bytes.Length);
        Assert.Single(LedgerSource.Read("exact.json", bytes).Studies);
    }

    // ---------------------------------------------------------------------
    // Tolerance.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A field the reader does not know about is ignored, not refused: an artifact
    /// written by a later build has to stay readable, or the screen would refuse to
    /// show the parts of a newer result it does understand.
    /// </summary>
    [Fact]
    public void AFieldTheReaderDoesNotKnowAboutIsIgnored()
    {
        var json = InProcessArtifact.Replace(
            "\"Cores\": 4,",
            "\"Cores\": 4, \"FutureField\": { \"nested\": [1, 2, 3] }, \"AnotherOne\": null,",
            StringComparison.Ordinal);

        var artifact = LedgerSource.Read("newer.json", Bytes(json));

        Assert.Equal(4, artifact.Cores);
        Assert.Equal("heldout", Assert.Single(artifact.Studies).Suite);
    }

    // ---------------------------------------------------------------------
    // Reading several files, in the order they were named.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Several artifacts load in the order they were named, which is the order the
    /// compare pane puts them in columns and the order the reader chose.
    /// </summary>
    [Fact]
    public void SeveralFilesLoadInTheOrderTheyWereGiven()
    {
        WithSandbox(directory =>
        {
            var artifacts = LedgerSource.ReadAll(
            [
                Write(directory, "first.json", InProcessArtifact),
                Write(directory, "second.json", TwoSuiteArtifact),
            ]);

            Assert.Equal(["first.json", "second.json"], artifacts.Select(artifact => artifact.Label).ToArray());
            Assert.Single(artifacts[0].Studies);
            Assert.Equal(2, artifacts[1].Studies.Count);
        });
    }

    /// <summary>
    /// One bad file fails the whole load before any screen is opened, and says which
    /// file it was: a reader given two files and a refusal needs to know which one to
    /// replace.
    /// </summary>
    [Fact]
    public void OneBadFileAmongSeveralFailsTheRunAndNamesIt()
    {
        WithSandbox(directory =>
        {
            Write(directory, "good.json", InProcessArtifact);
            var bad = Write(directory, "bad.json", "{ not json");
            Write(directory, "alsogood.json", TwoSuiteArtifact);

            var error = Assert.Throws<InvalidDataException>(() => LedgerSource.ReadAll(
            [
                Path.Combine(directory, "good.json"),
                bad,
                Path.Combine(directory, "alsogood.json"),
            ]));

            Assert.Contains("bad.json", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("good.json", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ThereIsNoSuchThingAsNoFilesToRead()
    {
        var error = Assert.Throws<ArgumentException>(() => LedgerSource.ReadAll([]));

        Assert.Equal("paths", error.ParamName);
    }

    // ---------------------------------------------------------------------
    // Read-only, proved rather than claimed.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reading changes nothing on disk. The sandbox already holds a file the load
    /// does not name, so a reader that merely created or removed something would be
    /// visible here; the artifact itself is compared before and after, so a reader
    /// that rewrote it would be visible too.
    /// </summary>
    [Fact]
    public void ReadingAnArtifactCreatesAndWritesNoFile()
    {
        WithSandbox(directory =>
        {
            var path = Write(directory, "results.json", InProcessArtifact);
            var before = Listing(directory);

            LedgerSource.ReadFile(path);

            // The listing is taken after the artifact exists, so this is exactly the
            // claim that reading added or removed nothing: keep.txt was there before
            // the read and results.json was too.
            Assert.Equal(before, Listing(directory));
            Assert.Equal(InProcessArtifact, File.ReadAllText(path));
        });
    }

    /// <summary>
    /// A reader with no writer in its signature cannot print, and with no process,
    /// session or key-source seam in its signature cannot start anything. Asserted
    /// over the reader's own public surface, because that surface is what a later
    /// change would have to widen to break this.
    /// </summary>
    [Fact]
    public void TheReaderHasNoWayToWriteOrToStartAnything()
    {
        var forbidden = new[]
        {
            typeof(TextWriter),
            typeof(Stream),
            typeof(System.Diagnostics.Process),
            typeof(IKeySource),
            typeof(ITerminalSessionFactory),
        };

        var offenders = typeof(LedgerSource)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => forbidden.Any(kind => kind.IsAssignableFrom(parameter.ParameterType)))
            .Select(parameter => $"{parameter.Member?.Name}.{parameter.Name}: {parameter.ParameterType.Name}")
            .ToArray();

        Assert.Equal([], offenders);
    }

    // ---------------------------------------------------------------------
    // The committed artifacts really do parse.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The two evaluation artifacts committed under <c>benchmarks/</c> are the ones
    /// this screen exists to show, so they are read to prove the reader handles them.
    /// Nothing about their host fields is asserted and none of their bytes are copied
    /// anywhere: the claim is "they parse", not what they contain.
    /// </summary>
    [Theory]
    [InlineData("bottleneck_evaluation_results.json")]
    [InlineData("mcts_evaluation_results.json")]
    public void ACommittedEvaluationArtifactParses(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "benchmarks", name);
        Assert.True(File.Exists(path), $"{path} is missing.");

        var artifact = LedgerSource.ReadFile(path);

        Assert.NotEmpty(artifact.Studies);

        foreach (var study in artifact.Studies)
        {
            Assert.NotEqual("", study.Suite);
            Assert.NotEqual("", study.Decision);

            // The per-seed rows and the statistics agree on how many seeds there
            // were: the one internal consistency a reader can check without
            // re-grading the study.
            Assert.Equal(study.Statistics.Seeds, study.PerSeed.Count);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string Write(string directory, string name, string json)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// A temporary directory of this test's own, removed afterwards however the body
    /// ends. Every path these tests hand the reader is inside it.
    /// </summary>
    private static void WithSandbox(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lattice-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "not an artifact");

        try
        {
            body(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Listing(string directory) =>
        string.Join('\n', Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
