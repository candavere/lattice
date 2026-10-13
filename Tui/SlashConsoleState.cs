namespace Lattice.Tui;

/// <summary>
/// The slash console's input and menu state as one pure value: no terminal, no clock,
/// no thread, no filesystem. The host owns the screen and the processes; this owns
/// what the keys mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only single-line editing.</b> Printable characters append at the end and
/// Backspace removes the last one; there is no mid-line caret. Typing <c>/</c> opens
/// the menu and filtering narrows it; leaving the leading slash closes it.
/// </para>
/// <para>
/// <b><c>q</c> never quits.</b> Unlike the Launchpad's navigation mode, a bare
/// <c>q</c> is text: <c>/quit</c> could not be typed otherwise. Leaving is
/// <c>/exit</c>, <c>/quit</c>, Esc on an empty idle line, or Ctrl-C at idle.
/// </para>
/// <para>
/// <b>Menu keys.</b> Up/down move the selection (clamped), Tab completes the first
/// token to the selected entry, Enter commits (completing to the selection first
/// when the menu is open), Esc closes the menu, clears the line, or quits, in that
/// order. Cancel while a command runs belongs to the runner, which holds the keys
/// for the run's duration — never to this value.
/// </para>
/// </remarks>
public sealed class SlashConsoleState
{
    private string _input = string.Empty;
    private IReadOnlyList<SlashMenuItem> _menu = [];
    private int _selected;
    private bool _menuOpen;
    private bool _quit;
    private bool _wantsToCommit;

    /// <summary>The input line as typed.</summary>
    public string Input => _input;

    /// <summary>The menu rows on show (set by the host after every input change).</summary>
    public IReadOnlyList<SlashMenuItem> Menu => _menu;

    /// <summary>Whether the menu is open.</summary>
    public bool MenuOpen => _menuOpen;

    /// <summary>Which menu row is selected.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The selected menu row, or null when the menu is closed or empty.</summary>
    public SlashMenuItem? Selected => _menuOpen && _menu.Count > 0 ? _menu[ClampSelected()] : null;

    /// <summary>Whether the reader has asked to leave.</summary>
    public bool HasQuit => _quit;

    /// <summary>Whether the reader has asked for the input line to run.</summary>
    public bool WantsToCommit => _wantsToCommit;

    /// <summary>Reports that a commit has been started, so the screen stops asking.</summary>
    public void AcknowledgeCommit() => _wantsToCommit = false;

    /// <summary>
    /// Replaces the menu rows after the host re-filters, clamping the selection.
    /// Closing is the state's own decision (leading slash gone); opening is too,
    /// but the rows always come from the host so the two cannot disagree.
    /// </summary>
    public void SetMenu(IReadOnlyList<SlashMenuItem> menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        _menu = menu;
        _selected = ClampSelected();
        if (_menu.Count == 0)
        {
            _menuOpen = false;
        }
    }

    /// <summary>Applies one key. Ctrl-C while a command runs never reaches here.</summary>
    public void Apply(TuiKey key)
    {
        if (_quit || _wantsToCommit)
        {
            return;
        }

        // Ctrl-C at idle leaves: while a command runs the runner holds the keys.
        if (key.Kind == TuiKeyKind.Interrupt)
        {
            _quit = true;
            return;
        }

        switch (key.Kind)
        {
            case TuiKeyKind.Escape:
                ApplyEscape();
                return;

            case TuiKeyKind.Enter:
                ApplyEnter();
                return;

            case TuiKeyKind.Tab:
                ApplyTab();
                return;

            case TuiKeyKind.Up:
                MoveSelection(-1);
                return;

            case TuiKeyKind.Down:
                MoveSelection(1);
                return;

            case TuiKeyKind.Backspace:
                if (_input.Length > 0)
                {
                    _input = _input[..^1];
                    AfterInputChanged();
                }

                return;

            case TuiKeyKind.Character:
                if (!char.IsControl(key.Glyph))
                {
                    _input += key.Glyph;
                    AfterInputChanged();
                }

                return;

            default:
                return;
        }
    }

    private void ApplyEscape()
    {
        if (_menuOpen)
        {
            _menuOpen = false;
            return;
        }

        if (_input.Length > 0)
        {
            _input = string.Empty;
            return;
        }

        _quit = true;
    }

    private void ApplyEnter()
    {
        if (_menuOpen && _menu.Count > 0)
        {
            CompleteToSelected();
            _menuOpen = false;
        }

        if (_input.Length == 0)
        {
            return;
        }

        _wantsToCommit = true;
    }

    private void ApplyTab()
    {
        if (!_menuOpen || _menu.Count == 0)
        {
            return;
        }

        CompleteToSelected();
    }

    private void CompleteToSelected()
    {
        var selected = _menu[ClampSelected()];
        _input = selected.Name + TrailingArgs();
        AfterInputChanged();
    }

    private string TrailingArgs()
    {
        var rest = _input.StartsWith("/", StringComparison.Ordinal) ? _input[1..] : _input;
        var space = rest.IndexOfAny([' ', '\t']);
        return space < 0 ? string.Empty : rest[space..];
    }

    private void MoveSelection(int offset)
    {
        if (!_menuOpen || _menu.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + offset, 0, _menu.Count - 1);
    }

    private void AfterInputChanged()
    {
        _selected = ClampSelected();
        _menuOpen = _input.StartsWith("/", StringComparison.Ordinal);
    }

    private int ClampSelected() =>
        _menu.Count == 0 ? 0 : Math.Clamp(_selected, 0, _menu.Count - 1);
}
