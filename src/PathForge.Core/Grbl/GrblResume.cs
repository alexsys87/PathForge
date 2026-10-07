using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>Settings of a resumed start.</summary>
/// <param name="SafeZ">Height for the moves over the work (work coordinates, mm); the highest Z of the program is used if higher.</param>
/// <param name="SpindleDelaySeconds">Pause after switching the spindle on.</param>
/// <param name="ApproachClearance">The tool goes down quickly to this height above the resume point, then with the plunge feed.</param>
public sealed record GrblResumeOptions(double SafeZ, double SpindleDelaySeconds, double ApproachClearance = 1);

/// <summary>A program ready to continue from a line, with what the operator should know.</summary>
/// <param name="Lines">Approach moves followed by the rest of the program.</param>
/// <param name="StartLine">Source line the program continues from.</param>
/// <param name="Position">Where the tool goes down (work coordinates, mm).</param>
/// <param name="Tool">Comment of the last tool change before the line (empty when there is none).</param>
public sealed record GrblResumeResult(List<GrblLine> Lines, int StartLine, Vec3 Position, string Tool, List<string> Warnings);

/// <summary>
/// Continues a program from a line after a broken tool, a power cut or a stop: replays the modal state of all
/// lines before it (units, distance mode, plane, work offset, feed, spindle, coolant, position) and adds a safe
/// approach — up to the safe height, over the point where the line starts, spindle on, down with the plunge feed.
/// </summary>
public static class GrblResume
{
    /// <summary>
    /// GRBL acknowledges a line when it enters its planner (about 15 moves ahead of the machine). A stopped job
    /// resumes this many program lines before the last acknowledged one: cutting a few moves again in the air
    /// is safe, skipping some is not.
    /// </summary>
    public const int PlannerLines = 20;

    /// <summary>Source line to suggest after a job stopped with <paramref name="lastAcknowledged"/> as the last acknowledged source line.</summary>
    public static int SuggestLine(IReadOnlyList<GrblLine> program, int lastAcknowledged)
    {
        var index = -1;
        for (var i = 0; i < program.Count; i++)
        {
            if (program[i].SourceLine <= lastAcknowledged)
            {
                index = i;
            }
        }

        return index < 0 ? (program.Count > 0 ? program[0].SourceLine : 1) : program[Math.Max(0, index - PlannerLines)].SourceLine;
    }

    public static GrblResumeResult Build(IReadOnlyList<GrblLine> program, int fromLine, GrblResumeOptions options)
    {
        var warnings = new List<string>();
        var start = -1;
        for (var i = 0; i < program.Count; i++)
        {
            if (program[i].SourceLine >= fromLine && program[i].Text.Length > 0)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            throw new InvalidOperationException(Loc.T($"После строки {fromLine} в программе нет команд.", $"There are no commands after line {fromLine}."));
        }

        // The highest Z the program goes to is its own safe height; the state before the line is what to restore.
        var all = new ModalState();
        var maxZ = double.NegativeInfinity;
        var state = new ModalState();
        for (var i = 0; i < program.Count; i++)
        {
            all.Apply(program[i], new List<string>());
            if (!double.IsNaN(all.Position.Z))
            {
                maxZ = Math.Max(maxZ, all.Position.Z);
            }

            if (i < start)
            {
                state.Apply(program[i], warnings);
            }
        }

        var lines = new List<GrblLine>();
        var source = program[start].SourceLine;
        // Approach lines carry the resume line's number: an error in them holds the job like any program line.
        void Add(string text) => lines.Add(new GrblLine(text, "", false, source));

        var safeZ = Math.Max(options.SafeZ, double.IsNegativeInfinity(maxZ) ? options.SafeZ : maxZ);
        var target = state.Position;
        Add($"G21G90G94{state.Plane}{state.WorkOffset}");
        if (state.HasZ)
        {
            Add("G0Z" + F(safeZ));
        }

        var x = double.IsNaN(target.X) ? "" : "X" + F(target.X);
        var y = double.IsNaN(target.Y) ? "" : "Y" + F(target.Y);
        if (x.Length + y.Length > 0)
        {
            Add("G0" + x + y);
        }
        else
        {
            warnings.Add(Loc.T("До этой строки программа не задавала X и Y: станок поедет прямо от текущего положения.", "The program set no X and Y before this line: the machine moves on from where it is."));
        }

        if (state.Spindle.Length > 0)
        {
            Add(state.Spindle + "S" + F(state.SpindleSpeed));
            if (state.Spindle == "M3" && options.SpindleDelaySeconds > 0)
            {
                Add("G4P" + F(options.SpindleDelaySeconds));
            }
        }

        if (state.Coolant.Length > 0)
        {
            Add(state.Coolant);
        }

        if (state.HasZ && !double.IsNaN(target.Z))
        {
            if (target.Z < safeZ - options.ApproachClearance)
            {
                // The tool was down there before (its body cleared the way), so a rapid stop just above is safe.
                Add("G0Z" + F(target.Z + options.ApproachClearance));
            }

            var plunge = double.IsNaN(state.LowestFeed) ? 100 : state.LowestFeed;
            Add("G1Z" + F(target.Z) + "F" + F(plunge));
        }

        if (!double.IsNaN(state.Feed))
        {
            // Still in G21 here: the feed is given in mm/min and GRBL keeps it when G20 comes back.
            Add("F" + F(state.Feed));
        }

        // Back to the program's own modes for the rest of it.
        var restore = (state.Inches ? "G20" : "") + (state.Relative ? "G91" : "");
        if (restore.Length > 0)
        {
            Add(restore);
        }

        var first = program[start];
        var text = first.Text;
        if (!HasMotionWord(text) && state.Motion.Length > 0 && (text.Contains('X') || text.Contains('Y') || text.Contains('Z')))
        {
            // The line relies on the modal motion of the lines before it; the approach above changed it.
            text = state.Motion + text;
        }

        lines.Add(first with { Text = text });
        lines.AddRange(program.Skip(start + 1));
        return new GrblResumeResult(lines, source, new Vec3(target.X, target.Y, target.Z), state.ToolComment, warnings);
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool HasMotionWord(string text)
    {
        foreach (var (letter, value) in Pairs(text))
        {
            if (letter == 'G' && value is 0 or 1 or 2 or 3 or 38.2 or 38.3 or 38.4 or 38.5 or 80)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Last value of every letter on the line (G and M words are handled separately).</summary>
    private static Dictionary<char, double> Words(string text)
    {
        var words = new Dictionary<char, double>();
        foreach (var (letter, value) in Pairs(text))
        {
            words[letter] = value;
        }

        return words;
    }

    private static IEnumerable<(char Letter, double Value)> Pairs(string text)
    {
        if (text.StartsWith('$'))
        {
            yield break;
        }

        var i = 0;
        while (i < text.Length)
        {
            var letter = text[i];
            var j = i + 1;
            while (j < text.Length && (char.IsDigit(text[j]) || text[j] is '.' or '-' or '+'))
            {
                j++;
            }

            if (char.IsLetter(letter) &&
                double.TryParse(text.AsSpan(i + 1, j - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (letter, value);
            }

            i = j;
        }
    }

    /// <summary>Modal state of GRBL after a number of lines.</summary>
    private sealed class ModalState
    {
        public bool Inches;
        public bool Relative;
        public string Plane = "G17";
        public string WorkOffset = "G54";
        public string Motion = "";
        public string Spindle = "";
        public double SpindleSpeed;
        public string Coolant = "";
        public double Feed = double.NaN;
        public double LowestFeed = double.NaN;
        public bool HasZ;
        public string ToolComment = "";

        /// <summary>Work position in millimetres (NaN until the program sets the axis).</summary>
        public Vec3 Position = new(double.NaN, double.NaN, double.NaN);

        public void Apply(GrblLine line, List<string> warnings)
        {
            if (line.StopAfter)
            {
                ToolComment = line.Comment;
                // M0 stops: the program writes M5 before a tool change; nothing else changes.
            }

            var axisWordsMoveMachine = true;
            var nonModal = "";
            foreach (var (letter, value) in Pairs(line.Text))
            {
                switch (letter)
                {
                    case 'G':
                        switch (value)
                        {
                            case 0 or 1 or 2 or 3:
                                Motion = "G" + value.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 17 or 18 or 19:
                                Plane = "G" + value.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 20:
                                Inches = true;
                                break;
                            case 21:
                                Inches = false;
                                break;
                            case 90:
                                Relative = false;
                                break;
                            case 91:
                                Relative = true;
                                break;
                            case >= 54 and <= 59:
                                WorkOffset = "G" + value.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 4 or 10 or 28 or 30 or 53 or 92 or 28.1 or 30.1 or 92.1:
                                nonModal = value.ToString(CultureInfo.InvariantCulture);
                                axisWordsMoveMachine = value is 28 or 30 or 53;
                                break;
                            case 38.2 or 38.3 or 38.4 or 38.5:
                                nonModal = "38";
                                break;
                        }

                        break;
                    case 'M':
                        switch (value)
                        {
                            case 3 or 4:
                                Spindle = "M" + value.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 5:
                                Spindle = "";
                                break;
                            case 7 or 8:
                                Coolant = "M" + value.ToString(CultureInfo.InvariantCulture);
                                break;
                            case 9:
                                Coolant = "";
                                break;
                        }

                        break;
                    case 'S':
                        SpindleSpeed = value;
                        break;
                    case 'F':
                        Feed = value * (Inches ? 25.4 : 1);
                        LowestFeed = double.IsNaN(LowestFeed) ? Feed : Math.Min(LowestFeed, Feed);
                        break;
                    case 'T':
                        ToolComment = "T" + value.ToString(CultureInfo.InvariantCulture);
                        break;
                }
            }

            switch (nonModal)
            {
                case "92" or "92.1" or "10":
                    warnings.Add(Loc.T(
                        $"Строка {line.SourceLine}: программа меняет систему координат (G{nonModal}) — проверьте ноль перед продолжением.",
                        $"Line {line.SourceLine}: the program changes the coordinate system (G{nonModal}) — check the zero before resuming."));
                    return;
                case "28" or "30" or "53" or "38":
                    // The machine ended up somewhere the work coordinates do not tell; the next moves set it again.
                    Position = new Vec3(double.NaN, double.NaN, double.NaN);
                    return;
                case "4" or "28.1" or "30.1":
                    return;
            }

            if (!axisWordsMoveMachine)
            {
                return;
            }

            var scale = Inches ? 25.4 : 1;
            var words = Words(line.Text);
            double Axis(char axis, double current)
            {
                if (!words.TryGetValue(axis, out var v))
                {
                    return current;
                }

                if (axis == 'Z')
                {
                    HasZ = true;
                }

                return Relative ? (double.IsNaN(current) ? double.NaN : current + v * scale) : v * scale;
            }

            Position = new Vec3(Axis('X', Position.X), Axis('Y', Position.Y), Axis('Z', Position.Z));
        }
    }
}
