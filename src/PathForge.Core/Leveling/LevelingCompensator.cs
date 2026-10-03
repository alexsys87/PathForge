using System.Globalization;
using System.Text;
using PathForge.Core.Geometry;

namespace PathForge.Core.Leveling;

public sealed record LevelingResult(string Gcode, List<string> Warnings);

/// <summary>
/// Adds the measured surface height to every Z of a program (auto-levelling for PCB engraving): feed moves are
/// split into short pieces so the tool follows the warped board, arcs are replaced by such pieces.
/// Works on absolute (G90), millimetre (G21), XY-plane (G17) programs as written by PathForge and most CAM tools.
/// </summary>
public static class LevelingCompensator
{
    private const double ArcTolerance = 0.005;

    public static LevelingResult Apply(string gcode, LevelingMap map, double maxSegment = 1.0)
    {
        if (!map.IsValid)
        {
            throw new ArgumentException("Карта высот пуста.", nameof(map));
        }

        maxSegment = Math.Max(0.05, maxSegment);
        var warnings = new List<string>();
        var output = new StringBuilder(gcode.Length * 2);
        var motion = 0;
        var absolute = true;
        var inches = false;
        var planeXy = true;
        double x = 0, y = 0, z = 0;
        var maxOutside = 0.0;
        var relativeWarned = false;
        var lineNumber = 0;

        foreach (var raw in gcode.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            var (code, comment) = SplitComment(raw);
            var words = Words(code);
            if (words.Count == 0)
            {
                output.Append(raw).Append('\n');
                continue;
            }

            int? lineMotion = null;
            var machineCoordinates = false;
            var nonMotionG = false;
            foreach (var (letter, value) in words)
            {
                if (letter != 'G')
                {
                    continue;
                }

                switch (value)
                {
                    case 0 or 1 or 2 or 3:
                        lineMotion = (int)value;
                        break;
                    case 90:
                        absolute = true;
                        break;
                    case 91:
                        absolute = false;
                        break;
                    case 20:
                        inches = true;
                        break;
                    case 21:
                        inches = false;
                        break;
                    case 17:
                        planeXy = true;
                        break;
                    case 18 or 19:
                        planeXy = false;
                        break;
                    case 53:
                        machineCoordinates = true;
                        break;
                    default:
                        // Probing, dwell, offsets and the like: never moved by the map.
                        nonMotionG = true;
                        break;
                }
            }

            if (lineMotion is { } m)
            {
                motion = m;
            }

            var hasAxes = words.Any(w => w.Letter is 'X' or 'Y' or 'Z');
            if (!hasAxes || nonMotionG || machineCoordinates || !absolute || inches || (motion >= 2 && !planeXy))
            {
                if (hasAxes && (!absolute || inches) && !relativeWarned)
                {
                    warnings.Add($"Строка {lineNumber}: относительные координаты или дюймы — такие строки не корректируются.");
                    relativeWarned = true;
                }

                if (hasAxes && absolute && !machineCoordinates && !nonMotionG && !inches)
                {
                    // Keep track of the position even when the line is left as it is.
                    x = Get(words, 'X') ?? x;
                    y = Get(words, 'Y') ?? y;
                    z = Get(words, 'Z') ?? z;
                }

                output.Append(raw).Append('\n');
                continue;
            }

            var tx = Get(words, 'X') ?? x;
            var ty = Get(words, 'Y') ?? y;
            var tz = Get(words, 'Z') ?? z;
            var extra = string.Concat(words
                .Where(w => w.Letter is not ('G' or 'X' or 'Y' or 'Z' or 'I' or 'J' or 'K' or 'R' or 'N'))
                .Select(w => " " + w.Letter + Format(w.Value)));
            var trailing = comment.Length > 0 ? " " + comment : "";

            var points = new List<Vec3>();
            if (motion is 2 or 3)
            {
                points.AddRange(ArcPoints(words, motion == 2, new Vec3(x, y, z), new Vec3(tx, ty, tz), maxSegment));
            }
            else if (motion == 1)
            {
                var length = new Vec2(x, y).DistanceTo(new Vec2(tx, ty));
                var count = Math.Max(1, (int)Math.Ceiling(length / maxSegment - 1e-9));
                for (var k = 1; k <= count; k++)
                {
                    var t = (double)k / count;
                    points.Add(new Vec3(x + (tx - x) * t, y + (ty - y) * t, z + (tz - z) * t));
                }
            }
            else
            {
                points.Add(new Vec3(tx, ty, tz));
            }

            var word = motion == 0 ? "G0" : "G1";
            for (var k = 0; k < points.Count; k++)
            {
                var p = points[k];
                maxOutside = Math.Max(maxOutside, map.DistanceOutside(p.X, p.Y));
                output.Append(word)
                    .Append(" X").Append(Format(p.X))
                    .Append(" Y").Append(Format(p.Y))
                    .Append(" Z").Append(Format(p.Z + map.HeightAt(p.X, p.Y)));
                if (k == 0)
                {
                    output.Append(extra).Append(trailing);
                }

                output.Append('\n');
            }

            x = tx;
            y = ty;
            z = tz;
        }

        if (maxOutside > Math.Max(map.StepX, map.StepY) / 2)
        {
            warnings.Add(string.Create(CultureInfo.CurrentCulture,
                $"Программа выходит за карту высот на {maxOutside:0.#} мм — там берётся высота ближайшего края. Снимите карту по всей плате."));
        }

        // Keep the original ending (no extra line break).
        if (!gcode.EndsWith('\n') && output.Length > 0)
        {
            output.Length--;
        }

        return new LevelingResult(output.ToString(), warnings);
    }

    /// <summary>Points along a G2 (clockwise) or G3 arc, centre from I/J or radius R; Z changes linearly (helix).</summary>
    private static IEnumerable<Vec3> ArcPoints(List<(char Letter, double Value)> words, bool clockwise, Vec3 from, Vec3 to, double maxSegment)
    {
        Vec2 center;
        var start = from.XY;
        var end = to.XY;
        if (Get(words, 'R') is { } r)
        {
            // Radius form: centre on the perpendicular bisector; negative R takes the longer arc.
            var chord = end - start;
            var d = chord.Length;
            var radius = Math.Max(Math.Abs(r), d / 2);
            var h = Math.Sqrt(Math.Max(0, radius * radius - d * d / 4));
            var mid = (start + end) / 2;
            var normal = chord.Length < 1e-12 ? Vec2.Zero : chord.PerpendicularLeft / d;
            var leftSide = clockwise ^ (r < 0);
            center = mid + normal * (leftSide ? -h : h);
        }
        else
        {
            center = start + new Vec2(Get(words, 'I') ?? 0, Get(words, 'J') ?? 0);
        }

        var radius0 = start.DistanceTo(center);
        var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var sweep = a1 - a0;
        if (clockwise)
        {
            while (sweep >= -1e-12)
            {
                sweep -= 2 * Math.PI;
            }
        }
        else
        {
            while (sweep <= 1e-12)
            {
                sweep += 2 * Math.PI;
            }
        }

        var byTolerance = radius0 > ArcTolerance ? Math.Abs(sweep) / (2 * Math.Acos(1 - ArcTolerance / radius0)) : 1;
        var byLength = Math.Abs(sweep) * radius0 / maxSegment;
        var count = Math.Clamp((int)Math.Ceiling(Math.Max(byTolerance, byLength)), 1, 10000);
        for (var k = 1; k <= count; k++)
        {
            var t = (double)k / count;
            var p = k == count ? end : center + Vec2.FromPolar(radius0, a0 + sweep * t);
            yield return new Vec3(p.X, p.Y, from.Z + (to.Z - from.Z) * t);
        }
    }

    private static (string Code, string Comment) SplitComment(string line)
    {
        var semicolon = line.IndexOf(';', StringComparison.Ordinal);
        var paren = line.IndexOf('(', StringComparison.Ordinal);
        var cut = semicolon < 0 ? paren : paren < 0 ? semicolon : Math.Min(semicolon, paren);
        return cut < 0 ? (line, "") : (line[..cut], line[cut..].Trim());
    }

    private static List<(char Letter, double Value)> Words(string code)
    {
        var words = new List<(char, double)>();
        var i = 0;
        while (i < code.Length)
        {
            var c = char.ToUpperInvariant(code[i]);
            if (!char.IsLetter(c))
            {
                i++;
                continue;
            }

            var start = ++i;
            while (i < code.Length && (char.IsDigit(code[i]) || code[i] is '.' or '-' or '+' || char.IsWhiteSpace(code[i])))
            {
                if (char.IsWhiteSpace(code[i]) && i > start && (char.IsDigit(code[i - 1]) || code[i - 1] == '.'))
                {
                    break;
                }

                i++;
            }

            var text = code[start..i].Replace(" ", "", StringComparison.Ordinal);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                words.Add((c, value));
            }
        }

        return words;
    }

    private static double? Get(List<(char Letter, double Value)> words, char letter)
    {
        foreach (var (l, v) in words)
        {
            if (l == letter)
            {
                return v;
            }
        }

        return null;
    }

    private static string Format(double value)
    {
        var text = value.ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
