namespace Lattice.Tui;

/// <summary>
/// One labelled block of a run's output, as the workspace holds it: the stream's
/// own name (<c>stdout</c>, <c>stderr</c>) and its lines unwrapped. The layout
/// wraps these at the pane width, so what the pane shows is the whole stream.
/// </summary>
public sealed record SlashConsoleResultSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// Everything the results workspace draws for one finished run as plain data: the
/// title, the effective command line, the working directory, the separated stream
/// sections, an optional one-line summary, the raw head-plus-spool lines for the
/// raw viewer, the honest limit notices, the scroll offset, and whether the raw
/// viewer is on. No terminal, no process, no clock.
/// </summary>
/// <param name="Title">The run's status line ("exit 0 ok 0.4s", "exit 1 failed 0.2s", "cancelled 0.1s").</param>
/// <param name="CommandLine">The effective command the child actually ran (executable and argv).</param>
/// <param name="WorkingDirectory">The directory the child ran in.</param>
/// <param name="Sections">The separated streams, stdout before stderr.</param>
/// <param name="RawLines">The raw viewer's lines: each stream's memory head plus its spool.</param>
/// <param name="LimitNotices">Honest per-stream loss notices; empty when nothing was lost.</param>
/// <param name="Summary">An optional one-line domain summary, or null.</param>
/// <param name="Scroll">How many body rows are scrolled off the top.</param>
/// <param name="ShowingRaw">Whether the raw viewer is on instead of the sectioned summary.</param>
public sealed record SlashConsoleResultView(
    string Title,
    string CommandLine,
    string WorkingDirectory,
    IReadOnlyList<SlashConsoleResultSection> Sections,
    IReadOnlyList<string> RawLines,
    IReadOnlyList<string> LimitNotices,
    string? Summary,
    int Scroll,
    bool ShowingRaw);
