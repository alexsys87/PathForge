using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PathForge.Core.Geometry;

namespace PathForge.Core.Import;

/// <summary>
/// SVG 1.1 reader for cutting geometry: path (all commands), rect, circle, ellipse, line, polyline,
/// polygon and use; nested transforms; document units and viewBox; Inkscape layers.
/// Coordinates are converted to millimetres with Y pointing up. Written from the public SVG specification.
/// </summary>
public static partial class SvgReader
{
    /// <summary>CSS pixel: 1/96 inch.</summary>
    private const double PxToMm = 25.4 / 96;

    private const double FlattenTolerance = 0.005;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Inkscape = "http://www.inkscape.org/namespaces/inkscape";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    public static DxfImportResult ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static DxfImportResult Read(Stream stream)
    {
        // External entities and DTDs are never resolved.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return Read(XDocument.Load(reader));
    }

    public static DxfImportResult ReadText(string svg)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
        return Read(stream);
    }

    private static DxfImportResult Read(XDocument document)
    {
        var root = document.Root ?? throw new FormatException("Пустой SVG.");
        var result = new DxfImportResult();
        var context = new Context(result, document);
        context.Root = DocumentTransform(root, result);
        foreach (var child in root.Elements())
        {
            Visit(child, context.Root, "", context, depth: 0);
        }

        foreach (var (name, count) in context.Ignored)
        {
            result.Warnings.Add($"Пропущено элементов <{name}>: {count}.");
        }

        return result;
    }

    /// <summary>Maps user units to millimetres and flips Y so that the drawing lies in the first quadrant.</summary>
    private static Affine2 DocumentTransform(XElement root, DxfImportResult result)
    {
        var width = Length(root.Attribute("width")?.Value);
        var height = Length(root.Attribute("height")?.Value);
        var viewBox = Numbers(root.Attribute("viewBox")?.Value ?? "");

        double scaleX = PxToMm, scaleY = PxToMm, minX = 0, minY = 0, userHeight;
        if (viewBox.Count == 4 && viewBox[2] > 0 && viewBox[3] > 0)
        {
            minX = viewBox[0];
            minY = viewBox[1];
            userHeight = viewBox[3];
            if (width is { } w)
            {
                scaleX = w / viewBox[2];
            }

            if (height is { } h)
            {
                scaleY = h / viewBox[3];
            }
            else
            {
                scaleY = scaleX;
            }

            if (width is null && height is not null)
            {
                scaleX = scaleY;
            }

            if (Math.Abs(scaleX - scaleY) > 1e-9 * Math.Max(scaleX, scaleY))
            {
                result.Warnings.Add("Пропорции viewBox и размеров документа различаются: использован масштаб по ширине.");
                scaleY = scaleX;
            }
        }
        else
        {
            userHeight = height is { } h ? h / PxToMm : 0;
        }

        // x_mm = s (x - minX); y_mm = s (minY + H - y)
        return new Affine2(scaleX, 0, 0, -scaleY, -scaleX * minX, scaleY * (minY + userHeight));
    }

    private static void Visit(XElement element, Affine2 parent, string layer, Context context, int depth)
    {
        if (depth > 64 || element.Name.Namespace != Svg || IsHidden(element))
        {
            return;
        }

        var transform = Affine2.Then(ParseTransform(element.Attribute("transform")?.Value), parent);
        switch (element.Name.LocalName)
        {
            case "g":
            case "a":
            case "switch":
            {
                var groupLayer = layer;
                if ((string?)element.Attribute(Inkscape + "groupmode") == "layer")
                {
                    groupLayer = (string?)element.Attribute(Inkscape + "label") ?? (string?)element.Attribute("id") ?? layer;
                }
                else if (depth == 0 && string.IsNullOrEmpty(layer) && element.Attribute("id") is { } id)
                {
                    groupLayer = id.Value;
                }

                foreach (var child in element.Elements())
                {
                    Visit(child, transform, groupLayer, context, depth + 1);
                }

                break;
            }

            case "svg":
            {
                var offset = Affine2.Translation(Number(element, "x"), Number(element, "y"));
                foreach (var child in element.Elements())
                {
                    Visit(child, Affine2.Then(offset, transform), layer, context, depth + 1);
                }

                break;
            }

            case "use":
            {
                var href = ((string?)element.Attribute(XLink + "href") ?? (string?)element.Attribute("href") ?? "").TrimStart('#');
                if (context.ById.TryGetValue(href, out var target))
                {
                    var offset = Affine2.Then(Affine2.Translation(Number(element, "x"), Number(element, "y")), transform);
                    if (target.Name.LocalName == "symbol")
                    {
                        foreach (var child in target.Elements())
                        {
                            Visit(child, offset, layer, context, depth + 1);
                        }
                    }
                    else
                    {
                        Visit(target, offset, layer, context, depth + 1);
                    }
                }

                break;
            }

            case "path":
                AddPath(element.Attribute("d")?.Value ?? "", transform, Layer(layer), context);
                break;
            case "rect":
                AddRect(element, transform, Layer(layer), context);
                break;
            case "circle":
                AddEllipse(Number(element, "cx"), Number(element, "cy"), Number(element, "r"), Number(element, "r"), transform, Layer(layer), context);
                break;
            case "ellipse":
                AddEllipse(Number(element, "cx"), Number(element, "cy"), Number(element, "rx"), Number(element, "ry"), transform, Layer(layer), context);
                break;
            case "line":
                Emit(context, Layer(layer), transform, new List<Segment>
                {
                    new LineSegment(new Vec2(Number(element, "x1"), Number(element, "y1")), new Vec2(Number(element, "x2"), Number(element, "y2"))),
                });
                break;
            case "polyline":
            case "polygon":
            {
                var values = Numbers(element.Attribute("points")?.Value ?? "");
                var points = new List<Vec2>();
                for (var i = 0; i + 1 < values.Count; i += 2)
                {
                    points.Add(new Vec2(values[i], values[i + 1]));
                }

                if (element.Name.LocalName == "polygon" && points.Count > 2)
                {
                    points.Add(points[0]);
                }

                Emit(context, Layer(layer), transform, Lines(points));
                break;
            }

            case "defs":
            case "symbol":
            case "clipPath":
            case "mask":
            case "pattern":
            case "marker":
            case "linearGradient":
            case "radialGradient":
            case "style":
            case "title":
            case "desc":
            case "metadata":
                break;
            default:
                context.Ignored[element.Name.LocalName] = context.Ignored.GetValueOrDefault(element.Name.LocalName) + 1;
                break;
        }
    }

    private static string Layer(string layer) => string.IsNullOrEmpty(layer) ? "SVG" : layer;

    private static bool IsHidden(XElement element)
    {
        if ((string?)element.Attribute("display") == "none" || (string?)element.Attribute("visibility") == "hidden")
        {
            return true;
        }

        var style = (string?)element.Attribute("style") ?? "";
        return StyleHiddenRegex().IsMatch(style);
    }

    private static void AddRect(XElement element, Affine2 transform, string layer, Context context)
    {
        var x = Number(element, "x");
        var y = Number(element, "y");
        var w = Number(element, "width");
        var h = Number(element, "height");
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var rxAttr = element.Attribute("rx");
        var ryAttr = element.Attribute("ry");
        var rx = rxAttr is null ? (ryAttr is null ? 0 : Number(element, "ry")) : Number(element, "rx");
        var ry = ryAttr is null ? rx : Number(element, "ry");
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);

        if (rx <= 0 || ry <= 0)
        {
            Emit(context, layer, transform, Lines(new List<Vec2>
            {
                new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h), new(x, y),
            }));
            return;
        }

        // Rounded rectangle: four sides and four elliptical corners.
        var path = FormattableString.Invariant(
            $"M {x + rx} {y} H {x + w - rx} A {rx} {ry} 0 0 1 {x + w} {y + ry} V {y + h - ry} A {rx} {ry} 0 0 1 {x + w - rx} {y + h} H {x + rx} A {rx} {ry} 0 0 1 {x} {y + h - ry} V {y + ry} A {rx} {ry} 0 0 1 {x + rx} {y} Z");
        AddPath(path, transform, layer, context);
    }

    private static void AddEllipse(double cx, double cy, double rx, double ry, Affine2 transform, string layer, Context context)
    {
        if (rx <= 0 || ry <= 0)
        {
            return;
        }

        if (Math.Abs(rx - ry) < 1e-12 && transform.IsSimilarity(out _))
        {
            Emit(context, layer, transform, new List<Segment> { new ArcSegment(new Vec2(cx, cy), rx, 0, 2 * Math.PI) });
            return;
        }

        var points = new List<Vec2>();
        var count = Math.Max(32, ArcSegment.SegmentCount(Math.Max(rx, ry) * Scale(transform), 2 * Math.PI, FlattenTolerance));
        for (var i = 0; i <= count; i++)
        {
            var t = 2 * Math.PI * i / count;
            points.Add(new Vec2(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }

        Emit(context, layer, transform, Lines(points));
    }

    private static void AddPath(string data, Affine2 transform, string layer, Context context)
    {
        var tokens = new PathTokens(data);
        var tolerance = FlattenTolerance / Math.Max(1e-9, Scale(transform));
        var current = Vec2.Zero;
        var start = Vec2.Zero;
        var lastControl = (Vec2?)null;
        var lastCommand = ' ';
        var segments = new List<Segment>();
        var command = ' ';

        void Flush()
        {
            if (segments.Count > 0)
            {
                Emit(context, layer, transform, segments);
                segments = new List<Segment>();
            }
        }

        void LineTo(Vec2 p)
        {
            if (!p.IsNear(current, 1e-12))
            {
                segments.Add(new LineSegment(current, p));
            }

            current = p;
        }

        while (tokens.TryCommand(ref command))
        {
            var relative = char.IsLower(command);
            Vec2 Point(Vec2 p) => relative ? current + p : p;
            var upper = char.ToUpperInvariant(command);
            switch (upper)
            {
                case 'M':
                    Flush();
                    current = Point(tokens.Point());
                    start = current;
                    // Further coordinate pairs after M are implicit line-to commands.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    LineTo(Point(tokens.Point()));
                    break;
                case 'H':
                {
                    var x = tokens.Number();
                    LineTo(new Vec2(relative ? current.X + x : x, current.Y));
                    break;
                }

                case 'V':
                {
                    var y = tokens.Number();
                    LineTo(new Vec2(current.X, relative ? current.Y + y : y));
                    break;
                }

                case 'C':
                case 'S':
                {
                    Vec2 c1;
                    if (upper == 'C')
                    {
                        c1 = Point(tokens.Point());
                    }
                    else
                    {
                        c1 = lastControl is { } lc && lastCommand is 'C' or 'S' ? current * 2 - lc : current;
                    }

                    var c2 = Point(tokens.Point());
                    var end = Point(tokens.Point());
                    FlattenCubic(current, c1, c2, end, tolerance, segments);
                    lastControl = c2;
                    current = end;
                    break;
                }

                case 'Q':
                case 'T':
                {
                    var c = upper == 'Q'
                        ? Point(tokens.Point())
                        : (lastControl is { } lc && lastCommand is 'Q' or 'T' ? current * 2 - lc : current);
                    var end = Point(tokens.Point());
                    // A quadratic curve is a cubic with control points at 2/3 towards the quadratic control.
                    FlattenCubic(current, current + (c - current) * (2.0 / 3), end + (c - end) * (2.0 / 3), end, tolerance, segments);
                    lastControl = c;
                    current = end;
                    break;
                }

                case 'A':
                {
                    var rx = Math.Abs(tokens.Number());
                    var ry = Math.Abs(tokens.Number());
                    var rotation = tokens.Number();
                    var largeArc = tokens.Flag();
                    var sweep = tokens.Flag();
                    var end = Point(tokens.Point());
                    AddArc(current, end, rx, ry, rotation, largeArc, sweep, transform, tolerance, segments);
                    current = end;
                    break;
                }

                case 'Z':
                    LineTo(start);
                    Flush();
                    current = start;
                    break;
                default:
                    throw new FormatException($"Неизвестная команда пути SVG '{command}'.");
            }

            if (upper is not ('C' or 'S' or 'Q' or 'T'))
            {
                lastControl = null;
            }

            lastCommand = upper;
        }

        Flush();
    }

    /// <summary>Elliptical arc in endpoint form (SVG spec, implementation notes F.6.5).</summary>
    private static void AddArc(Vec2 from, Vec2 to, double rx, double ry, double rotationDeg, bool largeArc, bool sweep,
        Affine2 transform, double tolerance, List<Segment> segments)
    {
        if (from.IsNear(to, 1e-12))
        {
            return;
        }

        if (rx < 1e-12 || ry < 1e-12)
        {
            segments.Add(new LineSegment(from, to));
            return;
        }

        var phi = rotationDeg * Math.PI / 180;
        var cos = Math.Cos(phi);
        var sin = Math.Sin(phi);
        var half = (from - to) / 2;
        var x1 = cos * half.X + sin * half.Y;
        var y1 = -sin * half.X + cos * half.Y;

        // Enlarge the radii when they are too small to reach the end point.
        var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            var s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        var numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var factor = Math.Sqrt(Math.Max(0, numerator / denominator)) * (largeArc == sweep ? -1 : 1);
        var cx1 = factor * rx * y1 / ry;
        var cy1 = -factor * ry * x1 / rx;
        var mid = (from + to) / 2;
        var center = new Vec2(cos * cx1 - sin * cy1 + mid.X, sin * cx1 + cos * cy1 + mid.Y);

        static double Angle(double ux, double uy, double vx, double vy) =>
            Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

        var theta1 = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        var delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        if (Math.Abs(rx - ry) < 1e-9 * Math.Max(rx, ry) && transform.IsSimilarity(out _))
        {
            // A circular arc stays a true arc (theta measured from the rotated x axis).
            segments.Add(new ArcSegment(center, rx, theta1 + phi, delta));
            return;
        }

        var count = Math.Max(4, ArcSegment.SegmentCount(Math.Max(rx, ry), delta, tolerance));
        var previous = from;
        for (var i = 1; i <= count; i++)
        {
            var t = theta1 + delta * i / count;
            var px = rx * Math.Cos(t);
            var py = ry * Math.Sin(t);
            var p = i == count ? to : new Vec2(cos * px - sin * py + center.X, sin * px + cos * py + center.Y);
            segments.Add(new LineSegment(previous, p));
            previous = p;
        }
    }

    /// <summary>Adaptive subdivision of a cubic Bézier until each piece is flat within the tolerance.</summary>
    private static void FlattenCubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance, List<Segment> segments, int depth = 0)
    {
        var flatness = Math.Max(Polyline.DistanceToSegment(p1, p0, p3), Polyline.DistanceToSegment(p2, p0, p3));
        if (depth >= 16 || flatness <= tolerance)
        {
            if (!p0.IsNear(p3, 1e-12))
            {
                segments.Add(new LineSegment(p0, p3));
            }

            return;
        }

        var p01 = (p0 + p1) / 2;
        var p12 = (p1 + p2) / 2;
        var p23 = (p2 + p3) / 2;
        var p012 = (p01 + p12) / 2;
        var p123 = (p12 + p23) / 2;
        var mid = (p012 + p123) / 2;
        FlattenCubic(p0, p01, p012, mid, tolerance, segments, depth + 1);
        FlattenCubic(mid, p123, p23, p3, tolerance, segments, depth + 1);
    }

    private static void Emit(Context context, string layer, Affine2 transform, List<Segment> segments)
    {
        var similarity = transform.IsSimilarity(out _);
        var transformed = new List<Segment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is ArcSegment && !similarity)
            {
                var points = new List<Vec2> { segment.Start };
                segment.AppendPoints(points, FlattenTolerance);
                transformed.AddRange(Lines(points).Select(s => s.Transform(transform)));
            }
            else
            {
                transformed.Add(segment.Transform(transform));
            }
        }

        if (transformed.Count > 0)
        {
            context.Result.Paths.Add(new ImportedPath(layer, transformed));
        }
    }

    private static List<Segment> Lines(List<Vec2> points)
    {
        var segments = new List<Segment>();
        for (var i = 1; i < points.Count; i++)
        {
            if (!points[i].IsNear(points[i - 1], 1e-12))
            {
                segments.Add(new LineSegment(points[i - 1], points[i]));
            }
        }

        return segments;
    }

    private static double Scale(Affine2 t) => Math.Sqrt(Math.Abs(t.Determinant));

    /// <summary>Composes an SVG transform list: the rightmost function is applied first.</summary>
    internal static Affine2 ParseTransform(string? text)
    {
        var result = Affine2.Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match m in TransformRegex().Matches(text))
        {
            var v = Numbers(m.Groups[2].Value);
            double V(int i, double fallback = 0) => i < v.Count ? v[i] : fallback;
            Affine2 f = m.Groups[1].Value switch
            {
                "matrix" => new Affine2(V(0, 1), V(2), V(1), V(3, 1), V(4), V(5)),
                "translate" => Affine2.Translation(V(0), V(1)),
                "scale" => Affine2.Scaling(V(0, 1), v.Count > 1 ? V(1) : V(0, 1)),
                "rotate" => Affine2.Then(
                    Affine2.Then(Affine2.Translation(-V(1), -V(2)), Affine2.Rotation(V(0) * Math.PI / 180)),
                    Affine2.Translation(V(1), V(2))),
                "skewX" => new Affine2(1, Math.Tan(V(0) * Math.PI / 180), 0, 1, 0, 0),
                "skewY" => new Affine2(1, 0, Math.Tan(V(0) * Math.PI / 180), 1, 0, 0),
                _ => Affine2.Identity,
            };
            // Functions further right act first: result = result ∘ f.
            result = Affine2.Then(f, result);
        }

        return result;
    }

    /// <summary>Length in millimetres for width/height attributes; null for missing or relative (%) values.</summary>
    private static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = LengthRegex().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }

        var value = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "mm" => value,
            "cm" => value * 10,
            "in" => value * 25.4,
            "pt" => value * 25.4 / 72,
            "pc" => value * 25.4 / 6,
            "px" or "" => value * PxToMm,
            _ => null,
        };
    }

    private static double Number(XElement element, string attribute)
    {
        var text = element.Attribute(attribute)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var m = LengthRegex().Match(text.Trim());
        return m.Success ? double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
    }

    private static List<double> Numbers(string text) =>
        NumberRegex().Matches(text).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-z%]*)$")]
    private static partial Regex LengthRegex();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformRegex();

    [GeneratedRegex(@"(display\s*:\s*none|visibility\s*:\s*hidden)")]
    private static partial Regex StyleHiddenRegex();

    private sealed class Context
    {
        public Context(DxfImportResult result, XDocument document)
        {
            Result = result;
            foreach (var element in document.Descendants())
            {
                if (element.Attribute("id") is { } id)
                {
                    ById[id.Value] = element;
                }
            }
        }

        public DxfImportResult Result { get; }

        public Affine2 Root { get; set; } = Affine2.Identity;

        public Dictionary<string, XElement> ById { get; } = new();

        public SortedDictionary<string, int> Ignored { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Scanner for path data: commands, numbers like "1.5.5" or "-1e-3", and packed arc flags.</summary>
    private sealed class PathTokens
    {
        private readonly string _text;
        private int _pos;

        public PathTokens(string text)
        {
            _text = text;
        }

        /// <summary>
        /// Advances to the next command letter, or keeps <paramref name="command"/> when numbers follow
        /// (implicit repetition). Returns false at the end.
        /// </summary>
        public bool TryCommand(ref char command)
        {
            SkipSeparators();
            if (_pos >= _text.Length)
            {
                return false;
            }

            var c = _text[_pos];
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                _pos++;
                command = c;
                return true;
            }

            if (command == ' ' || char.ToUpperInvariant(command) == 'Z')
            {
                throw new FormatException("Путь SVG должен начинаться с команды M.");
            }

            return true;
        }

        public Vec2 Point() => new(Number(), Number());

        public double Number()
        {
            SkipSeparators();
            var start = _pos;
            if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+'))
            {
                _pos++;
            }

            var dot = false;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || (_text[_pos] == '.' && !dot)))
            {
                dot |= _text[_pos] == '.';
                _pos++;
            }

            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+'))
                {
                    _pos++;
                }

                while (_pos < _text.Length && char.IsDigit(_text[_pos]))
                {
                    _pos++;
                }
            }

            if (_pos == start)
            {
                throw new FormatException($"В пути SVG ожидалось число (позиция {start}).");
            }

            return double.Parse(_text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>Arc flags are single digits and may be written without separators ("a5 5 0 015 5").</summary>
        public bool Flag()
        {
            SkipSeparators();
            if (_pos >= _text.Length || (_text[_pos] != '0' && _text[_pos] != '1'))
            {
                throw new FormatException("В дуге SVG ожидался флаг 0 или 1.");
            }

            return _text[_pos++] == '1';
        }

        private void SkipSeparators()
        {
            while (_pos < _text.Length && (char.IsWhiteSpace(_text[_pos]) || _text[_pos] == ','))
            {
                _pos++;
            }
        }
    }
}
