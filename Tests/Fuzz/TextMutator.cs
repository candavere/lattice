using System.Text;
using System.Text.RegularExpressions;
using Lattice.Environment;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Deterministic mutation engine over the seed corpus text. Every operator is
/// seeded through the engine's <see cref="Rng"/>, so the same seed always
/// produces the same mutated payload — which is what makes a failing case
/// reproducible from its reported seed alone. Byte-level operators corrupt
/// UTF-8 the way a hand-edited or truncated file would; text-level operators
/// target the JSONL structure, critical token values, and adversarial payload
/// shapes.
/// </summary>
internal static class TextMutator
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private static readonly Regex NumberPattern = new("-?\\d+", RegexOptions.Compiled);

    /// <summary>
    /// Applies a seeded chain of 1..4 mutation operators to
    /// <paramref name="input"/>, yielding one fuzz case.
    /// </summary>
    public static string Mutate(string input, Rng rng, string[] criticalTokens)
    {
        var pattern = TokenValuePattern(criticalTokens);
        var text = input;
        var operations = 1 + rng.Next(0, 4);
        for (var i = 0; i < operations; i++)
        {
            text = rng.Next(0, 10) switch
            {
                0 => BitFlips(text, rng),
                1 => DeleteBytes(text, rng),
                2 => InsertBytes(text, rng),
                3 => TruncateLines(text, rng),
                4 => ResplitNewlines(text, rng),
                5 => SwapTokenValues(text, rng, pattern),
                6 => InjectPayloads(text, rng),
                7 => DuplicateOrReorderLines(text, rng),
                8 => PerturbNumbers(text, rng),
                _ => text,
            };
        }

        return text;
    }

    /// <summary>Builds the compiled value-mutation pattern for a token list.</summary>
    public static Regex TokenValuePattern(string[] criticalTokens) =>
        new(
            "\"(?<token>" + string.Join("|", criticalTokens.Select(Regex.Escape)) + ")\"\\s*:\\s*(?<value>[^,\\]}]+)",
            RegexOptions.Compiled);

    /// <summary>Flips 1..6 random bits in the UTF-8 encoding.</summary>
    public static string BitFlips(string input, Rng rng)
    {
        var bytes = Utf8.GetBytes(input);
        if (bytes.Length == 0)
        {
            return input;
        }

        var flips = 1 + rng.Next(0, 6);
        for (var i = 0; i < flips; i++)
        {
            var index = rng.Next(0, bytes.Length);
            bytes[index] ^= (byte)(1 << rng.Next(0, 8));
        }

        return Utf8.GetString(bytes);
    }

    /// <summary>Deletes 1..12 random bytes from the UTF-8 encoding.</summary>
    public static string DeleteBytes(string input, Rng rng)
    {
        var bytes = Utf8.GetBytes(input).ToList();
        if (bytes.Count == 0)
        {
            return input;
        }

        var deletions = 1 + rng.Next(0, Math.Min(12, bytes.Count));
        for (var i = 0; i < deletions && bytes.Count > 0; i++)
        {
            bytes.RemoveAt(rng.Next(0, bytes.Count));
        }

        return Utf8.GetString(bytes.ToArray());
    }

    /// <summary>Inserts 1..8 random bytes (nulls included) at random positions.</summary>
    public static string InsertBytes(string input, Rng rng)
    {
        var bytes = Utf8.GetBytes(input).ToList();
        var insertions = 1 + rng.Next(0, 8);
        for (var i = 0; i < insertions; i++)
        {
            var position = bytes.Count == 0 ? 0 : rng.Next(0, bytes.Count + 1);
            var value = rng.Next(0, 4) switch
            {
                0 => (byte)0x00,
                1 => (byte)0xFF,
                2 => (byte)'A',
                _ => (byte)rng.Next(0, 256),
            };
            bytes.Insert(position, value);
        }

        return Utf8.GetString(bytes.ToArray());
    }

    /// <summary>Drops trailing lines, a random line, or keeps only a prefix.</summary>
    public static string TruncateLines(string input, Rng rng)
    {
        var lines = input.Split('\n').ToList();
        if (lines.Count < 2)
        {
            return input;
        }

        var kept = rng.Next(0, 3) switch
        {
            0 => lines.Take(Math.Max(1, lines.Count - rng.Next(1, Math.Min(4, lines.Count)))).ToList(),
            1 => lines.Where((_, index) => index != rng.Next(0, lines.Count)).ToList(),
            _ => lines.Take(1 + rng.Next(0, lines.Count)).ToList(),
        };

        return string.Join('\n', kept);
    }

    /// <summary>Splits lines at random positions, merges adjacent lines, and injects CR/LF variety.</summary>
    public static string ResplitNewlines(string input, Rng rng)
    {
        var builder = new StringBuilder(input);
        var edits = 1 + rng.Next(0, 3);
        for (var i = 0; i < edits; i++)
        {
            var op = rng.Next(0, 3);
            if (builder.Length == 0)
            {
                return builder.ToString();
            }

            switch (op)
            {
                case 0:
                    builder.Insert(rng.Next(0, builder.Length), '\n');
                    break;
                case 1 when builder.Length >= 2:
                    builder.Insert(rng.Next(0, builder.Length), "\r\n");
                    break;
                default:
                    var at = builder.ToString().IndexOf('\n');
                    if (at >= 0)
                    {
                        builder.Remove(at, 1);
                    }
                    else
                    {
                        builder.Append('\n');
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces the values of 1..3 random critical-token occurrences with
    /// adversarial substitutes: huge/negative/float/hex numbers, empty and
    /// null values, nested arrays, progressions (e.g. nine-digit and
    /// u-long-exceeding literals), and unicode/control strings.
    /// </summary>
    public static string SwapTokenValues(string input, Rng rng, Regex pattern)
    {
        var builder = new StringBuilder(input);
        var mutations = 1 + rng.Next(0, 3);
        for (var i = 0; i < mutations; i++)
        {
            var matches = pattern.Matches(builder.ToString());
            if (matches.Count == 0)
            {
                return builder.ToString();
            }

            var hit = matches[rng.Next(0, matches.Count)];
            var value = hit.Groups["value"];
            builder.Remove(value.Index, value.Length);
            builder.Insert(value.Index, AdversarialValue(rng));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Injects adversarial payloads anywhere in the text: very long strings,
    /// embedded nulls, replacement-char sequences, bracket explosions, huge
    /// numeric literals, and trailing garbage after the final line.
    /// </summary>
    public static string InjectPayloads(string input, Rng rng)
    {
        var builder = new StringBuilder(input);
        var injections = rng.Next(0, 3);
        for (var i = 0; i < injections; i++)
        {
            if (builder.Length == 0)
            {
                return builder.ToString();
            }

            builder.Insert(rng.Next(0, builder.Length), Payload(rng));
        }

        if (rng.Next(0, 3) == 0)
        {
            builder.Append('\n').Append(Payload(rng));
        }

        return builder.ToString();
    }

    /// <summary>Duplicates a random line or swaps two random lines.</summary>
    public static string DuplicateOrReorderLines(string input, Rng rng)
    {
        var lines = input.Split('\n').ToList();
        if (lines.Count < 2)
        {
            return input;
        }

        if (rng.Next(0, 2) == 0)
        {
            var duplicated = lines[rng.Next(0, lines.Count)];
            lines.Insert(rng.Next(0, lines.Count), duplicated);
        }
        else
        {
            var first = rng.Next(0, lines.Count);
            var second = rng.Next(0, lines.Count);
            (lines[first], lines[second]) = (lines[second], lines[first]);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Applies small arithmetic corruption to 1..3 number literals.</summary>
    public static string PerturbNumbers(string input, Rng rng)
    {
        var matches = NumberPattern.Matches(input);
        if (matches.Count == 0)
        {
            return input;
        }

        var operations = 1 + rng.Next(0, 3);
        var builder = new StringBuilder(input);
        for (var i = 0; i < operations; i++)
        {
            var hit = matches[rng.Next(0, matches.Count)];
            if (!long.TryParse(hit.Value, out var parsed))
            {
                continue;
            }

            var mutated = rng.Next(0, 4) switch
            {
                0 => parsed * (1 + rng.Next(1, 5)),
                1 => parsed + (long)rng.Next(-3, 4),
                2 => checked(-parsed),
                _ => parsed,
            };

            var replacement = mutated.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Remove(hit.Index, hit.Length);
            builder.Insert(hit.Index, replacement);
        }

        return builder.ToString();
    }

    private static string AdversarialValue(Rng rng) => rng.Next(0, 14) switch
    {
        0 => "-1",
        1 => "0",
        2 => "2147483647",
        3 => "-2147483648",
        4 => "999999999999",
        5 => "3.14",
        6 => "18446744073709551615",
        7 => "\"\"",
        8 => "\" \"",
        9 => "null",
        10 => "[]",
        11 => NestedArray(rng),
        12 => RandomAdversarialString(rng),
        _ => "1e309",
    };

    private static string NestedArray(Rng rng)
    {
        var depth = rng.Next(1, 32);
        return new string('[', depth) + "0" + new string(']', depth);
    }

    private static string RandomAdversarialString(Rng rng)
    {
        const string alphabet = "\"\\{}\"[],:null true}\u0000\u0001\u00E9\u20AC\u4E2D{";
        var length = rng.Next(0, 64);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[rng.Next(0, alphabet.Length)];
        }

        return new string(chars);
    }

    private static string Payload(Rng rng) => rng.Next(0, 8) switch
    {
        0 => new string('A', 4096),
        1 => new string('\u0000', 8),
        2 => "\uFFFD\uFFFD\uFFFD",
        3 => "]]]]}]]]",
        4 => new string('9', 64),
        5 => "1e999999999",
        6 => "{\"rulesKind\":",
        _ => "\n" + new string('x', 512) + "\n",
    };
}