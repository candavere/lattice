using System.Globalization;
using System.Text.Json;
using Lattice.Agents;
using Lattice.Tui;
using EvaluationAgentLimits = Lattice.Cli.CliApp.EvaluationAgentLimits;
using EvaluationArtifact = Lattice.Cli.CliApp.EvaluationArtifact;
using EvaluationForfeit = Lattice.Cli.CliApp.EvaluationForfeit;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Reads one or more <c>evaluate --out</c> artifacts into the values the Ledger
/// screen draws: provenance, per-suite statistics, the artifact's own verdict,
/// per-seed rows, and — when an external agent was scored — that block.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the artifact's format and the drawing, and it is
/// deliberately the only place the two meet: <c>Lattice.Tui</c> holds no reference
/// to the CLI or the evaluation harness, so an artifact is read and projected here,
/// once, and everything downstream works against plain values.
/// </para>
/// <para>
/// <b>It reads the writer's own records.</b> The JSON is deserialized into the very
/// types <see cref="CliApp"/> writes the artifact from, so a change to what the
/// artifact carries breaks the compile here rather than producing a quietly empty
/// screen. Nothing is re-derived and nothing is invented: every number, and the
/// verdict itself, is the artifact's.
/// </para>
/// <para>
/// <b>Strict about what must be there, tolerant of what may be added.</b> A field the
/// reader does not know is ignored, so an artifact written by a later build still
/// shows the parts of it this build understands. A field the reader <em>needs</em> and
/// cannot find is refused by name, because drawing a row of zeroes for a missing
/// statistic would be a number the artifact never stated.
/// </para>
/// <para>
/// <b>Bounded, read-only, and one line per refusal.</b> A file past
/// <see cref="MaxArtifactBytes"/> is refused by its size rather than read, and every
/// refusal is one line naming the file and the first problem. A refusal is an
/// <see cref="InvalidDataException"/>, so it reports the runtime-failure status — the
/// same split <see cref="ReplaySource"/> makes, where a bad invocation is a usage
/// error decided before any file is opened and a bad file is discovered while doing
/// the work.
/// </para>
/// </remarks>
public static class LedgerSource
{
    /// <summary>
    /// The most bytes one artifact may occupy, in bytes.
    /// <para>
    /// Chosen as a bound rather than a target: an artifact's content is bounded by
    /// its suites and their seeds, so the canonical fifty-seed suites produce a file
    /// of a few tens of kilobytes, and even a generous many-suite run stays far below
    /// this. The point of the cap is only that a viewer cannot be handed a file large
    /// enough to exhaust the memory of the machine it is reading it on.
    /// </para>
    /// </summary>
    public const int MaxArtifactBytes = 8 * 1024 * 1024;

    /// <summary>The command as it is typed, used in a refusal so the line names what ran.</summary>
    private const string Command = "lattice tui ledger";

    /// <summary>
    /// How the artifact is deserialized. Unknown members are ignored, which is what
    /// makes a later build's extra fields harmless; matching is by exact name, so a
    /// field that was renamed rather than added is reported as missing rather than
    /// quietly read as absent.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Reads the artifacts named, in the order they were named.</summary>
    /// <exception cref="ArgumentException">No path was given.</exception>
    /// <exception cref="InvalidDataException">One of the files is not a readable artifact.</exception>
    /// <exception cref="System.IO.FileNotFoundException">One of the paths names no file.</exception>
    public static IReadOnlyList<LedgerArtifact> ReadAll(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("The Ledger needs at least one artifact to read.", nameof(paths));
        }

        var artifacts = new LedgerArtifact[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            artifacts[i] = ReadFile(paths[i]);
        }

        return artifacts;
    }

    /// <summary>Reads one artifact from a file, opened read-only.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable artifact.</exception>
    /// <exception cref="System.IO.FileNotFoundException">There is no such file.</exception>
    /// <exception cref="System.IO.IOException">The file could not be read.</exception>
    public static LedgerArtifact ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // One byte past the cap, so a file exactly on the cap is still inside it and
        // the bound is checked against what was actually read rather than against a
        // length reported before the read — a file that grew between the two would
        // otherwise be read unbounded on the strength of a stale number.
        var buffer = new byte[MaxArtifactBytes + 1];

        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = ReadAtMost(stream, buffer);
        }

        return Read(path, buffer.AsSpan(0, read));
    }

    /// <summary>
    /// Reads one artifact from bytes already in hand. The seam the in-memory cases
    /// go through, and the same projection a file goes through — there is no second
    /// reader and no second answer to "what is in this artifact".
    /// </summary>
    /// <param name="path">
    /// The path the bytes came from, named in any refusal. Only its file name is kept.
    /// </param>
    /// <param name="bytes">The artifact's bytes, exactly as they were written.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a readable artifact.</exception>
    public static LedgerArtifact Read(string path, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // The cap is enforced here rather than at the file seam, so that every way in
        // is bounded: an entry point that read whatever bytes it was handed would be a
        // hole exactly the size of the cap the reader above it enforces.
        if (bytes.Length > MaxArtifactBytes)
        {
            throw new InvalidDataException(
                $"{Command}: '{path}' is larger than the {Invariant(MaxArtifactBytes)} bytes this screen will read; " +
                "an evaluate artifact is a few tens of kilobytes, so this is not one.");
        }

        var label = Path.GetFileName(path);
        EvaluationArtifact? artifact;

        try
        {
            artifact = JsonSerializer.Deserialize<EvaluationArtifact>(bytes, Options);
        }
        catch (JsonException exception)
        {
            throw Refusal(path, Where(exception));
        }

        if (artifact is null)
        {
            throw Refusal(path, "it holds no JSON value");
        }

        return Project(path, label, artifact);
    }

    /// <summary>
    /// Reads the whole of a stream into a buffer of the given size, or as much of it
    /// as fits. Returns the number of bytes actually read, which is the buffer size
    /// when the stream has more than that — the signal the cap is checked against.
    /// </summary>
    private static int ReadAtMost(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Projects the writer's own record onto the values the screen draws, refusing at
    /// the first thing it needs and cannot find.
    /// </summary>
    private static LedgerArtifact Project(string path, string label, EvaluationArtifact artifact)
    {
        // Checked in this order deliberately: 'Studies' first, because it is what makes a
        // file an evaluation artifact, so a reader who pointed at the wrong JSON is
        // told that rather than being sent looking for a missing runtime string.
        if (artifact.Studies is not { } studies)
        {
            throw Refusal(path, "it has no 'Studies' array");
        }

        if (artifact.Cores < 1)
        {
            throw Refusal(path, "it states no 'Cores' count");
        }

        var commit = artifact.CommitSha;
        var runtime = Need(artifact.Runtime, path, "it states no 'Runtime'");
        var os = Need(artifact.Os, path, "it states no 'Os'");
        var architecture = Need(artifact.Architecture, path, "it states no 'Architecture'");

        if (artifact.CreatedAtUtc == default)
        {
            throw Refusal(path, "it states no 'CreatedAtUtc'");
        }

        return new LedgerArtifact(
            label,
            commit,
            artifact.CreatedAtUtc,
            runtime,
            os,
            artifact.Cores,
            architecture,
            [.. studies.Select((study, index) => ProjectStudy(path, index, study))],
            ProjectAgent(path, artifact));
    }

    private static LedgerStudy ProjectStudy(string path, int index, PairedStudyReport study)
    {
        var suite = Need(study.Suite, path, $"study {index} has no 'Suite'");

        if (study.PerSeed is not { } perSeed)
        {
            throw Refusal(path, $"study '{suite}' has no 'PerSeed' array");
        }

        if (study.Statistics is not { } statistics)
        {
            throw Refusal(path, $"study '{suite}' has no 'Statistics'");
        }

        return new LedgerStudy(
            suite,
            Need(study.TargetPolicy, path, $"study '{suite}' has no 'TargetPolicy'"),
            Need(study.BaselinePolicy, path, $"study '{suite}' has no 'BaselinePolicy'"),
            study.RolloutsPerAction,
            study.MaxStepsPerMatch,
            [.. perSeed.Select((seed, seedIndex) => ProjectSeed(path, suite, seedIndex, seed))],
            new LedgerStatistics(
                statistics.Seeds,
                statistics.Matches,
                statistics.MeanDelta,
                statistics.MedianDelta,
                statistics.StdDevDelta,
                statistics.IqrDelta,
                statistics.CiLower95,
                statistics.CiUpper95,
                statistics.Wins,
                statistics.Draws,
                statistics.Losses,
                statistics.Timeouts,
                statistics.WinRate,
                statistics.DrawRate,
                statistics.LossRate,
                statistics.TimeoutRate,
                statistics.MeanContentionSaturation),
            study.Passed,
            Need(study.Decision, path, $"study '{suite}' has no 'Decision'"));
    }

    /// <summary>
    /// One per-seed row. The two outcomes are decoded here, where the stored value and
    /// the seat the target policy occupied in that match are both in hand, and are
    /// refused here by name if the stored value is not one the outcome enum has.
    /// </summary>
    private static LedgerSeed ProjectSeed(string path, string suite, int seedIndex, SeedMatch seed)
    {
        // The writer's own enum, not an int: an artifact written by a build whose
        // outcome enum had more values than this one's deserializes to a value this
        // build has no name for, and it is refused here by its number rather than
        // drawn as a result nobody produced.
        var match0 = (int)seed.Match0Outcome;
        var match1 = (int)seed.Match1Outcome;

        if (!LedgerOutcomes.IsKnown(match0))
        {
            throw Refusal(
                path,
                $"study '{suite}' seed {seed.Seed} has 'Match0Outcome' {Invariant(match0)}, which is not a match outcome");
        }

        if (!LedgerOutcomes.IsKnown(match1))
        {
            throw Refusal(
                path,
                $"study '{suite}' seed {seed.Seed} has 'Match1Outcome' {Invariant(match1)}, which is not a match outcome");
        }

        return new LedgerSeed(
            seed.Seed,
            seed.PolicyScoreAtSeat0,
            seed.PolicyScoreAtSeat1,
            seed.BaselineScoreAtSeat0,
            seed.BaselineScoreAtSeat1,
            seed.MeanDelta,
            LedgerOutcomes.ForPolicySeat(match0, policyAtSeat: 0),
            LedgerOutcomes.ForPolicySeat(match1, policyAtSeat: 1));
    }

    /// <summary>
    /// The external-agent block, or <c>null</c> when the artifact carries none. The
    /// writer omits all four members on an in-process run, so absence means "no
    /// external agent was scored" and must not become a block of zeroes.
    /// </summary>
    private static LedgerAgent? ProjectAgent(string path, EvaluationArtifact artifact)
    {
        var command = artifact.AgentCommand;
        var limits = artifact.AgentLimits;
        var failures = artifact.AgentFailures;

        if (command is null && limits is null && failures is null)
        {
            return null;
        }

        // A block that names the agent without stating its limits is still a block:
        // a reader needs to see that the run happened, and "not stated" is shown as
        // not stated rather than as a budget of zero.
        var played = limits ?? new EvaluationAgentLimits(0, 0);

        return new LedgerAgent(
            failures is null
                ? []
                : [.. failures.OrderBy(failure => failure.Key, StringComparer.Ordinal)
                    .Select(failure => new LedgerAgentFailure(failure.Key, failure.Value))],
            artifact.VoidRuns ?? 0,
            command ?? [],
            new LedgerAgentLimits(played.StepTimeoutMs, played.MatchTimeoutMs),
            artifact.AgentForfeits is { } forfeits
                ? [.. forfeits.Select(ProjectForfeit)]
                : []);
    }

    private static LedgerForfeit ProjectForfeit(EvaluationForfeit forfeit) => new(
        forfeit.Seed,
        forfeit.ExternalSeat,
        forfeit.Reason ?? string.Empty,
        forfeit.PartialScoreAtSlot0,
        forfeit.PartialScoreAtSlot1,
        forfeit.ScoredExternalScore,
        forfeit.ScoredOpponentScore);

    /// <summary>
    /// A required string, or a refusal naming it. A member the writer never emits
    /// empty cannot be told from one that was omitted, so both are refused rather than
    /// silently drawn as blank.
    /// </summary>
    private static string Need(string? value, string path, string problem) =>
        value ?? throw Refusal(path, problem);

    /// <summary>
    /// Where a JSON parse stopped, in the parser's own terms: the path inside the
    /// document and the line it stopped on. The parser's full message is not
    /// reproduced — it names internal types and spans several lines of shape, and a
    /// refusal here is one line of reason for a reader, not a diagnostic dump.
    /// </summary>
    private static string Where(JsonException exception)
    {
        var location = exception.Path is { Length: > 0 } path ? $" at {path}" : string.Empty;

        return exception.LineNumber is { } line
            ? $"its JSON could not be read{location} (line {exception.LineNumber + 1})"
            : $"its JSON could not be read{location}";
    }

    private static InvalidDataException Refusal(string path, string problem) =>
        new($"{Command}: '{path}' is not a readable evaluate artifact: {problem}.");

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
