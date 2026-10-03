using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.Core.Import.Pcb;

/// <summary>How Gerber graphics are interpreted.</summary>
public enum GerberMode
{
    /// <summary>Copper layer: the filled area (tracks, pads, zones) becomes closed contours.</summary>
    Copper,

    /// <summary>Board outline (Edge.Cuts): drawn lines are taken as centre lines, aperture width is ignored.</summary>
    Outline,
}

public sealed class GerberResult
{
    /// <summary>Copper mode: outer boundaries and holes of the copper area. Outline mode: centre lines.</summary>
    public List<Contour> Contours { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reader for Gerber RS-274X (and the extended X2 format, whose attributes are ignored):
/// apertures C/R/O/P and macros, linear and circular interpolation, regions, flashes and polarity.
/// Written from the public format specification.
/// </summary>
public static partial class GerberReader
{
    public static GerberResult ReadFile(string path, GerberMode mode)
    {
        var text = File.ReadAllText(path, Encoding.ASCII);
        return Read(text, mode, Path.GetFileName(path));
    }

    public static GerberResult Read(string text, GerberMode mode, string layerName = "")
    {
        var state = new State(mode, layerName);
        foreach (var (command, extended) in Tokenize(text))
        {
            if (extended)
            {
                state.Extended(command);
            }
            else
            {
                state.Word(command);
            }

            if (state.Finished)
            {
                break;
            }
        }

        state.Finish();
        return state.Result;
    }

    /// <summary>Splits the file into word commands ("X..Y..D01") and extended commands ("%...%").</summary>
    private static IEnumerable<(string Command, bool Extended)> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '%')
            {
                var end = text.IndexOf('%', i + 1);
                if (end < 0)
                {
                    yield break;
                }

                var block = text[(i + 1)..end];
                i = end + 1;
                // Aperture macros keep their internal '*' separated statements together.
                if (block.TrimStart().StartsWith("AM", StringComparison.Ordinal))
                {
                    yield return (Clean(block), true);
                }
                else
                {
                    foreach (var part in block.Split('*'))
                    {
                        var cleaned = Clean(part);
                        if (cleaned.Length > 0)
                        {
                            yield return (cleaned, true);
                        }
                    }
                }
            }
            else
            {
                var end = text.IndexOfAny(new[] { '*', '%' }, i);
                if (end < 0)
                {
                    end = text.Length;
                }

                var word = text[i..end];
                i = end < text.Length && text[end] == '*' ? end + 1 : end;
                // G04 comments keep their spaces; everything else is whitespace-free.
                var cleaned = word.TrimStart().StartsWith("G04", StringComparison.Ordinal) ? "G04" : Clean(word);
                if (cleaned.Length > 0)
                {
                    yield return (cleaned, false);
                }
            }
        }
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^FS([LTD]?)([AI]?)N?\d*G?\d*X(\d)(\d)Y(\d)(\d)")]
    private static partial Regex FormatRegex();

    [GeneratedRegex(@"^ADD(\d+)([^,]+)(?:,(.*))?$")]
    private static partial Regex ApertureRegex();

    [GeneratedRegex(@"([GXYIJDM])([+-]?[\d.]+)")]
    private static partial Regex WordRegex();

    private sealed class State
    {
        private readonly GerberMode _mode;
        private readonly string _layer;
        private readonly Dictionary<int, ApertureShape> _apertures = new();
        private readonly Dictionary<string, ApertureMacro> _macros = new();
        private readonly List<Vec2> _region = new();
        private readonly List<List<Vec2>> _regionContours = new();
        private readonly List<Vec2> _stroke = new();
        private readonly List<Segment> _outlineSegments = new();
        private readonly List<ImportedPath> _outlinePaths = new();
        private readonly HashSet<string> _reported = new();

        private Paths64 _image = new();
        private Paths64 _batch = new();
        private bool _batchDark = true;
        private bool _dark = true;
        private double _scale = 1; // mm per file unit
        private int _integerDigits = 3;
        private int _decimalDigits = 4;
        private bool _trailingZerosOmitted;
        private bool _incremental;
        private int _interpolation = 1; // 1 linear, 2 CW, 3 CCW
        private bool _multiQuadrant = true;
        private bool _regionMode;
        private int? _aperture;
        private int _lastOperation = 2;
        private Vec2 _position;

        public State(GerberMode mode, string layer)
        {
            _mode = mode;
            _layer = layer;
        }

        public GerberResult Result { get; } = new();

        public bool Finished { get; private set; }

        public void Extended(string command)
        {
            if (command.StartsWith("FS", StringComparison.Ordinal))
            {
                var m = FormatRegex().Match(command);
                if (!m.Success)
                {
                    throw new FormatException(Loc.T($"Не удалось разобрать формат координат: {command}", $"Could not parse the coordinate format: {command}"));
                }

                _trailingZerosOmitted = m.Groups[1].Value == "T";
                _incremental = m.Groups[2].Value == "I";
                _integerDigits = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                _decimalDigits = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (_incremental)
                {
                    Warn(Loc.T("Инкрементные координаты (FSI) устарели; результат может быть неверным.", "Incremental coordinates (FSI) are deprecated; the result may be wrong."));
                }
            }
            else if (command.StartsWith("MO", StringComparison.Ordinal))
            {
                _scale = command.StartsWith("MOIN", StringComparison.Ordinal) ? 25.4 : 1;
            }
            else if (command.StartsWith("ADD", StringComparison.Ordinal))
            {
                DefineAperture(command);
            }
            else if (command.StartsWith("AM", StringComparison.Ordinal))
            {
                var parts = command[2..].Split('*', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    _macros[parts[0]] = new ApertureMacro(parts[0], parts.Skip(1));
                }
            }
            else if (command.StartsWith("LP", StringComparison.Ordinal))
            {
                _dark = command != "LPC";
            }
            else if (command.StartsWith("SR", StringComparison.Ordinal))
            {
                if (command != "SR" && command != "SRX1Y1I0J0" && command != "SRX1Y1I0.0J0.0")
                {
                    Warn(Loc.T("Повтор блока (%SR) не поддерживается: размножьте плату в программе проектирования.", "Step and repeat (%SR) is not supported: panelize the board in your PCB design program."));
                }
            }
            else if (command.StartsWith("IPNEG", StringComparison.Ordinal))
            {
                Warn(Loc.T("Негативное изображение (%IPNEG) не поддерживается.", "Negative image (%IPNEG) is not supported."));
            }
            else if (command.StartsWith("MI", StringComparison.Ordinal) && command.Contains('1'))
            {
                Warn(Loc.T("Зеркалирование в файле (%MI) не поддерживается; используйте «Зеркалить по X».", "Mirroring in the file (%MI) is not supported; use “Mirror X”."));
            }

            // TF/TA/TO/TD (X2 attributes), IN, LN, OF, SF, AS, IR: no influence on 2D geometry here.
        }

        public void Word(string command)
        {
            if (command == "G04")
            {
                return;
            }

            double? x = null, y = null, i = null, j = null;
            int? d = null;
            foreach (Match m in WordRegex().Matches(command))
            {
                var letter = m.Groups[1].Value[0];
                var value = m.Groups[2].Value;
                switch (letter)
                {
                    case 'G':
                        Gcode((int)double.Parse(value, CultureInfo.InvariantCulture));
                        break;
                    case 'M':
                        if (value is "02" or "2" or "00" or "0" or "30")
                        {
                            Finished = true;
                        }

                        break;
                    case 'D':
                        d = (int)double.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case 'X':
                        x = Coordinate(value);
                        break;
                    case 'Y':
                        y = Coordinate(value);
                        break;
                    case 'I':
                        i = Coordinate(value);
                        break;
                    case 'J':
                        j = Coordinate(value);
                        break;
                }
            }

            if (d is >= 10)
            {
                FlushStroke();
                _aperture = d;
                return;
            }

            var hasCoordinates = x.HasValue || y.HasValue || i.HasValue || j.HasValue;
            if (d is null && !hasCoordinates)
            {
                return;
            }

            var operation = d ?? _lastOperation; // coordinate data without D is the deprecated modal form
            _lastOperation = operation;
            var target = new Vec2(
                x.HasValue ? (_incremental ? _position.X + x.Value : x.Value) : _position.X,
                y.HasValue ? (_incremental ? _position.Y + y.Value : y.Value) : _position.Y);
            var offset = new Vec2(i ?? 0, j ?? 0);

            switch (operation)
            {
                case 1:
                    Interpolate(target, offset);
                    break;
                case 2:
                    FlushStroke();
                    if (_regionMode)
                    {
                        CloseRegionContour();
                        _region.Add(target);
                    }

                    break;
                case 3:
                    FlushStroke();
                    Flash(target);
                    break;
            }

            _position = target;
        }

        private void Gcode(int code)
        {
            switch (code)
            {
                case 1:
                case 2:
                case 3:
                    _interpolation = code;
                    break;
                case 36:
                    FlushStroke();
                    _regionMode = true;
                    _region.Clear();
                    _regionContours.Clear();
                    break;
                case 37:
                    CloseRegionContour();
                    _regionMode = false;
                    if (_mode == GerberMode.Copper && _regionContours.Count > 0)
                    {
                        Add(ClipperBridge.Union(ClipperBridge.ToPaths(_regionContours)));
                    }

                    _regionContours.Clear();
                    break;
                case 74:
                    _multiQuadrant = false;
                    break;
                case 75:
                    _multiQuadrant = true;
                    break;
                case 70:
                    _scale = 25.4;
                    break;
                case 71:
                    _scale = 1;
                    break;
                case 90:
                    _incremental = false;
                    break;
                case 91:
                    _incremental = true;
                    break;
            }
        }

        private void DefineAperture(string command)
        {
            var m = ApertureRegex().Match(command);
            if (!m.Success)
            {
                Warn(Loc.T($"Не удалось разобрать апертуру: {command}", $"Could not parse the aperture: {command}"));
                return;
            }

            var number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var template = m.Groups[2].Value;
            var parameters = m.Groups[3].Success && m.Groups[3].Value.Length > 0
                ? m.Groups[3].Value.Split('X').Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray()
                : Array.Empty<double>();

            if (template is "C" or "R" or "O" or "P")
            {
                _apertures[number] = GerberApertures.Standard(template, parameters, _scale);
            }
            else if (_macros.TryGetValue(template, out var macro))
            {
                _apertures[number] = macro.Instantiate(parameters, _scale, Result.Warnings);
            }
            else
            {
                Warn(Loc.T($"Апертура D{number} ссылается на неизвестный макрос {template}.", $"Aperture D{number} refers to an unknown macro {template}."));
            }
        }

        private void Interpolate(Vec2 target, Vec2 offset)
        {
            var segment = _interpolation == 1 ? (Segment)new LineSegment(_position, target) : Arc(_position, target, offset);

            if (_regionMode)
            {
                if (_region.Count == 0)
                {
                    _region.Add(_position);
                }

                segment.AppendPoints(_region, GerberApertures.Tolerance);
                return;
            }

            if (_mode == GerberMode.Outline)
            {
                if (_outlineSegments.Count > 0 && !_outlineSegments[^1].End.IsNear(segment.Start, 1e-9))
                {
                    FlushStroke();
                }

                if (segment.Length > 1e-9)
                {
                    _outlineSegments.Add(segment);
                }

                return;
            }

            if (_stroke.Count == 0)
            {
                _stroke.Add(_position);
            }

            segment.AppendPoints(_stroke, GerberApertures.Tolerance);
        }

        private Segment Arc(Vec2 start, Vec2 end, Vec2 offset)
        {
            var clockwise = _interpolation == 2;
            Vec2 center;
            if (_multiQuadrant)
            {
                center = start + offset;
            }
            else
            {
                // Single quadrant: I and J are unsigned; pick the centre that gives a valid arc of at most 90°.
                center = start + offset;
                var best = double.PositiveInfinity;
                foreach (var sx in new[] { 1, -1 })
                {
                    foreach (var sy in new[] { 1, -1 })
                    {
                        var c = start + new Vec2(Math.Abs(offset.X) * sx, Math.Abs(offset.Y) * sy);
                        var error = Math.Abs(c.DistanceTo(start) - c.DistanceTo(end));
                        var sweep = Math.Abs(Sweep(c, start, end, clockwise, fullCircle: false));
                        if (sweep <= Math.PI / 2 + 1e-6 && error < best)
                        {
                            best = error;
                            center = c;
                        }
                    }
                }
            }

            var radius = center.DistanceTo(start);
            var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
            return new ArcSegment(center, radius, startAngle, Sweep(center, start, end, clockwise, _multiQuadrant));
        }

        private static double Sweep(Vec2 center, Vec2 start, Vec2 end, bool clockwise, bool fullCircle)
        {
            var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            var a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            var ccw = a1 - a0;
            while (ccw <= 0)
            {
                ccw += 2 * Math.PI;
            }

            while (ccw > 2 * Math.PI)
            {
                ccw -= 2 * Math.PI;
            }

            if (start.IsNear(end, 1e-9))
            {
                ccw = fullCircle ? 2 * Math.PI : 0;
                return clockwise ? -ccw : ccw;
            }

            return clockwise ? -(2 * Math.PI - ccw) : ccw;
        }

        private void Flash(Vec2 at)
        {
            if (_mode == GerberMode.Outline || !TryGetAperture(out var shape))
            {
                return;
            }

            // Parts of one aperture are combined first, then added to the image with the current polarity.
            var combined = new Paths64();
            foreach (var (ring, dark) in shape.Parts)
            {
                var placed = ClipperBridge.ToPaths(new[] { ring.Select(p => p + at).ToList() });
                combined = dark ? ClipperBridge.Union(combined, placed) : ClipperBridge.Difference(combined, placed);
            }

            Add(combined);
        }

        private void FlushStroke()
        {
            if (_mode == GerberMode.Outline)
            {
                if (_outlineSegments.Count > 0)
                {
                    _outlinePaths.Add(new ImportedPath(_layer, new List<Segment>(_outlineSegments)));
                    _outlineSegments.Clear();
                }

                return;
            }

            if (_stroke.Count == 0)
            {
                return;
            }

            if (TryGetAperture(out var shape))
            {
                if (!shape.IsCircle)
                {
                    Warn(Loc.T("Линии прямоугольной апертурой приближены скруглёнными (медь чуть шире).", "Lines drawn with a rectangular aperture are approximated by rounded ones (copper slightly wider)."));
                }

                Add(ClipperBridge.Stroke(_stroke, shape.StrokeWidth / 2));
            }

            _stroke.Clear();
        }

        private void CloseRegionContour()
        {
            if (_region.Count >= 3)
            {
                _regionContours.Add(new List<Vec2>(_region));
            }

            _region.Clear();
        }

        private bool TryGetAperture(out ApertureShape shape)
        {
            if (_aperture is int number && _apertures.TryGetValue(number, out shape!))
            {
                return true;
            }

            Warn(_aperture is null ? Loc.T("Рисование без выбранной апертуры пропущено.", "Drawing without a selected aperture was skipped.") : Loc.T($"Апертура D{_aperture} не определена.", $"Aperture D{_aperture} is not defined."));
            shape = null!;
            return false;
        }

        /// <summary>Adds an object to the image; objects of the same polarity are batched for speed.</summary>
        private void Add(Paths64 shape)
        {
            if (shape.Count == 0)
            {
                return;
            }

            if (_dark != _batchDark)
            {
                FlushBatch();
                _batchDark = _dark;
            }

            _batch.AddRange(shape);
        }

        private void FlushBatch()
        {
            if (_batch.Count == 0)
            {
                return;
            }

            _image = _batchDark ? ClipperBridge.Union(_image, _batch) : ClipperBridge.Difference(_image, _batch);
            _batch = new Paths64();
        }

        public void Finish()
        {
            FlushStroke();
            if (_mode == GerberMode.Outline)
            {
                Result.Contours.AddRange(ContourBuilder.Build(_outlinePaths, 0.02));
                return;
            }

            FlushBatch();
            foreach (var ring in ClipperBridge.FromPaths(_image))
            {
                var segments = new List<Segment>(ring.Count);
                for (var k = 0; k < ring.Count; k++)
                {
                    segments.Add(new LineSegment(ring[k], ring[(k + 1) % ring.Count]));
                }

                Result.Contours.Add(new Contour(0, segments, _layer));
            }
        }

        private double Coordinate(string value)
        {
            double number;
            if (value.Contains('.'))
            {
                number = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else
            {
                var negative = value.StartsWith('-');
                var digits = value.TrimStart('+', '-');
                if (_trailingZerosOmitted)
                {
                    digits = digits.PadRight(_integerDigits + _decimalDigits, '0');
                }

                number = long.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, _decimalDigits);
                if (negative)
                {
                    number = -number;
                }
            }

            return number * _scale;
        }

        private void Warn(string message)
        {
            if (_reported.Add(message))
            {
                Result.Warnings.Add(message);
            }
        }
    }
}
