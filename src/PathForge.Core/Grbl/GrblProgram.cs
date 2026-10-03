using System.Globalization;
using System.Text;

namespace PathForge.Core.Grbl;

/// <summary>One line ready to be streamed: comments and spaces removed.</summary>
/// <param name="Text">Command sent to GRBL (may be empty for a pure tool-change stop).</param>
/// <param name="Comment">Comment found on the source line (shown at a tool change).</param>
/// <param name="StopAfter">The source line contained M0: the stream stops here so the operator can work on the machine.</param>
/// <param name="SourceLine">1-based line number in the original program.</param>
public sealed record GrblLine(string Text, string Comment, bool StopAfter, int SourceLine);

/// <summary>Prepares G-code for streaming to GRBL.</summary>
public static class GrblProgram
{
    /// <summary>GRBL's line buffer holds 80 characters including the line break.</summary>
    public const int MaxLineLength = 79;

    public sealed record Prepared(List<GrblLine> Lines, List<string> Problems)
    {
        public int CommandCount => Lines.Count(l => l.Text.Length > 0);
    }

    /// <summary>
    /// Removes comments, spaces and block deletes, upper-cases the words and finds program stops (M0).
    /// M0 is not sent: GRBL could not be jogged or zeroed while paused by it, so the sender itself stops
    /// at that point and waits for the operator.
    /// </summary>
    public static Prepared Prepare(string gcode)
    {
        var lines = new List<GrblLine>();
        var problems = new List<string>();
        var source = gcode.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', '\r');
        for (var i = 0; i < source.Length; i++)
        {
            var (text, comment) = Clean(source[i]);
            if (text.Length == 0 || text == "%")
            {
                continue;
            }

            var stop = false;
            var words = SplitWords(text);
            if (words.Any(IsProgramStop))
            {
                stop = true;
                text = string.Concat(words.Where(w => !IsProgramStop(w)));
            }

            if (text.Length > MaxLineLength)
            {
                problems.Add($"Строка {i + 1}: длиннее {MaxLineLength + 1} символов — GRBL её не примет.");
            }

            if (text.StartsWith('$'))
            {
                problems.Add($"Строка {i + 1}: команда «{text}» в программе — GRBL выполняет её только в состоянии Idle.");
            }

            lines.Add(new GrblLine(text, comment, stop, i + 1));
        }

        return new Prepared(lines, problems);
    }

    private static (string Text, string Comment) Clean(string line)
    {
        var text = new StringBuilder(line.Length);
        var comment = new StringBuilder();
        var depth = 0;
        foreach (var c in line)
        {
            if (depth == 0 && c == ';')
            {
                break;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth > 0)
            {
                comment.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                text.Append(char.ToUpperInvariant(c));
            }
        }

        var semicolon = line.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && comment.Length == 0)
        {
            comment.Append(line[(semicolon + 1)..]);
        }

        var cleaned = text.ToString();
        if (cleaned.StartsWith('/'))
        {
            // Block delete: GRBL ignores the slash itself, so remove it.
            cleaned = cleaned[1..];
        }

        return (cleaned, comment.ToString().Trim());
    }

    /// <summary>Splits "G1X10F300" into "G1", "X10", "F300" (system commands stay whole).</summary>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        if (text.StartsWith('$'))
        {
            words.Add(text);
            return words;
        }

        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || char.IsLetter(text[i]))
            {
                words.Add(text[start..i]);
                start = i;
            }
        }

        return words;
    }

    private static bool IsProgramStop(string word) =>
        word.Length >= 2 && word[0] == 'M' &&
        double.TryParse(word.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == 0;
}
