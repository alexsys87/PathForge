using System.Globalization;
using PathForge.Core.Geometry;

namespace PathForge.Core.Grbl;

/// <summary>How the job frame is traced.</summary>
/// <param name="Feed">Speed along the frame (mm/min).</param>
/// <param name="PowerPercent">Laser power while tracing (percent of the maximum, 0 = beam off).</param>
/// <param name="Laser">GRBL laser mode ($32=1): the beam may be on at low power; Z is not touched.</param>
/// <param name="SpindleMaxS">S at full power (GRBL $30); 0 = 1000.</param>
/// <param name="SafeZ">Height to go up to first when not in laser mode (work coordinates, mm).</param>
public sealed record GrblFrameOptions(double Feed, double PowerPercent, bool Laser, double SpindleMaxS, double SafeZ);

/// <summary>
/// Traces the rectangle around a program's work area before the job: the head runs along its border (a laser at
/// minimal power, so the dot shows where the cut will be) and stops back at the start corner.
/// </summary>
public static class GrblFrame
{
    /// <summary>
    /// XY bounds of the feed moves (G1, G2, G3) of a program in work coordinates (mm); empty when there are none.
    /// Rapid moves only travel and are left out.
    /// </summary>
    public static Bounds2 WorkArea(string gcode) => WorkArea(gcode, out _);

    /// <param name="highestZ">Highest Z the program goes to (work coordinates, mm; NaN when it sets none): its own safe height.</param>
    public static Bounds2 WorkArea(string gcode, out double highestZ)
    {
        highestZ = double.NaN;
        var bounds = Bounds2.Empty;
        var inches = false;
        var relative = false;
        var motion = 0;
        var position = new Vec2(double.NaN, double.NaN);
        foreach (var line in GrblProgram.Prepare(gcode).Lines)
        {
            var words = Words(line.Text);
            var nonModal = false;
            var lineMotion = (int?)null;
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
                    case 20:
                        inches = true;
                        break;
                    case 21:
                        inches = false;
                        break;
                    case 90:
                        relative = false;
                        break;
                    case 91:
                        relative = true;
                        break;
                    case 4 or 10 or 28 or 30 or 53 or 92 or 28.1 or 30.1 or 92.1 or 38.2 or 38.3 or 38.4 or 38.5:
                        // Axis words here are not a move in work coordinates; where the machine ends up is unknown.
                        nonModal = true;
                        if (value is not (4 or 10 or 92 or 92.1 or 28.1 or 30.1))
                        {
                            position = new Vec2(double.NaN, double.NaN);
                        }

                        break;
                }
            }

            if (lineMotion is { } m)
            {
                motion = m;
            }

            if (nonModal)
            {
                continue;
            }

            var scale = inches ? 25.4 : 1;
            if (!relative && TryWord(words, 'Z', out var z))
            {
                highestZ = double.IsNaN(highestZ) ? z * scale : Math.Max(highestZ, z * scale);
            }

            var hasX = TryWord(words, 'X', out var x);
            var hasY = TryWord(words, 'Y', out var y);
            if (!hasX && !hasY)
            {
                continue;
            }

            double Axis(bool has, double value, double current) =>
                !has ? current : relative ? current + value * scale : value * scale;

            var start = position;
            var end = new Vec2(Axis(hasX, x, start.X), Axis(hasY, y, start.Y));
            position = end;
            if (motion == 0 || double.IsNaN(end.X) || double.IsNaN(end.Y))
            {
                continue;
            }

            bounds = bounds.Include(end);
            if (double.IsNaN(start.X) || double.IsNaN(start.Y))
            {
                continue;
            }

            bounds = bounds.Include(start);
            if (motion is 2 or 3)
            {
                bounds = bounds.Union(ArcBounds(start, end, words, scale, clockwise: motion == 2));
            }
        }

        return bounds;
    }

    /// <summary>Program lines that trace <paramref name="area"/> from its lower-left corner.</summary>
    public static List<string> Build(Bounds2 area, GrblFrameOptions options)
    {
        var lines = new List<string> { "G21G90G94" };
        if (!options.Laser)
        {
            lines.Add("G0Z" + F(options.SafeZ));
        }

        lines.Add($"G0X{F(area.MinX)}Y{F(area.MinY)}");
        var beam = options.Laser && options.PowerPercent > 0;
        if (beam)
        {
            // Dynamic laser power (M4): the beam is on only while moving, never at the corners' stops.
            var maxS = options.SpindleMaxS > 0 ? options.SpindleMaxS : 1000;
            var s = Math.Max(1, Math.Round(Math.Clamp(options.PowerPercent, 0, 100) / 100 * maxS));
            lines.Add("M4S" + F(s));
        }

        var feed = Math.Max(1, options.Feed);
        lines.Add($"G1X{F(area.MaxX)}Y{F(area.MinY)}F{F(feed)}");
        lines.Add($"G1X{F(area.MaxX)}Y{F(area.MaxY)}");
        lines.Add($"G1X{F(area.MinX)}Y{F(area.MaxY)}");
        lines.Add($"G1X{F(area.MinX)}Y{F(area.MinY)}");
        if (beam)
        {
            lines.Add("M5S0");
        }

        return lines;
    }

    /// <summary>Box of an arc in the XY plane (centre from I/J or from R).</summary>
    private static Bounds2 ArcBounds(Vec2 start, Vec2 end, List<(char Letter, double Value)> words, double scale, bool clockwise)
    {
        Vec2 center;
        if (TryWord(words, 'R', out var r))
        {
            r *= scale;
            var chord = end - start;
            var half = chord.Length / 2;
            if (half < 1e-9 || Math.Abs(r) < half)
            {
                return Bounds2.Of(new[] { start, end });
            }

            // Positive R takes the short arc, negative the long one; the centre is on the chord's perpendicular.
            var offset = Math.Sqrt(r * r - half * half);
            var normal = new Vec2(-chord.Y, chord.X) * (1 / chord.Length);
            var side = clockwise == r > 0 ? -1 : 1;
            center = start + chord * 0.5 + normal * (side * offset);
        }
        else
        {
            TryWord(words, 'I', out var i);
            TryWord(words, 'J', out var j);
            center = start + new Vec2(i * scale, j * scale);
        }

        var radius = start.DistanceTo(center);
        var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
        // Counter-clockwise sweep from a0 to a1 (a full circle when the ends meet).
        var sweep = clockwise ? a0 - a1 : a1 - a0;
        while (sweep <= 1e-9)
        {
            sweep += 2 * Math.PI;
        }

        var bounds = Bounds2.Of(new[] { start, end });
        for (var k = 0; k < 4; k++)
        {
            var angle = k * Math.PI / 2;
            var from = clockwise ? angle - a1 : angle - a0;
            from = ((from % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
            // For a clockwise arc, run the sweep backwards from the end.
            if (from < sweep)
            {
                bounds = bounds.Include(center + new Vec2(Math.Cos(angle), Math.Sin(angle)) * radius);
            }
        }

        return bounds;
    }

    private static bool TryWord(List<(char Letter, double Value)> words, char letter, out double value)
    {
        value = 0;
        var found = false;
        foreach (var (l, v) in words)
        {
            if (l == letter)
            {
                value = v;
                found = true;
            }
        }

        return found;
    }

    private static List<(char Letter, double Value)> Words(string text)
    {
        var words = new List<(char, double)>();
        if (text.StartsWith('$'))
        {
            return words;
        }

        var i = 0;
        while (i < text.Length)
        {
            var letter = char.ToUpperInvariant(text[i]);
            var j = i + 1;
            while (j < text.Length && (char.IsDigit(text[j]) || text[j] is '.' or '-' or '+'))
            {
                j++;
            }

            if (char.IsLetter(letter) &&
                double.TryParse(text.AsSpan(i + 1, j - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                words.Add((letter, value));
            }

            i = j;
        }

        return words;
    }

    private static string F(double value)
    {
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
