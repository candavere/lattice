using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The Ledger's state and its keys: which artifact is on show, which suite, which
/// seed row, and whether the comparison is up. One pure value — no terminal, no clock,
/// no thread, no filesystem — so the whole of it is reachable from a list of keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cursors, one key each.</b> The study table and the per-seed pane each have
/// their own selection, because a reader scrolling one suite's fifty seed rows is not
/// choosing a different suite. Up and down move the suite; the page and home/end keys
/// move the seed row. No key does both, so no key has two meanings.
/// </para>
/// <para>
/// <b>Moving past the end stops at the end.</b> A reader who has reached the last
/// artifact and pressed Tab again has said they are done, not asked to be taken back
/// to the first — a list that wraps turns a slip into a jump.
/// </para>
/// <para>
/// <b>Compare only where there is something to compare.</b> With one artifact there are
/// no columns to put beside each other, so <c>CanCompare</c> is false and the toggle
/// has nothing to do.
/// </para>
/// </remarks>
public sealed class LedgerScreen
{
    /// <summary>How far a page key moves the seed row.</summary>
    public const int PageSize = 8;

    private readonly IReadOnlyList<LedgerArtifact> _artifacts;

    private int _artifact;
    private int _study;
    private int _seed;
    private bool _comparing;
    private bool _quit;

    private LedgerScreen(IReadOnlyList<LedgerArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count == 0)
        {
            throw new ArgumentException("The Ledger needs at least one artifact to show.", nameof(artifacts));
        }

        _artifacts = artifacts;
    }

    /// <summary>A screen over the given artifacts, at the first artifact, suite and seed.</summary>
    public static LedgerScreen For(IReadOnlyList<LedgerArtifact> artifacts) => new(artifacts);

    /// <summary>Every artifact loaded, in the order they were named.</summary>
    public IReadOnlyList<LedgerArtifact> Artifacts => _artifacts;

    /// <summary>Which artifact is on show.</summary>
    public int ArtifactIndex => _artifact;

    /// <summary>Which suite is on show.</summary>
    public int StudyIndex => _study;

    /// <summary>Which per-seed row is on show.</summary>
    public int SeedIndex => _seed;

    /// <summary>Whether the detail band is showing the comparison rather than the seed rows.</summary>
    public bool Comparing => _comparing;

    /// <summary>Whether there is a second artifact to put beside the first.</summary>
    public bool CanCompare => _artifacts.Count >= 2;

    /// <summary>Whether the reader has asked to leave.</summary>
    public bool HasQuit => _quit;

    /// <summary>The status the screen reports when the reader leaves: always success.</summary>
    public int ExitCode => 0;

    /// <summary>The artifact on show, whole.</summary>
    public LedgerArtifact Artifact => _artifacts[_artifact];

    /// <summary>The file name of the artifact on show.</summary>
    public string ArtifactLabel => Artifact.Label;

    /// <summary>The suite on show, whole, or <c>null</c> when the artifact has none.</summary>
    public LedgerStudy? Study =>
        Artifact.Studies.Count == 0
            ? null
            : Artifact.Studies[Math.Clamp(_study, 0, Artifact.Studies.Count - 1)];

    /// <summary>The suite on show, by name, or the empty string when there is none.</summary>
    public string StudySuite => Study?.Suite ?? string.Empty;

    /// <summary>The per-seed row on show, or <c>null</c> when the suite has no rows.</summary>
    public LedgerSeed? Seed
    {
        get
        {
            var seeds = Study?.PerSeed;
            return seeds is null || seeds.Count == 0 ? null : seeds[Math.Clamp(_seed, 0, seeds.Count - 1)];
        }
    }

    /// <summary>
    /// Applies one key. Every key the screen binds arrives here, so the whole of its
    /// behaviour is reachable from a list of keys and a value.
    /// </summary>
    public void Apply(TuiKey key)
    {
        if (_quit)
        {
            return;
        }

        // Ctrl-C first and from anywhere: it is the one key that always means "leave",
        // because it is the one the reader pressed to interrupt a process.
        if (key.Kind == TuiKeyKind.Interrupt)
        {
            _quit = true;
            return;
        }

        switch (key.Kind)
        {
            case TuiKeyKind.Tab:
            case TuiKeyKind.Right:
                MoveArtifact(1);
                return;

            case TuiKeyKind.BackTab:
            case TuiKeyKind.Left:
                MoveArtifact(-1);
                return;

            case TuiKeyKind.Down:
            case TuiKeyKind.Character when key.Glyph == 'j':
                MoveStudy(1);
                return;

            case TuiKeyKind.Up:
            case TuiKeyKind.Character when key.Glyph == 'k':
                MoveStudy(-1);
                return;

            case TuiKeyKind.PageDown:
                MoveSeed(PageSize);
                return;

            case TuiKeyKind.PageUp:
                MoveSeed(-PageSize);
                return;

            case TuiKeyKind.Home:
                MoveSeedTo(0);
                return;

            case TuiKeyKind.End:
                MoveSeedTo(int.MaxValue);
                return;

            case TuiKeyKind.Character when key.Glyph is 'c' or 'C':
                ToggleCompare();
                return;

            case TuiKeyKind.Escape:
            case TuiKeyKind.Character when key.Glyph is 'q' or 'Q':
                _quit = true;
                return;

            default:
                return;
        }
    }

    private void MoveArtifact(int offset)
    {
        var next = _artifact + offset;
        _artifact = next < 0 ? 0 : next >= _artifacts.Count ? _artifacts.Count - 1 : next;

        // The other two cursors belong to the suite and the seed row that is now on
        // show, so they are clamped rather than carried: a selection past the end of a
        // shorter study would point at nothing.
        ClampStudy();
        ClampSeed();
    }

    private void MoveStudy(int offset)
    {
        var studies = Artifact.Studies;
        if (studies.Count == 0)
        {
            return;
        }

        var next = _study + offset;
        _study = next < 0 ? 0 : next >= studies.Count ? studies.Count - 1 : next;
        ClampSeed();
    }

    private void MoveSeed(int offset) => MoveSeedTo(_seed + offset);

    /// <summary>
    /// Moves the seed row to <paramref name="target"/>, clamped to the suite's own rows.
    /// <see cref="int.MaxValue"/> is what the End key asks for, and the clamp is what
    /// turns it into the last row.
    /// </summary>
    private void MoveSeedTo(int target)
    {
        var seeds = Study?.PerSeed;
        if (seeds is null || seeds.Count == 0)
        {
            _seed = 0;
            return;
        }

        _seed = Math.Clamp(target, 0, seeds.Count - 1);
    }

    private void ToggleCompare()
    {
        if (CanCompare)
        {
            _comparing = !_comparing;
        }
    }

    private void ClampStudy()
    {
        var studies = Artifact.Studies;
        _study = studies.Count == 0 ? 0 : Math.Clamp(_study, 0, studies.Count - 1);
    }

    private void ClampSeed() => MoveSeedTo(_seed);
}
