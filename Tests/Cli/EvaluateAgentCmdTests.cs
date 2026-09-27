using System.Text.Json;
using Lattice.Cli;
using Lattice.Tests.Agents.External;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// <c>evaluate --agent-cmd</c> end to end: a real child process, the real
/// evaluator, the real artifact on disk.
/// </summary>
/// <remarks>
/// <para>
/// The stub is the C# agent from <c>Tests/ExternalAgentStub</c>, launched as
/// <c>dotnet &lt;stub&gt; &lt;mode&gt;</c>. It is a real process speaking the real
/// protocol over a real pipe, which is the only way these paths can be tested at
/// all: the properties under test are about what the CLI does with a process that
/// fails and about what it refuses to start, and a same-process fake would be
/// asserting that the fake behaves.
/// </para>
/// <para>
/// The command line is written as one string, quoted, with the stub's own path as
/// a quoted argument — so these tests exercise the §3.3 splitter and the quoted
/// path rule at the same time as the flag. The two external runs here are
/// deliberately the cheapest that still say something: a handful of seeds with
/// the smallest legal step budget, and one seed is enough to show the artifact's
/// shape.
/// </para>
/// </remarks>
public class EvaluateAgentCmdTests
{
    /// <summary>
    /// A per-test ceiling, so a regression that hangs the CLI fails this test
    /// rather than the run. Small enough to notice, large enough for a handful of
    /// dotnet-hosted child processes on a cold machine.
    /// </summary>
    private const int TestTimeoutMs = 60_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Conforming_External_Agent_Produces_A_Study_And_An_Artifact()
    {
        var path = TempPath();
        try
        {
            var (exit, stdout, stderr) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--rollouts", "2",
                "--agent-cmd", AgentCommand("conform"),
                "--agent-step-timeout-ms", "2000",
                "--out", path));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);

            // The same summary the in-process path prints, plus the external
            // counts. The verdict is the in-process one verbatim, down to the
            // wording of "not graded" at one seed: identical statistics and one
            // analyzer means an identical verdict string, and this is the line that
            // says so on the terminal rather than in the artifact.
            Assert.Contains("evaluation suite=dev", stderr, StringComparison.Ordinal);
            Assert.Contains("paired delta", stderr, StringComparison.Ordinal);
            Assert.Contains("-> FAIL", stderr, StringComparison.Ordinal);
            Assert.Contains("Not graded: 1 seeds is below the 30-seed floor", stderr, StringComparison.Ordinal);
            Assert.Contains("valid_seeds=1", stderr, StringComparison.Ordinal);
            Assert.Contains("void_runs=0", stderr, StringComparison.Ordinal);
            Assert.Contains("agent_failures=none", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var artifact = document.RootElement;

            // The statistics are the ordinary ones, over the ordinary rows.
            var study = artifact.GetProperty("Studies")[0];
            Assert.Equal("external:dotnet", study.GetProperty("TargetPolicy").GetString());
            Assert.Equal("Scout", study.GetProperty("BaselinePolicy").GetString());
            Assert.Equal(1, study.GetProperty("PerSeed").GetArrayLength());
            Assert.Equal(2, study.GetProperty("Statistics").GetProperty("Matches").GetInt32());

            // And the external fields are there, and only there: a clean run
            // reports an empty failure object and zero voids rather than omitting
            // them, so a reader never has to tell "none failed" from "not
            // reported".
            Assert.Equal(0, artifact.GetProperty("VoidRuns").GetInt32());
            Assert.Equal(
                System.Text.Json.JsonValueKind.Object,
                artifact.GetProperty("AgentFailures").ValueKind);
            Assert.Empty(artifact.GetProperty("AgentFailures").EnumerateObject());

            // The command that was scored, element for element — the program, the
            // stub path with its quotes removed, and the mode. §3.1 requires the
            // run to state what it played, and an argv is the only form of that
            // which cannot be re-parsed into something else.
            var command = artifact.GetProperty("AgentCommand").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal(["dotnet", ExternalAgentTestHost.StubPath, "conform"], command);

            var limits = artifact.GetProperty("AgentLimits");

            // §3.1 requires a reported run to state the limits it played under
            // rather than leaving them to be inferred from a version number.
            Assert.Equal(2000, limits.GetProperty("StepTimeoutMs").GetInt32());

            // match_timeout_ms is computed from the step budget, never chosen:
            // 2000 x 200 ticks + 30000 slack.
            Assert.Equal(2000 * 200 + 30_000, limits.GetProperty("MatchTimeoutMs").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Failing_External_Agent_Is_Counted_And_Scored_As_A_Loss()
    {
        var path = TempPath();
        try
        {
            var (exit, _, stderr) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--rollouts", "2",
                "--agent-cmd", AgentCommand("crash"),
                "--agent-step-timeout-ms", "2000",
                "--out", path));

            // The run itself succeeds: a failing agent is a result, not a CLI
            // error (§9.1). It is a very bad result, and the exit code is still 0
            // because nothing about the invocation was wrong.
            Assert.Equal(0, exit);
            Assert.Contains("agent_failures=agent_crashed=2", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var artifact = document.RootElement;

            var failures = artifact.GetProperty("AgentFailures");
            Assert.Single(failures.EnumerateObject());
            Assert.Equal(2, failures.GetProperty("agent_crashed").GetInt32());
            Assert.Equal(0, artifact.GetProperty("VoidRuns").GetInt32());

            // Counted as losses, twice — once per mirrored seating — and not as
            // timeouts: a protocol failure is a different event from an episode
            // that ran long, and conflating them is what the separate count exists
            // to prevent.
            var statistics = artifact.GetProperty("Studies")[0].GetProperty("Statistics");
            Assert.Equal(2, statistics.GetProperty("Losses").GetInt32());
            Assert.Equal(0, statistics.GetProperty("Timeouts").GetInt32());
            Assert.Equal(0, statistics.GetProperty("Wins").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Forfeited_Match_Records_Its_Partial_Scores_And_Scores_From_Zero()
    {
        // The §9.3 forfeit, end to end through the CLI. A crashed agent is used
        // because the stub's `crash` mode never collects anything, so its partial
        // scores are 0-0 and the forfeit is a no-op on the numbers -- which is the
        // point of pairing this with the unit tests: what is asserted here is that
        // the record is *written*, carries the seat and the reason, and states the
        // scores the study was scored from, so a reader can see how far a failure
        // got even though the row said 0.
        var path = TempPath();
        try
        {
            var (exit, _, _) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--rollouts", "2",
                "--agent-cmd", AgentCommand("crash"),
                "--agent-step-timeout-ms", "2000",
                "--out", path));

            Assert.Equal(0, exit);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var artifact = document.RootElement;

            var forfeits = artifact.GetProperty("AgentForfeits");
            Assert.Equal(2, forfeits.GetArrayLength());

            // Both mirrored seatings, each naming the seat it played so the
            // slot-indexed partials are never read as team-indexed ones.
            var seats = forfeits.EnumerateArray()
                .Select(f => f.GetProperty("ExternalSeat").GetInt32())
                .Order()
                .ToArray();
            Assert.Equal([0, 1], seats);

            foreach (var forfeit in forfeits.EnumerateArray())
            {
                Assert.Equal("agent_crashed", forfeit.GetProperty("Reason").GetString());
                Assert.Equal(0, forfeit.GetProperty("ScoredExternalScore").GetInt32());
                Assert.True(forfeit.GetProperty("Seed").GetUInt64() > 0, "the forfeit record names no seed.");
            }

            // And the statistics are computed from the forfeited rows, so a
            // crashed agent is two losses and its partials moved nothing.
            var statistics = artifact.GetProperty("Studies")[0].GetProperty("Statistics");
            Assert.Equal(2, statistics.GetProperty("Losses").GetInt32());
            Assert.Equal(0, statistics.GetProperty("Wins").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Clean_External_Run_Records_No_Forfeits_At_All()
    {
        // The mirror of the case above: nothing failed, so the field is absent
        // rather than an empty array. Absent is the honest spelling for "this run
        // had no external agent failure to describe", and it keeps a clean run's
        // artifact byte-identical to what it was before the forfeit existed.
        var path = TempPath();
        try
        {
            var (exit, _, _) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--rollouts", "2",
                "--agent-cmd", AgentCommand("conform"),
                "--agent-step-timeout-ms", "2000",
                "--out", path));

            Assert.Equal(0, exit);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(
                document.RootElement.TryGetProperty("AgentForfeits", out _),
                "a clean run wrote an AgentForfeits field.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Program_That_Does_Not_Resolve_Is_A_Usage_Error_And_Writes_No_Artifact()
    {
        var path = TempPath();
        try
        {
            var (exit, stdout, stderr) = Run(
                "evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", "lattice-no-such-agent-9f3a", "--out", path);

            Assert.Equal(UsageError.ExitCode, exit);
            Assert.Equal(string.Empty, stdout);

            // The program is named, so the reader knows what was looked for.
            Assert.Contains("lattice-no-such-agent-9f3a", stderr, StringComparison.Ordinal);
            Assert.Contains("not found on PATH", stderr, StringComparison.Ordinal);

            // Nothing ran and nothing was written: the refusal is up front, which
            // is what keeps it from costing a seed or leaving a half-study behind.
            Assert.False(File.Exists(path), "a refused --agent-cmd wrote an artifact.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Program_That_Exists_But_Cannot_Start_Is_A_Usage_Error_And_Writes_No_Artifact()
    {
        // The other half of "cannot start": the file is there, so resolution
        // succeeds, and the failure only shows up when the OS is asked to execute
        // it. That is still not a match result and still not a §8 code — no agent
        // ever spoke — so it is reported the same way and still writes nothing.
        var notExecutable = Path.Combine(Path.GetTempPath(), $"lattice-not-executable-{Guid.NewGuid():N}");
        var path = TempPath();
        File.WriteAllText(notExecutable, "this is not a program\n");
        try
        {
            var (exit, _, stderr) = Run(
                "evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", $"\"{notExecutable}\"", "--out", path);

            Assert.Equal(UsageError.ExitCode, exit);
            Assert.Contains(notExecutable, stderr, StringComparison.Ordinal);

            // Named as a launch failure rather than a resolution failure: the file
            // was found, and it was the OS that would not run it. Telling the two
            // apart is the whole reason this case is reported rather than scored.
            Assert.Contains("could not be run", stderr, StringComparison.Ordinal);
            Assert.Contains("could not start", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(path), "an unstartable --agent-cmd wrote an artifact.");
        }
        finally
        {
            File.Delete(notExecutable);
            File.Delete(path);
        }
    }

    [Fact]
    public void An_Unterminated_Quote_Is_A_Usage_Error()
    {
        var (exit, _, stderr) = Run(
            "evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", "python3 \"agent.py");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("unterminated double quote", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Empty_Command_Is_A_Usage_Error()
    {
        var (exit, _, stderr) = Run("evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", "   ");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("empty", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Step_Timeout_Below_One_Is_A_Usage_Error()
    {
        var (exit, _, stderr) = Run(
            "evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", AgentCommand("conform"), "--agent-step-timeout-ms", "0");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("--agent-step-timeout-ms", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Step_Timeout_That_Overflows_The_Match_Budget_Is_A_Usage_Error()
    {
        // match_timeout_ms is step_timeout_ms x max_ticks + 30000, and the product
        // is 64-bit before it is cast down to the int the wire carries. A step
        // budget past (int.MaxValue - 30000) / 200 therefore cannot be turned into
        // a match budget at all -- and a number Lattice cannot honour has to be
        // refused in the same class as every other unusable --agent-cmd value,
        // naming the flag, rather than surfacing as an arithmetic failure.
        const int overflowing = (int.MaxValue - 30_000) / 200 + 1;

        var (exit, _, stderr) = Run(
            "evaluate", "--seed-set", "dev", "--seeds", "1", "--agent-cmd", AgentCommand("conform"),
            "--agent-step-timeout-ms", overflowing.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("--agent-step-timeout-ms", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_InProcess_Candidate_Selector_Cannot_Be_Combined_With_An_External_One()
    {
        // §9.5. `evaluate` has no in-process candidate selector in v3.0, so the
        // rule is exercised against a selector set supplied by the test: the point
        // is that adding one later cannot silently make the two combinable.
        var error = Assert.Throws<UsageError>(() => AgentCandidateSelection.GuardAgainstInProcessSelector(
            ["evaluate", "--agent-cmd", "python3 agent.py", "--policy", "mcts"],
            selectors: ["--policy"]));

        Assert.Contains("--policy", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, UsageError.ExitCode);
    }

    [Fact]
    public void No_Conflict_Is_Reported_When_There_Is_No_Candidate_Selector()
    {
        // The v3.0 state: the selector set is empty, so nothing can conflict and
        // the guard is inert rather than wrong.
        Assert.Empty(AgentCandidateSelection.InProcessCandidateSelectors);
        AgentCandidateSelection.GuardAgainstInProcessSelector(
            ["evaluate", "--agent-cmd", "python3 agent.py"]);
    }

    [Fact]
    public void Agent_Cmd_Is_Not_A_Simulate_Flag()
    {
        // §13 and §9.5: external agents are an `evaluate` feature. A single
        // episode has no mirror, no confidence interval and no grading floor, so a
        // number from `simulate` could not be compared with a published study —
        // and the flag must not grow a second meaning under another command.
        var (exit, _, stderr) = Run("simulate", "--seed", "42", "--agent-cmd", "python3 agent.py");

        Assert.Equal(1, exit);
        Assert.Contains("unknown flag '--agent-cmd'.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void An_InProcess_Evaluate_Artifact_Carries_No_External_Fields()
    {
        // The other half of the same promise: with no --agent-cmd the artifact is
        // exactly what it was, so the golden fixture that pins it cannot go stale
        // on a field nobody asked for.
        //
        // This deliberately re-asserts the field set that the golden artifact test
        // already pins, at the cost of one more two-seed in-process run. The
        // duplication is the point: the golden test compares a committed fixture,
        // so a field added on both sides at once would satisfy it, and this one
        // asks the live artifact directly. Kept as insurance against a future
        // unconditional field rather than trimmed for the seconds it costs.
        var path = TempPath();
        try
        {
            var (exit, _, _) = Run(
                "evaluate", "--scenario", "standard", "--seed-set", "dev", "--seeds", "2", "--rollouts", "32", "--out", path);

            Assert.Equal(0, exit);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

            Assert.Equal(
                ["CommitSha", "CreatedAtUtc", "Runtime", "Os", "Cores", "Architecture", "Studies"],
                names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The stub as one command line: the dotnet host, then the stub's own path as a
    /// quoted argument, then the mode. The quoting is not decoration — the test
    /// assembly's output directory is a path like any other and may contain a
    /// space, and an unquoted one would be split into two arguments.
    /// </summary>
    private static string AgentCommand(string mode) =>
        $"dotnet \"{ExternalAgentTestHost.StubPath}\" {mode}";

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-evaluate-agent-{Guid.NewGuid():N}.json");

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
