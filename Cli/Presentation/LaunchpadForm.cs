using System.Text;
using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>What a key means, which depends entirely on whether a field has focus.</summary>
public enum LaunchpadMode
{
    /// <summary>Letters steer: they move the selection, and one of them quits.</summary>
    Navigation,

    /// <summary>Letters type: every printable character goes into the focused field.</summary>
    Editing,
}

/// <summary>
/// The setup screen's state and its key handling, as one pure value: no terminal, no
/// clock, no thread, no filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two modes, and the mode is the whole design.</b> Navigation mode is where
/// <c>j</c> moves down and <c>q</c> quits. Editing mode is where every printable
/// character — including <c>q</c>, <c>j</c> and <c>k</c> — is a character in a
/// field. There is no third interpretation and no key that means one thing in one
/// mode and another in the other, so a field can hold any text at all.
/// </para>
/// <para>
/// <b>Validation is the parser, asked earlier.</b> <see cref="ValidationError"/>
/// comes from the same <see cref="CliApp"/> helpers each command uses, on the same
/// arguments, and is pure: no file is read or written, no episode is started, no
/// thread is spawned, no console property is read and nothing is printed. A rule
/// that needs the filesystem is not this type's business — it belongs to the
/// command, and it surfaces after the screen is restored.
/// </para>
/// <para>
/// <b>The command line is derived, never stored.</b> <see cref="CommandLine"/>
/// is composed from the selected command and its fields every time it is asked for,
/// so the line under the form cannot drift from the form above it.
/// </para>
/// </remarks>
public sealed class LaunchpadForm
{
    /// <summary>The name the command line under the form starts with.</summary>
    private const string Executable = "lattice";

    /// <summary>How far a page key moves the selection.</summary>
    private const int PageSize = 4;

    private readonly IReadOnlyList<LaunchpadCommand> _commands;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _carets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typed = new(StringComparer.Ordinal);

    private int _selected;
    private int _field = -1;
    private bool _quit;
    private bool _wantsToRun;
    private string? _validationError;

    private LaunchpadForm(IReadOnlyList<LaunchpadCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new ArgumentException("A form needs at least one command.", nameof(commands));
        }

        _commands = commands;
        LoadDefaults();
    }

    /// <summary>A form over the given commands, with the first selected and nothing focused.</summary>
    public static LaunchpadForm For(IReadOnlyList<LaunchpadCommand> commands) => new(commands);

    /// <summary>Which mode the screen is in.</summary>
    public LaunchpadMode Mode => _field >= 0 ? LaunchpadMode.Editing : LaunchpadMode.Navigation;

    /// <summary>The command on show, by name.</summary>
    public string Selected => Current.Name;

    /// <summary>The command on show, whole.</summary>
    public LaunchpadCommand Current => _commands[_selected];

    /// <summary>Every command the screen offers, in the order the list shows them.</summary>
    public IReadOnlyList<LaunchpadCommand> Commands => _commands;

    /// <summary>Which command in the list is on show.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The label of the field being edited, or <c>null</c> in navigation mode.</summary>
    public string? FocusedField => _field >= 0 ? Fields[_field].Label : null;

    /// <summary>Which field of the form is being edited, or -1 in navigation mode.</summary>
    public int FocusedIndex => _field;

    /// <summary>Every field of the selected command, in the order the form shows them.</summary>
    public IReadOnlyList<LaunchpadField> Fields => Current.Fields;

    /// <summary>
    /// What each field currently holds, defaults included. A reader of this value —
    /// the layout, a test — wants to know what is on the form, not what the reader has
    /// typed; whether a value was chosen is a separate question, and the command line
    /// is what answers it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values =>
        Fields.ToDictionary(entry => entry.Label, Value, StringComparer.Ordinal);

    /// <summary>The caret's column in the focused field.</summary>
    public int Caret => _field >= 0 ? _carets[Fields[_field].Label] : 0;

    /// <summary>Why the form cannot be run, or <c>null</c> when it can.</summary>
    public string? ValidationError => _validationError;

    /// <summary>Whether the form can be handed to the command it names.</summary>
    public bool CanRun => _validationError is null;

    /// <summary>Whether the reader has asked for the form to be run.</summary>
    public bool WantsToRun => _wantsToRun;

    /// <summary>The ways the selected command can be started, in the order they are offered.</summary>
    public IReadOnlyList<string> RunModes => Current.Ways;

    /// <summary>
    /// How the reader asked for the selected command to run: watched as a screen, or
    /// run as typed. Read from the run-mode field, so it is the reader's choice and
    /// not the host's default.
    /// </summary>
    public RunMode ChosenMode
    {
        get
        {
            var mode = Field(LaunchpadCatalog.RunModeLabel);
            return Current.ModeNamedBy(mode is null ? Current.Ways[0] : Value(mode));
        }
    }

    /// <summary>
    /// The run-mode field, or <c>null</c> for a command that has only one way to be
    /// run and so is not asked.
    /// </summary>
    private LaunchpadField? Field(string label) =>
        Fields.FirstOrDefault(entry => entry.Label == label);

    /// <summary>
    /// Reports that a run has been started, so the screen stops asking. The values
    /// stay: a reader whose command failed comes back to the form they filled in, not
    /// to an empty one, and re-typing a trajectory is the worst thing this screen
    /// could ask of them.
    /// </summary>
    public void Acknowledge() => _wantsToRun = false;

    /// <summary>Whether the reader has asked to leave.</summary>
    public bool HasQuit => _quit;

    /// <summary>The status the screen reports when the reader leaves: always success.</summary>
    public int ExitCode => 0;

    /// <summary>
    /// The command line the form adds up to, exactly as it would be typed. A field
    /// left at its own default contributes nothing, because the command applies that
    /// default by itself.
    /// </summary>
    public string CommandLine
    {
        get
        {
            var parts = new List<string> { Executable };

            // The command name, then its arguments, then the raw tail verbatim. The
            // text and the vector are the same walk, so they cannot disagree about
            // what the form adds up to.
            parts.Add(Current.Name);
            AppendArguments(parts);
            return string.Join(' ', parts);
        }
    }

    /// <summary>The arguments after the command name, for handing to <c>CliApp.Run</c>.</summary>
    public string[] Arguments
    {
        get
        {
            var arguments = new List<string> { Current.Name };
            AppendArguments(arguments);
            return [.. arguments];
        }
    }

    /// <summary>
    /// Appends the form's own arguments to a list, in field order: the flag's name and
    /// then its value for a flag, the value alone for a path.
    /// <para>
    /// Neither the run mode nor the raw tail is an argument. The mode decides which
    /// path the command takes rather than naming an argument, and the tail is the
    /// reader's own words, appended verbatim below so they cannot be reflowed into
    /// tokens they did not type.
    /// </para>
    /// </summary>
    private void AppendArguments(List<string> into)
    {
        foreach (var entry in Fields)
        {
            if (entry.Kind is LaunchpadFieldKind.ExtraArguments or LaunchpadFieldKind.RunMode)
            {
                continue;
            }

            var text = Chosen(entry);
            if (text.Length == 0)
            {
                continue;
            }

            if (entry.Kind == LaunchpadFieldKind.Flag)
            {
                into.Add(entry.Label);
            }

            into.Add(text);
        }

        // Any run of whitespace in the tail is collapsed, so a reader who typed a
        // stray space has not produced a command line with a doubled space in it.
        if (_values.GetValueOrDefault(LaunchpadCatalog.ExtraArgumentsLabel) is { Length: > 0 } tail)
        {
            into.AddRange(Collapse(tail).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>The text a field shows, default included.</summary>
    public string Value(LaunchpadField field) =>
        _values.TryGetValue(field.Label, out var text) ? text : field.Default ?? string.Empty;

    /// <summary>
    /// The text a field contributes to the command line: what the reader chose, or
    /// nothing. A default is a placeholder, not an answer — the command applies it
    /// by itself, so repeating it would claim the reader picked it.
    /// </summary>
    private string Chosen(LaunchpadField field) =>
        _values.TryGetValue(field.Label, out var text) ? text : string.Empty;

    /// <summary>
    /// Applies one key. Every key the screen binds arrives here, so the whole of its
    /// behaviour is reachable from a list of keys and a value.
    /// </summary>
    public void Apply(TuiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_quit || _wantsToRun)
        {
            return;
        }

        // Ctrl-C first, and from either mode: it is the one key that always means
        // "leave", because it is the one the reader pressed to interrupt a process.
        if (key.Kind == TuiKeyKind.Interrupt)
        {
            _quit = true;
            return;
        }

        if (Mode == LaunchpadMode.Editing)
        {
            ApplyEditing(key);
            return;
        }

        ApplyNavigation(key);
    }

    private void ApplyEditing(TuiKey key)
    {
        var field = Fields[_field];
        var text = Value(field);

        switch (key.Kind)
        {
            case TuiKeyKind.Escape:
                LeaveField();
                return;

            case TuiKeyKind.Enter:
                LeaveField();
                return;

            case TuiKeyKind.Tab:
                MoveField(1);
                Validate();
                return;

            case TuiKeyKind.BackTab:
                MoveField(-1);
                Validate();
                return;

            case TuiKeyKind.Backspace:
                Erase(field, text, _carets[field.Label] - 1);
                return;

            case TuiKeyKind.Delete:
                Erase(field, text, _carets[field.Label]);
                return;

            case TuiKeyKind.Left:
                SetCaret(field, _carets[field.Label] - 1);
                return;

            case TuiKeyKind.Right:
                SetCaret(field, _carets[field.Label] + 1);
                return;

            case TuiKeyKind.Home:
                SetCaret(field, 0);
                return;

            case TuiKeyKind.End:
                SetCaret(field, text.Length);
                return;

            case TuiKeyKind.Character:
                Insert(field, text, key.Glyph);
                return;

            default:
                // The navigation keys and the page keys are not text. Up and down
                // are deliberately unbound inside a field: a caret is one line long,
                // so "the row above" has no meaning, and binding them would move the
                // reader out of the field they are typing in.
                return;
        }
    }

    private void ApplyNavigation(TuiKey key)
    {
        switch (key.Kind)
        {
            case TuiKeyKind.Down:
            case TuiKeyKind.Character when key.Glyph == 'j':
                MoveSelection(1);
                return;

            case TuiKeyKind.Up:
            case TuiKeyKind.Character when key.Glyph == 'k':
                MoveSelection(-1);
                return;

            case TuiKeyKind.PageDown:
                MoveSelection(PageSize);
                return;

            case TuiKeyKind.PageUp:
                MoveSelection(-PageSize);
                return;

            case TuiKeyKind.Tab:
                Focus(0);
                return;

            case TuiKeyKind.BackTab:
                Focus(Fields.Count - 1);
                return;

            case TuiKeyKind.Enter:
                RunOrFocus();
                return;

            case TuiKeyKind.Escape:
                _quit = true;
                return;

            case TuiKeyKind.Character when key.Glyph is 'q' or 'Q':
                _quit = true;
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Enter in navigation mode runs the form when it can run, and otherwise puts the
    /// reader in the field that is wrong. The alternative — refusing and saying so
    /// again — leaves a reader who has just corrected the field still having to find
    /// their way back to it.
    /// </summary>
    private void RunOrFocus()
    {
        Validate();

        if (CanRun)
        {
            _wantsToRun = true;
            return;
        }

        var offending = Fields.ToList().FindIndex(field =>
        {
            var text = Chosen(field);
            return text.Length == 0 ? field.Required : Refusal(field, text) is not null;
        });

        Focus(offending < 0 ? 0 : offending);
    }

    /// <summary>
    /// Types one character. The first character into a field that is still showing
    /// its default <em>replaces</em> the default rather than appending to it: a
    /// default is a placeholder that says what the command would do on its own, and
    /// a reader who starts typing has decided on something else. Appending would
    /// make <c>100</c> become <c>1005</c>, which is not a number the reader chose.
    /// </summary>
    private void Insert(LaunchpadField field, string text, char glyph)
    {
        // A control character is not a character a text field can hold: it is either
        // an escape sequence the terminal has not finished sending or a byte no
        // label can carry.
        if (char.IsControl(glyph))
        {
            return;
        }

        // The first character into a field the reader has not typed in replaces the
        // default rather than appending to it. A default is a placeholder that says
        // what the command would do on its own, and appending would make <c>100</c>
        // become <c>1005</c> — a value the reader did not choose.
        if (!_typed.Contains(field.Label))
        {
            _typed.Add(field.Label);
            text = string.Empty;
            _carets[field.Label] = 0;
        }

        var caret = _carets[field.Label];
        _values[field.Label] = string.Concat(text.AsSpan(0, caret), glyph.ToString(), text.AsSpan(caret));
        _carets[field.Label] = caret + 1;
        Validate();
    }

    /// <summary>
    /// Removes the character at <paramref name="at"/>. Erasing in a field the reader
    /// has not typed in makes the field their own first — otherwise a Backspace over
    /// a default would silently keep the default, and a second one would appear to do
    /// nothing at all.
    /// </summary>
    private void Erase(LaunchpadField field, string text, int at)
    {
        if (at < 0 || at >= text.Length)
        {
            return;
        }

        _typed.Add(field.Label);
        _values[field.Label] = string.Concat(text.AsSpan(0, at), text.AsSpan(at + 1));
        _carets[field.Label] = at;
        Validate();
    }

    private void SetCaret(LaunchpadField field, int caret) =>
        _carets[field.Label] = Math.Clamp(caret, 0, Value(field).Length);

    /// <summary>
    /// Whether the reader has typed in this field. A field they have not touched is
    /// still showing the command's own default, which the command line therefore
    /// leaves out — and which the first character typed replaces.
    /// </summary>
    public bool WasTyped(LaunchpadField field) => _typed.Contains(field.Label);

    private void Focus(int index)
    {
        _field = Math.Clamp(index, 0, Fields.Count - 1);
        var label = Fields[_field].Label;
        _carets[label] = Value(Fields[_field]).Length;
        _validationError = null;
        Validate();
    }

    private void LeaveField()
    {
        _field = -1;
        Validate();
    }

    private void MoveField(int offset)
    {
        var next = _field + offset;
        _field = next < 0 ? Fields.Count - 1 : next >= Fields.Count ? 0 : next;
        var label = Fields[_field].Label;
        _carets[label] = Value(Fields[_field]).Length;
    }

    private void MoveSelection(int offset)
    {
        var next = _selected + offset;
        _selected = next < 0 ? 0 : next >= _commands.Count ? _commands.Count - 1 : next;
        _field = -1;
        LoadDefaults();
        _validationError = null;
    }

    /// <summary>
    /// Loads the selected command's defaults and discards everything else. The form
    /// is a view of one command's flags, so a value from another command is not
    /// carried across — carrying it would hand the parser a flag that means something
    /// different there.
    /// </summary>
    private void LoadDefaults()
    {
        // A default lives in the catalog, not in the values, so "the reader chose
        // this" and "the command would default to this" stay two different questions.
        // Everything the reader typed belongs to the command they typed it on.
        _values.Clear();
        _carets.Clear();
        _typed.Clear();

        foreach (var field in Current.Fields)
        {
            _carets[field.Label] = field.Default?.Length ?? 0;
        }
    }

    /// <summary>
    /// The one inline check, asked of the same helpers the commands use. A rule that
    /// needs the filesystem is not asked here: it belongs to the command, and it
    /// surfaces after the screen is restored.
    /// </summary>
    private void Validate()
    {
        var command = Current;

        foreach (var name in command.RequiredFlags)
        {
            var field = Fields.FirstOrDefault(candidate => candidate.Label == name);
            if (field is { Required: true } && Chosen(field).Length == 0)
            {
                _validationError = RefusalFor(() => CliApp.Require(new Dictionary<string, string>(StringComparer.Ordinal), name));
                return;
            }
        }

        foreach (var field in Fields.Where(field => field.Kind == LaunchpadFieldKind.Flag))
        {
            var text = Chosen(field);
            if (text.Length == 0)
            {
                continue;
            }

            var refusal = Refusal(field, text);
            if (refusal is not null)
            {
                _validationError = refusal;
                return;
            }
        }

        _validationError = null;
    }

    /// <summary>
    /// Whether the command would accept this field's value, as the message it would
    /// print. Every branch calls the parser the command calls, so the two answers
    /// cannot differ.
    /// </summary>
    private static string? Refusal(LaunchpadField field, string text)
    {
        try
        {
            Throw(field, text);
            return null;
        }
        catch (UsageError error)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// Runs exactly the parse the command runs for this field, and lets the
    /// <see cref="UsageError"/> escape. Pure: it reads no file, writes nothing,
    /// starts nothing and touches no console.
    /// </summary>
    private static void Throw(LaunchpadField field, string text)
    {
        switch (field.ValueFormat)
        {
            case LaunchpadValueFormat.UnsignedInteger:
                CliApp.ParseULong(text, field.Label);
                return;

            case LaunchpadValueFormat.PositiveInteger:
                CliApp.ParsePositiveInt(text, field.Label);
                return;

            case LaunchpadValueFormat.UnitInterval:
                CliApp.ParseFairnessThreshold(text);
                return;

            case LaunchpadValueFormat.Choice:
                if (field.Choices is { Length: > 0 } choices
                    && !choices.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    // The run-mode field names a choice about how to run, not a flag,
                    // and the wording says so: a reader who mistypes it is not being
                    // told about a flag this command does not have.
                    throw new UsageError(
                        field.Kind == LaunchpadFieldKind.RunMode
                            ? $"invalid {field.Label} '{text}' (expected {Choices(choices)})."
                            : $"invalid {field.Label} '{text}' (expected {Choices(choices)}).");
                }

                return;

            default:
                return;
        }
    }

    /// <summary>The accepted values as the command's own messages name them.</summary>
    private static string Choices(string[] choices) =>
        string.Join(
            " or ",
            choices.Select(choice => $"'{choice}'"));

    private static string? RefusalFor(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (UsageError error)
        {
            return error.Message;
        }
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var glyph in text)
        {
            var isSpace = glyph == ' ' || glyph == '\t';
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(glyph);
            }

            lastWasSpace = isSpace;
        }

        return builder.ToString();
    }
}
