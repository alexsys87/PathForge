using System.Globalization;
using System.Text;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;

namespace PathForge.Core.GCode;

/// <summary>
/// G-code text with, for every move of every toolpath, the 1-based number of the line that brings the machine to
/// its end (an arc line covers several moves; a move that writes nothing maps to the last line before it).
/// </summary>
public sealed record GcodeOutput(string Text, IReadOnlyList<int[]> MoveLines)
{
    /// <summary>Line of the move at <paramref name="index"/> counted through all toolpaths in order (0 when out of range).</summary>
    public int LineOfMove(int index)
    {
        if (index < 0)
        {
            return 0;
        }

        foreach (var lines in MoveLines)
        {
            if (index < lines.Length)
            {
                return lines[index];
            }

            index -= lines.Length;
        }

        return 0;
    }
}

/// <summary>
/// Writes G-code: G0/G1 moves in absolute millimetres, M3/M5 spindle, M8 (or M7) / M9 air assist per operation.
/// <see cref="GcodeDialect.Generic"/> uses Tn M6 tool changes; <see cref="GcodeDialect.Grbl"/> pauses with M0,
/// keeps every line within GRBL's 80 character buffer and writes comments in ASCII.
/// </summary>
public sealed class GcodeWriter
{
    /// <summary>GRBL's line buffer is 80 characters; stay clearly below it.</summary>
    public const int GrblMaxLineLength = 70;

    private readonly MachineSettings _machine;
    private readonly double _safeZ;
    private readonly string _format;
    private readonly StringBuilder _output = new();
    private readonly List<int[]> _moveLines = new();
    private int _lineCount;
    private double? _x;
    private double? _y;
    private double? _z;
    private double? _feed;
    private Vec3 _current;
    private double? _s;
    private bool _laser;
    private string _coolant = "";

    private GcodeWriter(MachineSettings machine, double safeZ)
    {
        _machine = machine;
        _safeZ = safeZ;
        var decimals = Math.Clamp(machine.DecimalPlaces, 0, 6);
        _format = decimals == 0 ? "0" : "0." + new string('#', decimals);
    }

    private bool IsGrbl => _machine.Dialect == GcodeDialect.Grbl;

    private bool UseArcs => _machine.UseArcs && _machine.DecimalPlaces >= 3;

    /// <param name="safeZ">Safe height in program coordinates; defaults to <see cref="MachineSettings.SafeZ"/>.</param>
    public static string Write(string programName, IReadOnlyList<Toolpath> toolpaths, MachineSettings machine, double? safeZ = null)
    {
        var writer = new GcodeWriter(machine, safeZ ?? machine.SafeZ);
        writer.WriteProgram(programName, toolpaths);
        return writer._output.ToString();
    }

    public static string Write(string programName, GenerationResult result, MachineSettings machine) =>
        Write(programName, result.Toolpaths, machine, result.SafeZ);

    /// <summary>Like <see cref="Write(string, GenerationResult, MachineSettings)"/>, with the line of every move.</summary>
    public static GcodeOutput WriteWithLines(string programName, GenerationResult result, MachineSettings machine)
    {
        var writer = new GcodeWriter(machine, result.SafeZ);
        writer.WriteProgram(programName, result.Toolpaths);
        return new GcodeOutput(writer._output.ToString(), writer._moveLines);
    }

    private void WriteProgram(string programName, IReadOnlyList<Toolpath> toolpaths)
    {
        Comment(programName);
        foreach (var tool in toolpaths.Select(t => t.Tool).DistinctBy(t => t.Id))
        {
            Comment($"T{tool.Number}: {tool.Name}, D={Number(tool.Diameter)}");
        }

        Line("G90 G94");
        Line("G17");
        Line("G21");
        WriteCustomLines(_machine.Header);
        if (_machine.LaserMode)
        {
            // The laser is focused at Z0 before the job: Z words only appear when a pass lowers the focus.
            _z = 0;
            _current = new Vec3(0, 0, 0);
        }
        else
        {
            Rapid(null, null, _safeZ);
        }

        Tool? currentTool = null;
        foreach (var toolpath in toolpaths)
        {
            Comment(toolpath.Operation.Name);
            if (currentTool is null || currentTool.Id != toolpath.Tool.Id || currentTool.SpindleRpm != toolpath.Tool.SpindleRpm)
            {
                StartTool(toolpath.Tool, currentTool);
                currentTool = toolpath.Tool;
            }

            Coolant(toolpath.Operation.AirAssist);
            WriteMoves(toolpath);
        }

        if (!_machine.LaserMode)
        {
            Rapid(null, null, _safeZ);
        }

        Coolant(false);
        Line("M5");
        if (_machine.ReturnToOrigin)
        {
            Rapid(0, 0, null);
        }

        WriteCustomLines(_machine.Footer);
        Line("M30");
    }

    private void WriteMoves(Toolpath toolpath)
    {
        _laser = toolpath.Tool.Kind == ToolKind.Laser;
        // Raster lines are straight: arc fitting would only cost time.
        var arcs = UseArcs && toolpath.Operation is not LaserRasterOperation;
        var moves = toolpath.Moves;
        var lines = new int[moves.Count];
        _moveLines.Add(lines);
        var k = 0;
        while (k < moves.Count)
        {
            if (arcs && TryWriteArcRun(moves, ref k, toolpath.Tool.FeedRate, lines))
            {
                continue;
            }

            var t = moves[k].Target;
            switch (moves[k].Kind)
            {
                case MoveKind.Rapid:
                    Rapid(t.X, t.Y, t.Z);
                    break;
                case MoveKind.Plunge:
                    Feed(t.X, t.Y, t.Z, moves[k].FeedOr(toolpath.Tool.PlungeRate), moves[k].Power);
                    break;
                default:
                    Feed(t.X, t.Y, t.Z, moves[k].FeedOr(toolpath.Tool.FeedRate), moves[k].Power);
                    break;
            }

            lines[k] = _lineCount;
            k++;
        }
    }

    /// <summary>S word for a laser move (power 0…1) when it differs from the last one.</summary>
    private string PowerWord(double power)
    {
        if (!_laser)
        {
            return "";
        }

        var maxS = _machine.SpindleMaxS > 0 ? _machine.SpindleMaxS : 1000;
        var s = Math.Round((double.IsNaN(power) ? 0 : Math.Clamp(power, 0, 1)) * maxS);
        if (_s is double last && Math.Abs(last - s) < 1e-9)
        {
            return "";
        }

        _s = s;
        return " S" + Number(s);
    }

    /// <summary>
    /// Writes a run of cutting moves at constant Z starting at <paramref name="k"/>, replacing points that
    /// lie on circles with G2/G3. Returns false when there is no such run.
    /// </summary>
    private bool TryWriteArcRun(List<ToolMove> moves, ref int k, double feed, int[] lines)
    {
        var z = _current.Z;
        var power = moves[k].Power;
        var ownFeed = moves[k].Feed;
        feed = moves[k].FeedOr(feed);
        var end = k;
        while (end < moves.Count && moves[end].Kind == MoveKind.Cut && Math.Abs(moves[end].Target.Z - z) < 1e-9 &&
               moves[end].Power.Equals(power) && moves[end].Feed.Equals(ownFeed))
        {
            end++;
        }

        if (end - k < 3)
        {
            return false;
        }

        var points = new List<Vec2>(end - k + 1) { _current.XY };
        for (var i = k; i < end; i++)
        {
            points.Add(moves[i].Target.XY);
        }

        var done = 0;
        foreach (var piece in ArcFitter.Fit(points))
        {
            var target = points[piece.End];
            if (piece.Center is { } center)
            {
                Arc(target, center, piece.Clockwise, feed, power);
            }
            else
            {
                Feed(target.X, target.Y, z, feed, power);
            }

            // Point i of the run is the end of move k + i - 1.
            for (; done < piece.End; done++)
            {
                lines[k + done] = _lineCount;
            }
        }

        for (; done < end - k; done++)
        {
            lines[k + done] = _lineCount;
        }

        k = end;
        return true;
    }

    private void Arc(Vec2 end, Vec2 center, bool clockwise, double feed, double power)
    {
        var start = _current.XY;
        var axes = Axes(end.X, end.Y, null);
        if (axes.Length == 0)
        {
            return;
        }

        var f = "";
        if (_feed is null || Math.Abs(_feed.Value - feed) > 1e-9)
        {
            f = " F" + Number(feed);
            _feed = feed;
        }

        Line($"{(clockwise ? "G2" : "G3")}{axes} I{Number(center.X - start.X)} J{Number(center.Y - start.Y)}{PowerWord(power)}{f}");
        _current = new Vec3(end, _current.Z);
    }

    private void StartTool(Tool tool, Tool? previous)
    {
        var toolChanged = previous is not null && previous.Id != tool.Id;
        if (previous is not null)
        {
            if (!_machine.LaserMode)
            {
                Rapid(null, null, _safeZ);
            }

            // No air blowing while the program waits for a tool change.
            Coolant(false);
            Line("M5");
        }

        if (tool.Kind == ToolKind.Laser)
        {
            if (toolChanged)
            {
                Line("M0", $"Attach laser {tool.Name}");
            }

            // Laser mode: M4 scales the power with the actual speed (no burn marks in corners); S0 = beam off.
            _s = 0;
            Line(_machine.LaserMode ? "M4 S0" : "M3 S0");
            return;
        }

        if (toolChanged && _machine.UseToolChange && !IsGrbl)
        {
            Line($"T{tool.Number} M6");
            // The machine may have moved during the tool change.
            _x = _y = _z = null;
            Rapid(null, null, _safeZ);
        }
        else if (toolChanged)
        {
            // Manual tool change: the program pauses, the operator changes the cutter, re-zeroes Z and resumes.
            Line("M0", $"Insert T{tool.Number} {tool.Name}");
            // The operator has moved the machine: go up to the (new) safe height before any XY move.
            _x = _y = _z = null;
            Rapid(null, null, _safeZ);
        }

        Line($"M3 S{Number(_machine.SpindleWord(tool.SpindleRpm))}");
        if (_machine.SpindleDelaySeconds > 0)
        {
            Line($"G4 P{Number(_machine.SpindleDelaySeconds)}");
        }
    }

    /// <summary>Switches the air assist (coolant) on or off when its state changes.</summary>
    private void Coolant(bool on)
    {
        var word = !on ? "" : _machine.AirAssistCommand == CoolantCommand.Mist ? "M7" : "M8";
        if (word == _coolant)
        {
            return;
        }

        Line(word.Length > 0 ? word : "M9");
        _coolant = word;
    }

    private void Rapid(double? x, double? y, double? z)
    {
        _current = new Vec3(x ?? _current.X, y ?? _current.Y, z ?? _current.Z);
        var axes = Axes(x, y, z);
        if (axes.Length > 0)
        {
            Line("G0" + axes);
        }
    }

    private void Feed(double x, double y, double z, double feed, double power = double.NaN)
    {
        _current = new Vec3(x, y, z);
        var axes = Axes(x, y, z);
        if (axes.Length == 0)
        {
            return;
        }

        axes += PowerWord(power);

        var f = "";
        if (_feed is null || Math.Abs(_feed.Value - feed) > 1e-9)
        {
            f = " F" + Number(feed);
            _feed = feed;
        }

        Line("G1" + axes + f);
    }

    /// <summary>Only the axes whose (rounded) value changes are written.</summary>
    private string Axes(double? x, double? y, double? z)
    {
        var sb = new StringBuilder();
        Axis(sb, 'X', x, ref _x);
        Axis(sb, 'Y', y, ref _y);
        Axis(sb, 'Z', z, ref _z);
        return sb.ToString();
    }

    private void Axis(StringBuilder sb, char name, double? value, ref double? last)
    {
        if (value is null)
        {
            return;
        }

        var text = Number(value.Value);
        if (last is not null && Number(last.Value) == text)
        {
            return;
        }

        sb.Append(' ').Append(name).Append(text);
        last = value;
    }

    private string Number(double value)
    {
        var text = value.ToString(_format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private void WriteCustomLines(string text)
    {
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            Line(line);
        }
    }

    private void Comment(string text) => Line("", text);

    /// <summary>Writes a code line with an optional trailing comment.</summary>
    private void Line(string code, string? comment = null)
    {
        var line = code;
        if (!string.IsNullOrWhiteSpace(comment))
        {
            var prefix = code.Length > 0 ? code + " (" : "(";
            var body = CommentText(comment);
            if (IsGrbl && prefix.Length + body.Length + 1 > GrblMaxLineLength)
            {
                body = body[..Math.Max(0, GrblMaxLineLength - prefix.Length - 1)].TrimEnd();
            }

            line = prefix + body + ")";
        }

        _output.Append(line).Append('\n');
        _lineCount++;
    }

    private string CommentText(string text)
    {
        // Parentheses would terminate the comment early.
        var clean = text.Replace('(', '[').Replace(')', ']').Replace('\n', ' ').Replace('\r', ' ').Trim();
        return IsGrbl ? ToAscii(clean) : clean;
    }

    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya", ['Ø'] = "D", ['ø'] = "D", ['°'] = "deg", ['×'] = "x", ['—'] = "-", ['–'] = "-", ['«'] = "\"", ['»'] = "\"",
    };

    /// <summary>Transliterates Russian and drops other characters GRBL senders may not handle.</summary>
    public static string ToAscii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~')
            {
                sb.Append(c);
            }
            else if (Cyrillic.TryGetValue(char.ToLowerInvariant(c), out var latin))
            {
                sb.Append(char.IsUpper(c) && latin.Length > 0 ? char.ToUpperInvariant(latin[0]) + latin[1..] : latin);
            }
            else if (c == '\t')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
