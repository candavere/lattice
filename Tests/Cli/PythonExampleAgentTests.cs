using System.Runtime.InteropServices;
using System.Text.Json;
using Lattice.Cli;
using Xunit;
using Xunit.Abstractions;

namespace Lattice.Tests.Cli;

/// <summary>
/// The shipped Python example, run through <c>evaluate --agent-cmd</c> for real.
/// </summary>
/// <remarks>
/// <para>
/// This is the one test that proves the example is an example and not a
/// sketch: a Python interpreter, the committed script, the real protocol, sixty
/// real matches. It is also the only test that depends on anything outside this
/// repository, so it resolves the interpreter at runtime and returns early — with
/// a message saying so — when there is not one. A missing <c>python3</c> is a
/// fact about the machine, not a defect in the agent, and a test that failed for
/// it would be testing the machine.
/// </para>
/// <para>
/// No new xunit trait is used for the skip: the check is a plain runtime
/// resolution, because the thing being skipped is the absence of a program on
/// <c>PATH</c>, which is not a property of the test that can be declared up
/// front. The reason is written to the test output, which is where a reader
/// looking for a green-but-unrun test will look.
/// </para>
/// </remarks>
public class PythonExampleAgentTests
{
    /// <summary>
    /// A per-test ceiling. Generous, because this test starts one interpreter per
    /// match, and bounded, because a hang here should fail the test rather than
    /// the run.
    /// </summary>
    private const int TestTimeoutMs = 60_000;

    private readonly ITestOutputHelper _output;

    public PythonExampleAgentTests(ITestOutputHelper output) => _output = output;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_Python_Example_Plays_A_Whole_Study_Without_A_Protocol_Failure()
    {
        var python = ResolvePython();
        if (python is null)
        {
            _output.WriteLine(
                "SKIPPED: neither python3 nor python resolves on PATH on this machine, so the Python " +
                "example agent cannot be exercised here. Nothing else in this file is affected, and the " +
                "external-agent path itself is covered by the stub-based integration tests.");
            return;
        }

        var script = ExampleScript();
        var path = Path.Combine(Path.GetTempPath(), $"lattice-python-example-{Guid.NewGuid():N}.json");

        try
        {
            var (exit, stdout, stderr) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "2",
                "--agent-step-timeout-ms", "5000",
                "--agent-cmd", $"\"{python}\" \"{script}\"",
                "--out", path));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);

            // Both mirrored seatings of both seeds played, and none of them failed
            // on the wire. This is the assertion that matters: an example agent
            // that scored badly would be a weak example, and one that scored badly
            // *because it violated the protocol* would be a broken one. A single
            // timeout, crash or illegal action would show up here as a non-zero
            // failure count.
            Assert.Contains("valid_seeds=2", stderr, StringComparison.Ordinal);
            Assert.Contains("void_runs=0", stderr, StringComparison.Ordinal);
            Assert.Contains("agent_failures=none", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var artifact = document.RootElement;

            Assert.Empty(artifact.GetProperty("AgentFailures").EnumerateObject());
            Assert.Equal(0, artifact.GetProperty("VoidRuns").GetInt32());

            // The argv records the interpreter and the script, in that order.
            var command = artifact.GetProperty("AgentCommand").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal([python, script], command);

            // The example collects, so it scores: a run of all zeros would mean the
            // agent was answering legally and doing nothing, which is a different
            // bug and just as much a broken example.
            var perSeed = artifact.GetProperty("Studies")[0].GetProperty("PerSeed");
            Assert.Equal(2, perSeed.GetArrayLength());
            Assert.Contains(
                perSeed.EnumerateArray(),
                seed => seed.GetProperty("PolicyScoreAtSeat0").GetInt32() > 0
                    || seed.GetProperty("PolicyScoreAtSeat1").GetInt32() > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The example must emit bare LF whatever the host's text-mode newline
    /// default happens to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The study test above only proves this on a host whose default translates to
    /// <c>CRLF</c>, which is Windows alone. On Linux and macOS the same defect is
    /// invisible, so a change that reintroduced it would pass every green run
    /// everywhere except the one platform that notices — which is exactly how the
    /// Windows CI failure got there.
    /// </para>
    /// <para>
    /// So this test manufactures the condition instead of waiting for it. A
    /// generated wrapper sets the host default to <c>CRLF</c> — exactly what
    /// Windows does — and then runs the shipped script unmodified. On any host,
    /// an agent that pins its own newline overrides the wrapper and plays cleanly;
    /// an agent that inherits the default writes <c>CRLF</c> and every match fails
    /// as <c>malformed_json</c>, which is the failure this test exists to catch.
    /// The wrapper is generated rather than committed so the assertion is always
    /// about the shipped script, never about a fixture that can drift from it.
    /// </para>
    /// </remarks>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_Example_Emits_Bare_LF_Even_When_The_Host_Defaults_To_Crlf()
    {
        var python = ResolvePython();
        if (python is null)
        {
            _output.WriteLine(
                "SKIPPED: no Python interpreter on PATH on this machine, so the newline contract cannot be " +
                "exercised here. The example agent's own reconfigure is what this test is about.");
            return;
        }

        var script = ExampleScript();
        var wrapper = Path.Combine(
            Path.GetTempPath(), $"lattice-crlf-wrapper-{Guid.NewGuid():N}.py");
        var path = Path.Combine(Path.GetTempPath(), $"lattice-crlf-{Guid.NewGuid():N}.json");

        // The Windows default, stated explicitly so the test means the same thing
        // on every host. newline=None would translate to os.linesep, which is "\n"
        // here and "\r\n" there, so it cannot express the condition portably.
        File.WriteAllText(
            wrapper,
            "import runpy, sys\n"
            + "sys.stdout.reconfigure(encoding='utf-8', newline='\\r\\n')\n"
            + "sys.stdin.reconfigure(encoding='utf-8', newline=None)\n"
            + "sys.argv = [sys.argv[1]]\n"
            + "runpy.run_path(sys.argv[0], run_name='__main__')\n");

        try
        {
            var (exit, stdout, stderr) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--agent-step-timeout-ms", "5000",
                "--agent-cmd", $"\"{python}\" \"{wrapper}\" \"{script}\"",
                "--out", path));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);

            // Scoped to the framing contract, not to "nothing went wrong": only a CR can break this
            // newline and it surfaces as malformed_json, so a one-match host stall is a runner fact.
            Assert.DoesNotContain("malformed_json", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var failures = document.RootElement.GetProperty("AgentFailures");

            // The whole point: a CR anywhere in a line is malformed_json (spec section 1), so an
            // agent that inherits the host's CRLF default fails before the first observation.
            Assert.False(
                failures.TryGetProperty("malformed_json", out _),
                $"the agent wrote a CR-terminated line, so it did not pin its own newline: {failures}");

            // One seed mirrored is two matches and a match records at most one fault, so the summed
            // count is how many matches failed: a dead or broken agent fails both and is caught
            // here, while a single-match stall is let through.
            var failedMatches = failures.EnumerateObject().Sum(failure => failure.Value.GetInt32());
            Assert.True(
                failedMatches < 2,
                $"both matches failed, which is a broken agent rather than a single-match host stall: {failures}");
        }
        finally
        {
            File.Delete(path);
            File.Delete(wrapper);
        }
    }

    /// <summary>
    /// The inverse of the test above: an agent that does <em>not</em> pin its own newline must be
    /// caught, so the test above cannot pass merely by being blind to framing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped script runs unmodified. The generated wrapper only swaps <c>sys.stdout</c> for a
    /// stand-in whose <c>reconfigure</c> is inert, so the script keeps writing through a stream that
    /// translates to CRLF -- the state an agent written before the pin existed would have been in on
    /// Windows. Both mirrored matches then end as <c>malformed_json</c>, which is the code the test
    /// above now keys on.
    /// </para>
    /// <para>
    /// This is what keeps that assertion honest: a reader who made the reader tolerate a CR would
    /// leave the test above green on its happy path while quietly accepting a broken agent.
    /// </para>
    /// </remarks>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Agent_That_Inherits_The_Host_Newline_Is_Caught_As_Malformed_Json()
    {
        var python = ResolvePython();
        if (python is null)
        {
            _output.WriteLine(
                "SKIPPED: no Python interpreter on PATH on this machine, so the CRLF detection path cannot " +
                "be exercised here. Lattice's own framing check is what this test is about.");
            return;
        }

        var script = ExampleScript();
        var wrapper = Path.Combine(
            Path.GetTempPath(), $"lattice-crlf-inherit-{Guid.NewGuid():N}.py");
        var path = Path.Combine(Path.GetTempPath(), $"lattice-crlf-inherit-{Guid.NewGuid():N}.json");

        File.WriteAllText(
            wrapper,
            "import runpy, sys\n"
            + "sys.stdout.reconfigure(encoding='utf-8', newline='\\r\\n')\n"
            + "sys.stdin.reconfigure(encoding='utf-8', newline=None)\n"
            + "class _InheritedCrlfStdout:\n"
            + "    def __init__(self, inner):\n"
            + "        self._inner = inner\n"
            + "    def reconfigure(self, *args, **kwargs):\n"
            + "        return None\n"
            + "    def write(self, text):\n"
            + "        return self._inner.write(text)\n"
            + "    def flush(self):\n"
            + "        return self._inner.flush()\n"
            + "sys.stdout = _InheritedCrlfStdout(sys.stdout)\n"
            + "sys.argv = [sys.argv[1]]\n"
            + "runpy.run_path(sys.argv[0], run_name='__main__')\n");

        try
        {
            var (exit, stdout, stderr) = await Task.Run(() => Run(
                "evaluate",
                "--scenario", "standard",
                "--seed-set", "dev",
                "--seeds", "1",
                "--agent-step-timeout-ms", "5000",
                "--agent-cmd", $"\"{python}\" \"{wrapper}\" \"{script}\"",
                "--out", path));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);

            // Every match must be refused, and refused as a framing failure: an agent emitting CR is
            // exactly the condition the test above exists to keep working.
            Assert.Contains("malformed_json", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("agent_failures=none", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var failures = document.RootElement.GetProperty("AgentFailures");
            Assert.Equal(2, failures.GetProperty("malformed_json").GetInt32());
        }
        finally
        {
            File.Delete(path);
            File.Delete(wrapper);
        }
    }

    /// <summary>
    /// The example script, resolved from the test assembly's output directory up
    /// to the repository root. Not from the current directory: the child process
    /// inherits whatever directory the test host happens to be in, and the test
    /// should not depend on that.
    /// </summary>
    private static string ExampleScript()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "python", "lattice_agent.py"));

        Assert.True(File.Exists(path), $"the Python example agent was not found at '{path}'.");
        return path;
    }

    /// <summary>
    /// The interpreter, resolved the same way the CLI resolves an agent program,
    /// or <see langword="null"/> when this machine has none.
    /// </summary>
    private static string? ResolvePython()
    {
        foreach (var candidate in Candidates())
        {
            if (AgentProgramResolver.TryResolve(candidate, out var resolved, out _))
            {
                return resolved;
            }
        }

        // No interpreter, so nothing to run and nothing to assert. The reason goes
        // to the test output rather than to a skip trait: whether an interpreter
        // exists is a property of the machine, not something that can be declared
        // when the test is written, and a test that failed for it would be testing
        // the machine rather than the agent.
        return null;
    }

    private static IEnumerable<string> Candidates() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["python", "python3"] : ["python3", "python"];

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
