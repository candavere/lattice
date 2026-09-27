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

            // The whole point: a CR anywhere in a line is malformed_json (spec
            // section 1), so inheriting the host's CRLF default fails every match
            // before the first observation is ever answered.
            Assert.Contains("agent_failures=none", stderr, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Empty(document.RootElement.GetProperty("AgentFailures").EnumerateObject());
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
