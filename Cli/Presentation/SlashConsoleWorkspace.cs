using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The results workspace translates a finished <see cref="CapturedRunResult"/> or a
/// message into a <see cref="SlashConsoleResultView"/>. It tracks scroll and raw
/// mode, and provides small actions (scroll, toggle raw, save). Pure: no terminal,
/// no clock, no process.
/// </summary>
public sealed class SlashConsoleWorkspace
{
    private int _scroll;
    private bool _showingRaw;
    private readonly string _title;
    private readonly string _commandLine;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<SlashConsoleResultSection> _sections;
    private readonly IReadOnlyList<string> _rawLines;
    private readonly IReadOnlyList<string> _limitNotices;
    private readonly string? _summary;

    // For saving: keep the raw data so 's' can persist the full head+spool.
    private readonly string _stdoutHead;
    private readonly string? _stdoutSpoolPath;
    private readonly string _stderrHead;
    private readonly string? _stderrSpoolPath;
    private readonly IReadOnlyList<string> _headerLines;
    private readonly string _commandDisplay;
    private readonly string _cwdForSave;

    private SlashConsoleWorkspace(
        string title,
        string commandLine,
        string workingDirectory,
        IReadOnlyList<SlashConsoleResultSection> sections,
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> limitNotices,
        string? summary,
        string stdoutHead,
        string? stdoutSpoolPath,
        string stderrHead,
        string? stderrSpoolPath,
        IReadOnlyList<string> headerLines,
        string commandDisplay,
        string cwdForSave)
    {
        _title = title ?? string.Empty;
        _commandLine = commandLine ?? string.Empty;
        _workingDirectory = workingDirectory ?? string.Empty;
        _sections = sections ?? Array.Empty<SlashConsoleResultSection>();
        _rawLines = rawLines ?? Array.Empty<string>();
        _limitNotices = limitNotices ?? Array.Empty<string>();
        _summary = summary;
        _stdoutHead = stdoutHead ?? string.Empty;
        _stdoutSpoolPath = stdoutSpoolPath;
        _stderrHead = stderrHead ?? string.Empty;
        _stderrSpoolPath = stderrSpoolPath;
        _headerLines = headerLines ?? Array.Empty<string>();
        _commandDisplay = commandDisplay ?? string.Empty;
        _cwdForSave = cwdForSave ?? string.Empty;
        _scroll = 0;
        _showingRaw = false;
    }

    public static SlashConsoleWorkspace FromRun(
        CapturedRunResult result,
        string command,
        string displayLine)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(command);

        var effectiveCommand = string.IsNullOrWhiteSpace(result.EffectiveCommand)
            ? (string.IsNullOrWhiteSpace(displayLine) ? command : displayLine)
            : result.EffectiveCommand;

        var workingDir = string.IsNullOrWhiteSpace(result.WorkingDirectory)
            ? string.Empty
            : result.WorkingDirectory;

        var title = BuildTitle(result);

        var stdoutLines = SplitLines(result.Stdout);
        var stderrLines = SplitLines(result.Stderr);
        if (stdoutLines.Count == 0 || (stdoutLines.Count == 1 && string.IsNullOrEmpty(stdoutLines[0])))
        {
            stdoutLines = new[] { "(empty)" };
        }

        if (stderrLines.Count == 0 || (stderrLines.Count == 1 && string.IsNullOrEmpty(stderrLines[0])))
        {
            stderrLines = new[] { "(empty)" };
        }

        var sections = new List<SlashConsoleResultSection>
        {
            new("stdout", stdoutLines),
            new("stderr", stderrLines)
        };

        var rawLinesList = new List<string>();
        if (!string.IsNullOrEmpty(result.Stdout) || result.StdoutSpoolPath is not null)
        {
            rawLinesList.AddRange(SplitLines(result.Stdout));
            if (!string.IsNullOrEmpty(result.StdoutSpoolPath))
            {
                try
                {
                    if (File.Exists(result.StdoutSpoolPath))
                    {
                        rawLinesList.AddRange(SplitLines(File.ReadAllText(result.StdoutSpoolPath)));
                    }
                }
                catch (IOException)
                {
                    // Honest: do not throw; omit spool contents if unreadable.
                }
            }
        }

        if (!string.IsNullOrEmpty(result.Stderr) || result.StderrSpoolPath is not null)
        {
            rawLinesList.AddRange(SplitLines(result.Stderr));
            if (!string.IsNullOrEmpty(result.StderrSpoolPath))
            {
                try
                {
                    if (File.Exists(result.StderrSpoolPath))
                    {
                        rawLinesList.AddRange(SplitLines(File.ReadAllText(result.StderrSpoolPath)));
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        if (rawLinesList.Count == 0)
        {
            rawLinesList.Add("(empty)");
        }

        var notices = new List<string>();
        if (result.StdoutDroppedChars > 0)
        {
            notices.Add($"stdout dropped {result.StdoutDroppedChars} characters");
        }

        if (result.StderrDroppedChars > 0)
        {
            notices.Add($"stderr dropped {result.StderrDroppedChars} characters");
        }

        if (result.StdoutSpoolFailed)
        {
            notices.Add("stdout spool write failed; output may be partial");
        }

        if (result.StderrSpoolFailed)
        {
            notices.Add("stderr spool write failed; output may be partial");
        }

        var headerLines = new List<string>
        {
            $"# command: {effectiveCommand}",
            $"# cwd: {workingDir}"
        };

        return new SlashConsoleWorkspace(
            title,
            effectiveCommand,
            workingDir,
            sections,
            rawLinesList,
            notices,
            null,
            result.Stdout,
            result.StdoutSpoolPath,
            result.Stderr,
            result.StderrSpoolPath,
            headerLines,
            displayLine,
            workingDir);
    }

    public static SlashConsoleWorkspace FromMessage(string title, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);
        var sectionLines = new List<string>(lines.Count);
        sectionLines.AddRange(lines);
        if (sectionLines.Count == 0)
        {
            sectionLines.Add("(empty)");
        }

        var sections = new List<SlashConsoleResultSection>
        {
            new("output", sectionLines)
        };

        return new SlashConsoleWorkspace(
            title,
            string.Empty,
            string.Empty,
            sections,
            sectionLines,
            Array.Empty<string>(),
            null,
            string.Empty,
            null,
            string.Empty,
            null,
            Array.Empty<string>(),
            string.Empty,
            string.Empty);
    }

    public void ScrollBy(int delta)
    {
        _scroll = Math.Max(0, _scroll + delta);
    }

    public void ScrollToTop()
    {
        _scroll = 0;
    }

    public void ScrollToEnd()
    {
        _scroll = int.MaxValue / 4;
    }

    public void ToggleRaw()
    {
        _showingRaw = !_showingRaw;
    }

    public void BackToSummary()
    {
        _showingRaw = false;
    }

    public SlashConsoleResultView BuildView() => new(
        _title,
        _commandLine,
        _workingDirectory,
        _sections,
        _rawLines,
        _limitNotices,
        _summary,
        _scroll,
        _showingRaw);

    public ResultArtifactOutcome SaveRaw(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _cwdForSave;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ResultArtifactOutcome(false, null, "working directory not set");
        }

        var fileName = BuildFileName(_commandDisplay);
        return ResultArtifactWriter.SaveRaw(
            directory,
            fileName,
            _stdoutHead,
            _stdoutSpoolPath,
            _stderrHead,
            _stderrSpoolPath,
            _headerLines);
    }

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = new List<string>(normalized.Split('\n'));
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string BuildTitle(CapturedRunResult result)
    {
        var elapsed = CommandLifecycle.Elapsed(result.Elapsed);
        if (result.Cancelled)
        {
            return $"cancelled {elapsed}";
        }

        if (result.ExitCode == 0)
        {
            return $"exit 0 ok {elapsed}";
        }

        return $"exit {result.ExitCode} failed {elapsed}";
    }

    private static string BuildFileName(string commandDisplay)
    {
        var cmd = commandDisplay.Trim();
        if (string.IsNullOrWhiteSpace(cmd))
        {
            cmd = "run";
        }

        var slug = cmd
            .TrimStart('/')
            .Replace(' ', '-')
            .Replace('\t', '-')
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(':', '-')
            .Replace(';', '-');
        if (slug.Length > 60)
        {
            slug = slug[..60];
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "run";
        }

        return $"lattice-console-{slug}.log";
    }
}
