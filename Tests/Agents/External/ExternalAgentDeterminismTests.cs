using System.Diagnostics;
using System.Text;
using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// The two properties that make an external agent's results mean something: the
/// wire path records exactly what an in-process agent records, and replaying that
/// recording never needs the agent again.
/// </summary>
/// <remarks>
/// §10.2's claim is that an external action is recorded "in exactly the form an
/// in-process agent's action is recorded", and §10.3's is that <c>replay</c> "MUST
/// NOT spawn, re-execute, or consult the external agent process". The first is
/// tested by byte comparison rather than by inspecting the model, because a
/// dropped field or a reordered member is invisible to a structural assertion and
/// very visible to a byte one.
/// </remarks>
public class ExternalAgentDeterminismTests
{
    /// <summary>Fixed seed, so a byte difference is reproducible rather than a flake.</summary>
    private const ulong Seed = 1001;

    private const int MaxTicks = 6;

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Conforming_External_Agent_Records_What_An_InProcess_Agent_Records()
    {
        var map = ExternalAgentTestHost.TwoZoneMap();
        var config = ExternalAgentTestHost.Config(MaxTicks);

        // Two external runs from the same seed: the same bytes, twice. This is the
        // half that is about Lattice's determinism, and it would fail if the wire
        // path introduced a wall clock, a hash-order iteration, or a
        // thread-scheduling dependency.
        //
        // The recordings are written to real files — a byte comparison against a
        // file nobody serialized would prove less — and the test owns both paths,
        // so a run leaves nothing behind in the temp directory.
        using var firstFile = ExternalAgentTestHost.TempFile.Create("lattice-ext", ".jsonl");
        using var secondFile = ExternalAgentTestHost.TempFile.Create("lattice-ext", ".jsonl");

        var first = await Task.Run(() => RecordExternalTo(map, config, firstFile.Path));
        var second = await Task.Run(() => RecordExternalTo(map, config, secondFile.Path));
        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        // The other half: the external trajectory equals the in-process one that
        // played the same policy. Same schema, same bytes, no "external" marker
        // anywhere in the recording.
        var inProcess = await Task.Run(() => RecordInProcess(map, config));

        Assert.Equal(inProcess, first);
        Assert.DoesNotContain("external", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Replay_Verifies_A_Recorded_External_Match_Without_The_Agent()
    {
        var map = ExternalAgentTestHost.TwoZoneMap();
        var config = ExternalAgentTestHost.Config(MaxTicks);

        var path = Path.Combine(Path.GetTempPath(), $"lattice-external-{Guid.NewGuid():N}.jsonl");
        try
        {
            // Recorded from a real match so the file on disk is a genuine external
            // trajectory rather than an in-memory object nobody serialized.
            await Task.Run(() => RecordExternalTo(map, config, path));

            // The library path the CLI's `replay --verify` calls. The reader is
            // this test's own and is disposed here rather than left to the
            // collector: an undisposed reader keeps the file open, and the delete
            // in the finally below then fails on Windows, where an open handle
            // cannot be unlinked. The read completes into a fully materialized
            // recording, so nothing after this point needs the file.
            TrajectoryRecording recording;
            using (var reader = new StreamReader(path))
            {
                recording = TrajectoryReader.Read(reader);
            }

            var verification = TrajectoryReplay.VerifyDetailed(recording);
            Assert.Empty(verification.Problems);
            Assert.Equal(MaxTicks, recording.Steps.Length);
            Assert.All(recording.Steps, step => Assert.NotNull(step.StateHash));

            // And the real command, in a working directory that contains no agent
            // at all. `replay` takes a file path and nothing else: there is no
            // argument through which it could name a program, so there is nothing
            // for it to spawn. Running it from an empty directory makes that
            // concrete rather than asserted.
            var sandbox = Directory.CreateTempSubdirectory("lattice-replay-sandbox");
            try
            {
                var cli = CliAssemblyPath();
                var startInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = sandbox.FullName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                startInfo.ArgumentList.Add(cli);
                startInfo.ArgumentList.Add("replay");
                startInfo.ArgumentList.Add(path);
                startInfo.ArgumentList.Add("--verify");

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("could not start the CLI for replay --verify.");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();

                Assert.True(
                    process.WaitForExit(ExternalAgentTestHost.TestTimeoutMs),
                    "`replay --verify` did not finish within the per-test budget.");
                Assert.Equal(0, process.ExitCode);

                // The verdict goes to stderr: the CLI reserves stdout for recorded
                // trajectory bytes, so a verified run writes nothing there.
                Assert.Contains("replay verified", stderr, StringComparison.Ordinal);
                Assert.Contains("state hash(es) matched", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("replay error", stderr, StringComparison.Ordinal);
                // Schema 4 without a fog side-channel emits NoPerceptionNotice
                // (same class of advisory as NoStateHashNotice on legacy files).
                Assert.Contains("replay notice", stderr, StringComparison.Ordinal);
                Assert.Contains(TrajectoryReplay.NoPerceptionNotice, stderr, StringComparison.Ordinal);
                Assert.Equal(string.Empty, stdout);
            }
            finally
            {
                sandbox.Delete(recursive: true);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task One_Process_Per_Match_Never_Crosses_Matches()
    {
        // U-8: no reuse across matches, seeds, or mirrored seatings. Each call
        // gets its own process id, and each id is gone by the time the call
        // returns.
        var map = ExternalAgentTestHost.TwoZoneMap();
        var config = ExternalAgentTestHost.Config(MaxTicks);
        var launch = ExternalAgentTestHost.Launch("conform");

        var first = await Task.Run(() => PlayExternal(map, config, launch, externalSlot: 0, seed: 1001));
        var second = await Task.Run(() => PlayExternal(map, config, launch, externalSlot: 0, seed: 1002));
        var mirrored = await Task.Run(() => PlayExternal(map, config, launch, externalSlot: 1, seed: 1001));

        foreach (var result in new[] { first, second, mirrored })
        {
            Assert.True(result.Completed);
            Assert.NotNull(result.ChildProcessId);
            Assert.True(result.ChildExited);
            ExternalAgentTestHost.AssertNoProcessLeft(result.ChildProcessId!.Value);
        }

        // Three matches, three processes. A reused process would collapse these
        // onto one id, and it is the only way agent state could cross a seed.
        Assert.Equal(3, new[] { first, mirrored, second }
            .Select(r => r.ChildProcessId!.Value)
            .Distinct()
            .Count());
    }

    private static ExternalMatchResult PlayExternal(
        MapGraph map,
        SimulationConfig config,
        ExternalAgentLaunch launch,
        int externalSlot,
        ulong seed) =>
        ExternalMatchRunner.Run(
            map,
            config,
            launch,
            externalSlot,
            new AlwaysWaitAgent(1 - externalSlot),
            seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.StandardBudget(MaxTicks));

    private static string RecordExternalTo(MapGraph map, SimulationConfig config, string path)
    {
        var result = PlayExternal(map, config, ExternalAgentTestHost.Launch("conform"), externalSlot: 0, seed: Seed);
        Assert.Null(result.Fault);

        var writer = new StringWriter();
        TrajectoryWriter.Record(map, config, Seed, result.Episode!.Turns, writer);
        var text = writer.ToString();
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return text;
    }

    private static string RecordInProcess(MapGraph map, SimulationConfig config)
    {
        // The identical policy on both seats, with no process anywhere: the pure
        // in-process path the external one is required to be indistinguishable from.
        var episode = ScenarioRunner.Run(
            map,
            config,
            [new AlwaysWaitAgent(0), new AlwaysWaitAgent(1)],
            MaxTicks);

        var writer = new StringWriter();
        TrajectoryWriter.Record(map, config, Seed, episode.Turns, writer);
        return writer.ToString();
    }

    /// <summary>
    /// The CLI assembly, resolved from the test assembly's own output directory
    /// so the replay test exercises the real command rather than a stand-in. The
    /// project is <c>Lattice.Cli</c> but the assembly is <c>lattice</c>, and it
    /// arrives in this directory complete with its <c>.runtimeconfig.json</c>,
    /// which is what lets it be launched the same way as any other app.
    /// </summary>
    private static string CliAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lattice.dll");
        Assert.True(File.Exists(path), $"the CLI was not found at '{path}'.");
        return path;
    }
}
