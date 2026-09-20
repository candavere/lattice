using System.Globalization;
using System.Text;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Run-level facts for the dashboard header: which episode ran, on how large
/// a topology, for how many ticks, and how fast wall-clock execution went.
/// </summary>
public sealed record RunHeaderInfo(
    string ScenarioName,
    ulong Seed,
    int ZoneCount,
    int TotalTicks,
    double ElapsedMilliseconds)
{
    /// <summary>Simulated ticks per wall-clock second; 0 when the run was instantaneous.</summary>
    public double StepsPerSecond =>
        ElapsedMilliseconds > 0.0 ? TotalTicks / (ElapsedMilliseconds / 1000.0) : TotalTicks;
}

/// <summary>
/// One agent's row in the scoreboard: its slot, tactical role, policy
/// implementation, collected score, end-of-run status label, and how many
/// move actions it issued over the episode.
/// </summary>
public sealed record AgentScoreboardRow(
    int Slot,
    string Role,
    string Policy,
    int Score,
    string Status,
    int Steps);

/// <summary>
/// Contention metrics for one choke edge: how often agents attempted the
/// crossing and how often capacity gating denied them.
/// </summary>
public sealed record ChokeContentionRow(
    int FromZoneId,
    int ToZoneId,
    int Capacity,
    int Attempts,
    int Denials)
{
    /// <summary>Share of attempts that capacity gating denied, in [0, 1].</summary>
    public double SaturationRate => Attempts > 0 ? Denials / (double)Attempts : 0.0;
}

/// <summary>
/// Footer facts: where the trajectory was written (null when it streamed to
/// stdout), its size in bytes, whether a replay of the recorded turns
/// reproduced the exact recorded outcome, and the number of ticks that
/// verification compared (0 when verification did not complete).
/// </summary>
public sealed record OutputFooterInfo(
    string? TrajectoryPath,
    long? ByteSize,
    bool DeterminismVerified,
    int VerifiedStepCount = 0);

/// <summary>
/// Renders the end-of-run console dashboard for <c>simulate</c>: a framed run
/// header, an agent scoreboard, a choke-contention summary, and an output
/// footer. Pure <see cref="System.Text"/> formatting plus ANSI escape codes —
/// no external formatting libraries — and all output flows through a
/// caller-supplied writer so the CLI stays writer-injected and testable.
/// Deciding <i>whether</i> to render at all (quiet mode) is the caller's job;
/// this renderer always emits the full decorated dashboard.
/// </summary>
public static class SimulationConsoleRenderer
{
    internal const string Escape = "\u001b[";

    public const string Reset = Escape + "0m";
    public const string Bold = Escape + "1m";
    public const string Dim = Escape + "2m";
    public const string Red = Escape + "31m";
    public const string Green = Escape + "32m";
    public const string Yellow = Escape + "33m";
    public const string Cyan = Escape + "36m";
    public const string Gray = Escape + "90m";

    /// <summary>
    /// Writes the complete dashboard to <paramref name="sink"/> in the fixed
    /// block order: header, scoreboard, contention, footer.
    /// </summary>
    public static void RenderDashboard(
        TextWriter sink,
        RunHeaderInfo header,
        IReadOnlyList<AgentScoreboardRow> scoreboard,
        IReadOnlyList<ChokeContentionRow> contention,
        OutputFooterInfo footer)
    {
        RenderRunHeader(sink, header);
        RenderScoreboard(sink, scoreboard);
        RenderContentionSummary(sink, contention);
        RenderOutputFooter(sink, footer);
    }

    /// <summary>
    /// The framed run header: scenario, seed, topology size, executed ticks,
    /// elapsed wall time, and resulting throughput.
    /// </summary>
    public static void RenderRunHeader(TextWriter sink, RunHeaderInfo header)
    {
        var invariant = CultureInfo.InvariantCulture;
        var lines = new[]
        {
            Field("scenario", header.ScenarioName),
            Field("seed", header.Seed.ToString(invariant)),
            Field("topology", $"{header.ZoneCount} zones"),
            Field("ticks", header.TotalTicks.ToString(invariant)),
            Field("elapsed", $"{header.ElapsedMilliseconds.ToString("0.#", invariant)} ms"),
            Field("throughput", $"{header.StepsPerSecond.ToString("0.#", invariant)} steps/s"),
        };

        var width = lines.Max(line => line.Length) + 4;
        var rule = new string('═', width);
        sink.WriteLine();
        sink.WriteLine($"{Cyan}╔{rule}╗{Reset}");

        var title = "LATTICE SIMULATION RUN";
        var padding = Math.Max(0, width - title.Length);
        sink.WriteLine(
            $"{Cyan}║{Reset}{new string(' ', padding / 2)}{Bold}{title}{Reset}" +
            $"{new string(' ', padding - padding / 2)}{Cyan}║{Reset}");
        sink.WriteLine($"{Cyan}╠{rule}╣{Reset}");

        foreach (var line in lines)
        {
            sink.WriteLine($"{Cyan}║{Reset} {line.PadRight(width - 2)} {Cyan}║{Reset}");
        }

        sink.WriteLine($"{Cyan}╚{rule}╝{Reset}");
    }

    /// <summary>
    /// The aligned agent scoreboard: slot, role, policy, score, end status,
    /// and issued moves. Statuses are color-coded — green for a favorable
    /// outcome, red for an adverse one, gray for an inconclusive one.
    /// </summary>
    public static void RenderScoreboard(TextWriter sink, IReadOnlyList<AgentScoreboardRow> rows)
    {
        sink.WriteLine();
        sink.WriteLine($"{Bold}{Yellow}AGENT SCOREBOARD{Reset}");

        const string slotH = "Slot", roleH = "Role", policyH = "Policy", scoreH = "Score", statusH = "Status", stepsH = "Steps";
        var slotW = Math.Max(slotH.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Slot.ToString(CultureInfo.InvariantCulture).Length));
        var roleW = Math.Max(roleH.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Role.Length));
        var policyW = Math.Max(policyH.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Policy.Length));
        var scoreW = Math.Max(scoreH.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Score.ToString(CultureInfo.InvariantCulture).Length));
        var statusW = Math.Max(statusH.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Status.Length));

        sink.WriteLine(
            $"{Dim}  {slotH.PadRight(slotW)}  {roleH.PadRight(roleW)}  {policyH.PadRight(policyW)}" +
            $"  {scoreH.PadRight(scoreW)}  {statusH.PadRight(statusW)}  {stepsH}{Reset}");

        foreach (var row in rows)
        {
            var statusColor = StatusColor(row.Status);
            sink.WriteLine(
                $"  {row.Slot.ToString(CultureInfo.InvariantCulture).PadRight(slotW)}" +
                $"  {row.Role.PadRight(roleW)}" +
                $"  {row.Policy.PadRight(policyW)}" +
                $"  {row.Score.ToString(CultureInfo.InvariantCulture).PadRight(scoreW)}" +
                $"  {statusColor}{row.Status.PadRight(statusW)}{Reset}" +
                $"  {row.Steps}");
        }
    }

    /// <summary>
    /// The choke-contention summary: one line per contested choke with
    /// attempts, capacity denials, and the resulting saturation rate, most
    /// contested first.
    /// </summary>
    public static void RenderContentionSummary(TextWriter sink, IReadOnlyList<ChokeContentionRow> contention)
    {
        sink.WriteLine();
        sink.WriteLine($"{Bold}{Yellow}CHOKE CONTENTION{Reset}");

        if (contention.Count == 0)
        {
            sink.WriteLine($"{Gray}  no contested choke crossings this run{Reset}");
            return;
        }

        var invariant = CultureInfo.InvariantCulture;
        foreach (var row in contention.OrderByDescending(row => row.Attempts))
        {
            var rate = row.SaturationRate;
            var color = rate >= 0.5 ? Red : rate > 0.0 ? Yellow : Green;
            var capacity = row.Capacity == int.MaxValue ? "uncapped" : $"cap {row.Capacity.ToString(invariant)}";
            sink.WriteLine(
                $"  choke {row.FromZoneId} <-> {row.ToZoneId} ({capacity}):" +
                $"  attempts {row.Attempts.ToString(invariant)}" +
                $"  denials {row.Denials.ToString(invariant)}" +
                $"  {color}saturation {(rate * 100.0).ToString("0.#", invariant)}%{Reset}");
        }
    }

    /// <summary>
    /// The output footer: where the trajectory went, how large it is, and the
    /// outcome of the deterministic replay verification.
    /// </summary>
    public static void RenderOutputFooter(TextWriter sink, OutputFooterInfo footer)
    {
        sink.WriteLine();
        sink.WriteLine($"{Bold}{Yellow}OUTPUT{Reset}");

        if (footer.TrajectoryPath is { } path)
        {
            var size = footer.ByteSize is { } bytes
                ? $" ({FormatBytes(bytes)})"
                : string.Empty;
            sink.WriteLine($"  trajectory   {path}{size}");
        }
        else
        {
            sink.WriteLine($"  trajectory   {Gray}streamed to stdout{Reset}");
        }

        sink.WriteLine(footer.DeterminismVerified
            ? $"  determinism  {Green}serialized StepResult replay equivalence verified across all {footer.VerifiedStepCount} step{(footer.VerifiedStepCount == 1 ? "" : "s")}{Reset}"
            : $"  determinism  {Red}replay diverged — investigate{Reset}");
    }

    private static string Field(string label, string value) =>
        $"{Gray}{label,-11}{Reset}{value}";

    private static string StatusColor(string status) => status switch
    {
        "Extracted" or "Winner" or "Interception" => Green,
        "Intercepted" or "Defeated" or "Evaded" => Red,
        _ => Gray,
    };

    private static string FormatBytes(long bytes)
    {
        var invariant = CultureInfo.InvariantCulture;
        return bytes >= 1024
            ? $"{(bytes / 1024.0).ToString("0.#", invariant)} KiB"
            : $"{bytes.ToString(invariant)} B";
    }

    /// <summary>
    /// The number of UTF-8 bytes <paramref name="content"/> occupies on disk —
    /// the size the footer reports for a written trajectory file.
    /// </summary>
    public static long ByteCount(string content) => Encoding.UTF8.GetByteCount(content);
}
