using System.Text;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// Pins the one thing the external-agent path promises about its own output: a
/// recorded match leaves nothing holding the file it was written to.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real Windows failure. <c>replay --verify</c> reads a
/// recording through a <see cref="StreamReader"/> the test owns, and an undisposed
/// one keeps the file open for the life of the test host, so the delete that
/// cleans the temporary file up throws <see cref="IOException"/>. Unix unlinks an
/// open file and never noticed, which is why the leak was invisible everywhere
/// else.
/// </para>
/// <para>
/// The assertion is an <b>exclusive open</b>, not a delete. Deleting the file to
/// see whether it can be deleted passes on two of the three operating systems
/// whatever the code under test does, so it cannot be the claim; opening the same
/// path with <see cref="FileShare.None"/> is refused by the OS while any other
/// handle is open, on every operating system .NET supports, because .NET enforces
/// the sharing mode with an advisory lock off Windows too. So this test fails on
/// all three platforms while a handle is outstanding, and it needs no retry and no
/// sleep to say so — the file is deleted immediately afterwards, once and only
/// once, and the delete is what a caller would actually do.
/// </para>
/// </remarks>
public class ExternalAgentFileHandleTests
{
    /// <summary>Fixed seed, so the recording this deletes is the recording it wrote.</summary>
    private const ulong Seed = 1001;

    private const int MaxTicks = 6;

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Recorded_Match_Leaves_No_Open_Handle_On_Its_Trajectory()
    {
        var map = ExternalAgentTestHost.TwoZoneMap();
        var config = ExternalAgentTestHost.Config(MaxTicks);
        var path = Path.Combine(Path.GetTempPath(), $"lattice-external-handle-{Guid.NewGuid():N}.jsonl");

        try
        {
            await Task.Run(() => Record(map, config, path));

            // Read it back the way the replay path does, through a reader this test
            // owns — the shape that leaked. The using is the fix; the exclusive
            // open below is what proves it took.
            TrajectoryRecording recording;
            using (var reader = new StreamReader(path))
            {
                recording = TrajectoryReader.Read(reader);
            }

            Assert.Equal(MaxTicks, recording.Steps.Length);

            // The claim under test: once the match has returned and this test's own
            // reader is disposed, nothing holds the file. A leaked handle fails here
            // with the same refusal Windows gives a delete.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            // Immediately, once, with no retry and no settle: a caller that recorded a
            // match and read it back must be able to remove the file at once.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            // Reached only when an assertion above threw. The exclusive open has
            // already proved the file was free, so this cannot be the retry of a
            // flaky delete: there is no handle left to wait for.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Plays one real external match and writes its trajectory to
    /// <paramref name="path"/>, so the file on disk is a genuine external
    /// recording rather than a fixture nothing produced.
    /// </summary>
    private static void Record(MapGraph map, SimulationConfig config, string path)
    {
        var result = ExternalMatchRunner.Run(
            map,
            config,
            ExternalAgentTestHost.Launch("conform"),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.StandardBudget(MaxTicks));

        Assert.Null(result.Fault);

        var writer = new StringWriter();
        TrajectoryWriter.Record(map, config, Seed, result.Episode!.Turns, writer);
        File.WriteAllText(path, writer.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
