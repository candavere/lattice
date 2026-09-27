using System.Diagnostics;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// Shared plumbing for the external-agent runtime tests: where the stub process
/// lives, the small deterministic maps and budgets the tests play on, and the
/// "no process left behind" assertion every spawning test ends with.
/// </summary>
/// <remarks>
/// The budgets here are <b>test-only</b> and deliberately far smaller than the
/// spec defaults. The spec's <c>step_timeout_ms = 5000</c> and the
/// <c>+ 30000</c> match slack are right for a real agent and far too slow for a
/// suite, so each test builds its own pair through
/// <see cref="ExternalTimeLimits.ComputeMatchTimeoutMs"/> — which means every test
/// still honours the §7 constraint, because the match budget is computed rather
/// than typed.
/// </remarks>
internal static class ExternalAgentTestHost
{
    /// <summary>Per-test ceiling, so a regression that hangs a test fails it instead of the run.</summary>
    public const int TestTimeoutMs = 30_000;

    /// <summary>The built stub, resolved from the test assembly's own output directory.</summary>
    public static string StubPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Lattice.ExternalAgentStub.dll");

    /// <summary>
    /// The launch for one stub run: <c>dotnet &lt;stub&gt; &lt;mode&gt; [arg]</c>.
    /// The program is the dotnet host and the stub path plus the mode are two
    /// separate argv entries, which is the §3.2 contract in use rather than in
    /// prose — and the stub's own path containing no spaces is not what makes this
    /// work, the argument list is.
    /// </summary>
    public static ExternalAgentLaunch Launch(string mode, string? argument = null)
    {
        Assert.True(
            File.Exists(StubPath),
            $"the external-agent stub was not found at '{StubPath}'. It is built by the " +
            "Lattice.Tests project and copied next to the test assembly.");

        return argument is null
            ? new ExternalAgentLaunch("dotnet", StubPath, mode)
            : new ExternalAgentLaunch("dotnet", StubPath, mode, argument);
    }

    /// <summary>A test-only time-limit pair computed the way the spec's formula requires.</summary>
    public static ExternalTimeLimits Budget(int stepTimeoutMs, int maxTicks, int slackMs) =>
        new(stepTimeoutMs, ExternalTimeLimits.ComputeMatchTimeoutMs(stepTimeoutMs, maxTicks, slackMs), maxTicks);

    /// <summary>
    /// The budget almost every test uses: a short step wait, and a match budget
    /// computed from it, so a stub that misbehaves fails fast instead of hanging.
    /// </summary>
    public static ExternalTimeLimits StandardBudget(int maxTicks = 6) =>
        Budget(stepTimeoutMs: 1_500, maxTicks, slackMs: 2_000);

    /// <summary>A two-zone, one-resource, one-choke map: small, fixed, and legal to play.</summary>
    public static MapGraph TwoZoneMap() =>
        new(
            [
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(3, 0)),
            ],
            [new ResourceNode(0, 1, new GridPoint(3, 0))],
            [new ChokePoint(0, 0, 1, MaxOccupancy: 1)]);

    /// <summary>A three-zone, two-resource map with a wider choke, for variety in the property test.</summary>
    public static MapGraph ThreeZoneMap() =>
        new(
            [
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(2, 0)),
                new Zone(2, new GridPoint(4, 0)),
            ],
            [
                new ResourceNode(0, 0, new GridPoint(0, 0)),
                new ResourceNode(1, 2, new GridPoint(4, 0)),
            ],
            [
                new ChokePoint(0, 0, 1, MaxOccupancy: 1),
                new ChokePoint(1, 1, 2, MaxOccupancy: 4),
            ]);

    /// <summary>
    /// A map with no resources at all, so every <c>Collect</c> is out of the
    /// action space. Without it a property sample could never observe a
    /// <c>Collect</c> rejection caused by an empty target range rather than by an
    /// out-of-range id.
    /// </summary>
    public static MapGraph NoResourceMap() =>
        new(
            [new Zone(0, new GridPoint(0, 0)), new Zone(1, new GridPoint(1, 0))],
            [],
            [new ChokePoint(0, 0, 1)]);

    /// <summary>
    /// A map far too large to fit in one <c>observation</c> line: enough zones
    /// that the serialized projection passes <c>max_line_bytes</c>, which is the
    /// situation §7's <c>host_limit</c> refusal exists for.
    /// </summary>
    public static MapGraph OversizedObservationMap(int zoneCount = 30_000) =>
        new(
            [.. Enumerable.Range(0, zoneCount).Select(i => new Zone(i, new GridPoint(i, 0)))],
            [],
            []);

    /// <summary>The head-to-head config every match here uses; evaluation pairings are two-agent.</summary>
    public static SimulationConfig Config(int maxTicks) => new(AgentCount: 2, MaxTicks: maxTicks);

    /// <summary>
    /// A temp file this test owns and removes on dispose.
    /// </summary>
    /// <remarks>
    /// The path is generated, not taken from a caller, and disposal removes the
    /// file if it is there. A test that mints a recording to compare bytes never
    /// has a reason to keep the file, so owning the path is what keeps a suite
    /// from growing a temp directory by two files every run. Disposal rather than
    /// a <c>finally</c> at each call site means the delete cannot be skipped by a
    /// later edit that adds an early return.
    /// </remarks>
    public sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        /// <summary>The absolute path to write to. The file does not exist yet.</summary>
        public string Path { get; }

        /// <summary>A unique path under the system temp directory.</summary>
        public static TempFile Create(string prefix, string extension) =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}{extension}"));

        /// <summary>
        /// Deletes the file if it exists, tolerating a delete that a transient
        /// lock refused.
        /// </summary>
        /// <remarks>
        /// The <see cref="IOException"/> catch is not a retry and adds no wait: it
        /// makes one attempt and, if the OS refuses it, leaves the file. That is
        /// deliberate, because the alternative is worse on Windows, where an
        /// indexer or a virus scanner holding a temp file for a moment would
        /// otherwise turn a passing determinism test into a failure that says
        /// nothing about determinism. A genuinely leaked handle is still caught,
        /// deliberately and loudly, by
        /// <c>ExternalAgentFileHandleTests</c>, which asserts the exclusive-open
        /// property directly instead of inferring it from a temp directory. Every
        /// other exception propagates: a permissions or path fault is a real
        /// problem, not noise.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                if (System.IO.File.Exists(Path))
                {
                    System.IO.File.Delete(Path);
                }
            }
            catch (IOException)
            {
                // One attempt refused by the OS; the file stays and the next run
                // uses a different path, so nothing accumulates within a run.
            }
        }
    }

    /// <summary>
    /// Asserts the child process is really gone, by asking the OS. The runner's
    /// kill-on-dispose is the thing under test, so the assertion is deliberately
    /// stronger than "dispose was called": a process id that still resolves is a
    /// leak no matter who is responsible for it.
    /// </summary>
    public static void AssertNoProcessLeft(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        // A short settle, so a kill observed a moment ago has been reaped.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var probe = Process.GetProcessById(processId);
                if (probe.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // The id no longer resolves: the process is gone.
                return;
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"external agent process {processId} was still running after the match ended.");
            }

            Thread.Sleep(25);
        }
    }
}
