using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Import.Pcb;

/// <summary>Shape drawn by a Gerber aperture, as polygons around the aperture origin (mm).</summary>
internal sealed class ApertureShape
{
    public ApertureShape(List<(List<Vec2> Ring, bool Dark)> parts, double strokeWidth, bool isCircle)
    {
        Parts = parts;
        StrokeWidth = strokeWidth;
        IsCircle = isCircle;
    }

    /// <summary>Polygons in drawing order; dark parts add, clear parts remove (macro exposure off).</summary>
    public List<(List<Vec2> Ring, bool Dark)> Parts { get; }

    /// <summary>Width used when the aperture strokes a line (circle diameter, or the larger rectangle side).</summary>
    public double StrokeWidth { get; }

    public bool IsCircle { get; }
}

/// <summary>Builds aperture shapes for standard templates (C, R, O, P) and aperture macros.</summary>
internal static class GerberApertures
{
    /// <summary>Chord tolerance for round shapes (mm).</summary>
    public const double Tolerance = 0.005;

    public static ApertureShape Standard(string template, double[] p, double scale)
    {
        double P(int i) => i < p.Length ? p[i] * scale : 0;
        switch (template)
        {
            case "C":
                return new ApertureShape(new() { (Circle(Vec2.Zero, P(0) / 2), true) }, P(0), isCircle: true);
            case "R":
                return new ApertureShape(new() { (Rectangle(Vec2.Zero, P(0), P(1), 0), true) }, Math.Max(P(0), P(1)), isCircle: false);
            case "O":
                return new ApertureShape(new() { (Obround(P(0), P(1)), true) }, Math.Max(P(0), P(1)), isCircle: false);
            case "P":
                var vertices = Math.Max(3, (int)Math.Round(p.Length > 1 ? p[1] : 3));
                var rotation = p.Length > 2 ? p[2] : 0;
                return new ApertureShape(new() { (RegularPolygon(Vec2.Zero, P(0) / 2, vertices, rotation), true) }, P(0), isCircle: false);
            default:
                throw new FormatException($"Unknown aperture template '{template}'.");
        }
    }

    public static List<Vec2> Circle(Vec2 center, double radius)
    {
        var count = Math.Max(8, ArcSegment.SegmentCount(radius, 2 * Math.PI, Tolerance));
        var ring = new List<Vec2>(count);
        for (var i = 0; i < count; i++)
        {
            ring.Add(center + Vec2.FromPolar(radius, 2 * Math.PI * i / count));
        }

        return ring;
    }

    /// <summary>Rectangle of size w×h centred at <paramref name="center"/>, rotated by degrees around the origin.</summary>
    public static List<Vec2> Rectangle(Vec2 center, double w, double h, double rotationDeg)
    {
        var ring = new List<Vec2>
        {
            center + new Vec2(-w / 2, -h / 2),
            center + new Vec2(w / 2, -h / 2),
            center + new Vec2(w / 2, h / 2),
            center + new Vec2(-w / 2, h / 2),
        };
        return Rotate(ring, rotationDeg);
    }

    public static List<Vec2> Obround(double w, double h)
    {
        if (Math.Abs(w - h) < 1e-9)
        {
            return Circle(Vec2.Zero, w / 2);
        }

        // Straight part between two half circles along the longer side.
        var horizontal = w > h;
        var r = Math.Min(w, h) / 2;
        var half = (Math.Max(w, h) - 2 * r) / 2;
        var c1 = horizontal ? new Vec2(half, 0) : new Vec2(0, half);
        var c2 = -c1;
        var start = horizontal ? -Math.PI / 2 : 0;
        var steps = Math.Max(4, ArcSegment.SegmentCount(r, Math.PI, Tolerance));
        var ring = new List<Vec2>();
        for (var i = 0; i <= steps; i++)
        {
            ring.Add(c1 + Vec2.FromPolar(r, start + Math.PI * i / steps));
        }

        for (var i = 0; i <= steps; i++)
        {
            ring.Add(c2 + Vec2.FromPolar(r, start + Math.PI + Math.PI * i / steps));
        }

        return ring;
    }

    public static List<Vec2> RegularPolygon(Vec2 center, double radius, int vertices, double rotationDeg)
    {
        var ring = new List<Vec2>(vertices);
        for (var i = 0; i < vertices; i++)
        {
            ring.Add(center + Vec2.FromPolar(radius, (rotationDeg * Math.PI / 180) + 2 * Math.PI * i / vertices));
        }

        return ring;
    }

    public static List<Vec2> Rotate(List<Vec2> ring, double rotationDeg)
    {
        if (Math.Abs(rotationDeg) < 1e-12)
        {
            return ring;
        }

        var t = Affine2.Rotation(rotationDeg * Math.PI / 180);
        return ring.Select(t.Apply).ToList();
    }
}

/// <summary>Aperture macro (%AM): primitives with arithmetic expressions over $n parameters.</summary>
internal sealed class ApertureMacro
{
    private readonly List<string> _statements;

    public ApertureMacro(string name, IEnumerable<string> statements)
    {
        Name = name;
        _statements = statements.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public string Name { get; }

    public ApertureShape Instantiate(double[] parameters, double scale, List<string> warnings)
    {
        var variables = new Dictionary<int, double>();
        for (var i = 0; i < parameters.Length; i++)
        {
            variables[i + 1] = parameters[i];
        }

        var parts = new List<(List<Vec2>, bool)>();
        double width = 0;
        foreach (var statement in _statements)
        {
            if (statement.StartsWith('0'))
            {
                continue; // comment primitive
            }

            if (statement.StartsWith('$'))
            {
                var eq = statement.IndexOf('=');
                var index = int.Parse(statement[1..eq], CultureInfo.InvariantCulture);
                variables[index] = Expression.Evaluate(statement[(eq + 1)..], variables);
                continue;
            }

            var fields = statement.Split(',');
            var code = int.Parse(fields[0], CultureInfo.InvariantCulture);
            var v = fields.Skip(1).Select(f => Expression.Evaluate(f, variables)).ToArray();
            double V(int i) => i < v.Length ? v[i] : 0;
            double L(int i) => V(i) * scale;
            var dark = code == 7 || V(0) >= 0.5; // thermal has no exposure field

            switch (code)
            {
                case 1: // circle: exposure, diameter, x, y [, rotation]
                    parts.Add((GerberApertures.Rotate(GerberApertures.Circle(new Vec2(L(2), L(3)), L(1) / 2), V(4)), dark));
                    width = Math.Max(width, L(1));
                    break;
                case 2:
                case 20: // vector line: exposure, width, x1, y1, x2, y2, rotation
                {
                    var a = new Vec2(L(2), L(3));
                    var b = new Vec2(L(4), L(5));
                    var dir = (b - a).Normalized();
                    var n = dir.PerpendicularLeft * (L(1) / 2);
                    var ring = new List<Vec2> { a - n, b - n, b + n, a + n };
                    parts.Add((GerberApertures.Rotate(ring, V(6)), dark));
                    width = Math.Max(width, L(1));
                    break;
                }

                case 21: // center line: exposure, width, height, cx, cy, rotation
                    parts.Add((GerberApertures.Rectangle(new Vec2(L(3), L(4)), L(1), L(2), V(5)), dark));
                    width = Math.Max(width, Math.Max(L(1), L(2)));
                    break;
                case 22: // lower-left line (deprecated): exposure, width, height, x, y, rotation
                    parts.Add((GerberApertures.Rectangle(new Vec2(L(3) + L(1) / 2, L(4) + L(2) / 2), L(1), L(2), V(5)), dark));
                    width = Math.Max(width, Math.Max(L(1), L(2)));
                    break;
                case 4: // outline: exposure, n, x0, y0, ..., xn, yn, rotation
                {
                    var count = (int)Math.Round(V(1)) + 1;
                    var ring = new List<Vec2>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ring.Add(new Vec2(L(2 + 2 * i), L(3 + 2 * i)));
                    }

                    if (ring.Count > 1 && ring[^1].IsNear(ring[0], 1e-9))
                    {
                        ring.RemoveAt(ring.Count - 1);
                    }

                    parts.Add((GerberApertures.Rotate(ring, V(2 + 2 * count)), dark));
                    width = Math.Max(width, Bounds2.Of(ring).Width);
                    break;
                }

                case 5: // polygon: exposure, vertices, cx, cy, diameter, rotation
                {
                    var center = new Vec2(L(2), L(3));
                    var ring = GerberApertures.RegularPolygon(center, L(4) / 2, Math.Max(3, (int)Math.Round(V(1))), 0);
                    parts.Add((GerberApertures.Rotate(ring, V(5)), dark));
                    width = Math.Max(width, L(4));
                    break;
                }

                case 7: // thermal: cx, cy, outer d, inner d, gap, rotation
                {
                    var center = new Vec2(L(0), L(1));
                    parts.Add((GerberApertures.Rotate(GerberApertures.Circle(center, L(2) / 2), V(5)), true));
                    parts.Add((GerberApertures.Rotate(GerberApertures.Circle(center, L(3) / 2), V(5)), false));
                    parts.Add((GerberApertures.Rectangle(center, L(2) * 1.1, L(4), V(5)), false));
                    parts.Add((GerberApertures.Rectangle(center, L(4), L(2) * 1.1, V(5)), false));
                    width = Math.Max(width, L(2));
                    break;
                }

                default:
                    warnings.Add(Loc.T($"Примитив макроса {code} в апертуре {Name} не поддерживается и пропущен.", $"Macro primitive {code} in aperture {Name} is not supported and was skipped."));
                    break;
            }
        }

        return new ApertureShape(parts, width, isCircle: false);
    }
}

/// <summary>Arithmetic of aperture macros: + - x / and parentheses over numbers and $n.</summary>
internal static class Expression
{
    public static double Evaluate(string text, IReadOnlyDictionary<int, double> variables)
    {
        var parser = new Parser(text.Replace(" ", "", StringComparison.Ordinal), variables);
        var value = parser.ParseSum();
        return value;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly IReadOnlyDictionary<int, double> _variables;
        private int _pos;

        public Parser(string text, IReadOnlyDictionary<int, double> variables)
        {
            _text = text;
            _variables = variables;
        }

        public double ParseSum()
        {
            var value = ParseProduct();
            while (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
            {
                var op = _text[_pos++];
                var right = ParseProduct();
                value = op == '+' ? value + right : value - right;
            }

            return value;
        }

        private double ParseProduct()
        {
            var value = ParseUnary();
            while (_pos < _text.Length && (_text[_pos] is 'x' or 'X' or '/'))
            {
                var op = _text[_pos++];
                var right = ParseUnary();
                value = op == '/' ? (right == 0 ? 0 : value / right) : value * right;
            }

            return value;
        }

        private double ParseUnary()
        {
            if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+'))
            {
                var negative = _text[_pos++] == '-';
                var value = ParseUnary();
                return negative ? -value : value;
            }

            if (_pos < _text.Length && _text[_pos] == '(')
            {
                _pos++;
                var value = ParseSum();
                if (_pos < _text.Length && _text[_pos] == ')')
                {
                    _pos++;
                }

                return value;
            }

            if (_pos < _text.Length && _text[_pos] == '$')
            {
                _pos++;
                var start = _pos;
                while (_pos < _text.Length && char.IsDigit(_text[_pos]))
                {
                    _pos++;
                }

                var index = int.Parse(_text[start.._pos], CultureInfo.InvariantCulture);
                return _variables.TryGetValue(index, out var v) ? v : 0;
            }

            var numberStart = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
            {
                _pos++;
            }

            return _pos > numberStart
                ? double.Parse(_text[numberStart.._pos], NumberStyles.Float, CultureInfo.InvariantCulture)
                : 0;
        }
    }
}
