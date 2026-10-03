using System.Globalization;
using System.Text;
using PathForge.Core.Geometry;

namespace PathForge.Core.Import;

/// <summary>Connected chain of segments read from one drawing entity.</summary>
public sealed record ImportedPath(string Layer, List<Segment> Segments);

public sealed class DxfImportResult
{
    public List<ImportedPath> Paths { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Factor applied to convert drawing units to millimetres.</summary>
    public double UnitScale { get; set; } = 1;
}

/// <summary>
/// Minimal ASCII DXF reader for 2D geometry: LINE, ARC, CIRCLE, LWPOLYLINE, POLYLINE, ELLIPSE, SPLINE
/// and block references (INSERT). All coordinates are converted to millimetres.
/// </summary>
public static class DxfReader
{
    private const int MaxBlockNesting = 16;

    public static DxfImportResult ReadFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    public static DxfImportResult Read(TextReader reader)
    {
        var pairs = ReadPairs(reader);
        var result = new DxfImportResult();
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<Entity>();

        var i = 0;
        while (i < pairs.Count)
        {
            if (pairs[i].Code == 0 && pairs[i].Value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Code == 2)
            {
                var name = pairs[i + 1].Value;
                i += 2;
                switch (name)
                {
                    case "HEADER":
                        i = ReadHeader(pairs, i, result);
                        break;
                    case "BLOCKS":
                        i = ReadBlocks(pairs, i, blocks);
                        break;
                    case "ENTITIES":
                        i = ReadEntities(pairs, i, entities, "ENDSEC");
                        break;
                    default:
                        i = SkipSection(pairs, i);
                        break;
                }
            }
            else
            {
                i++;
            }
        }

        var ignored = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var context = new Context(result, blocks, ignored);
        var root = Affine2.Scaling(result.UnitScale, result.UnitScale);
        ConvertEntities(entities, root, context, 0);

        foreach (var (type, count) in ignored)
        {
            result.Warnings.Add($"Ignored {count} entit{(count == 1 ? "y" : "ies")} of type {type}.");
        }

        return result;
    }

    private static List<GroupPair> ReadPairs(TextReader reader)
    {
        var pairs = new List<GroupPair>();
        while (true)
        {
            var codeLine = reader.ReadLine();
            var valueLine = reader.ReadLine();
            if (codeLine is null || valueLine is null)
            {
                break;
            }

            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                throw new FormatException($"Invalid DXF group code '{codeLine.Trim()}' (only ASCII DXF is supported).");
            }

            pairs.Add(new GroupPair(code, valueLine.Trim()));
        }

        return pairs;
    }

    private static int ReadHeader(List<GroupPair> pairs, int i, DxfImportResult result)
    {
        while (i < pairs.Count && !(pairs[i].Code == 0 && pairs[i].Value == "ENDSEC"))
        {
            if (pairs[i].Code == 9 && pairs[i].Value == "$INSUNITS" && i + 1 < pairs.Count)
            {
                result.UnitScale = pairs[i + 1].AsInt() switch
                {
                    1 => 25.4, // inches
                    2 => 304.8, // feet
                    5 => 10, // centimetres
                    6 => 1000, // metres
                    _ => 1, // millimetres or unitless
                };
            }

            i++;
        }

        return i + 1;
    }

    private static int SkipSection(List<GroupPair> pairs, int i)
    {
        while (i < pairs.Count && !(pairs[i].Code == 0 && pairs[i].Value == "ENDSEC"))
        {
            i++;
        }

        return i + 1;
    }

    private static int ReadBlocks(List<GroupPair> pairs, int i, Dictionary<string, Block> blocks)
    {
        while (i < pairs.Count && !(pairs[i].Code == 0 && pairs[i].Value == "ENDSEC"))
        {
            if (pairs[i].Code == 0 && pairs[i].Value == "BLOCK")
            {
                var header = ReadEntity(pairs, ref i);
                var block = new Block(header.GetString(2), new Vec2(header.GetDouble(10), header.GetDouble(20)));
                i = ReadEntities(pairs, i, block.Entities, "ENDBLK");
                blocks[block.Name] = block;
            }
            else
            {
                i++;
            }
        }

        return i + 1;
    }

    private static int ReadEntities(List<GroupPair> pairs, int i, List<Entity> target, string terminator)
    {
        while (i < pairs.Count)
        {
            if (pairs[i].Code == 0 && (pairs[i].Value == terminator || pairs[i].Value == "ENDSEC"))
            {
                if (pairs[i].Value == terminator)
                {
                    // Skip the terminator record itself (ENDBLK has its own group codes).
                    ReadEntity(pairs, ref i);
                }

                return i;
            }

            if (pairs[i].Code == 0)
            {
                target.Add(ReadEntity(pairs, ref i));
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static Entity ReadEntity(List<GroupPair> pairs, ref int i)
    {
        var entity = new Entity(pairs[i].Value);
        i++;
        while (i < pairs.Count && pairs[i].Code != 0)
        {
            entity.Groups.Add(pairs[i]);
            i++;
        }

        return entity;
    }

    private static void ConvertEntities(List<Entity> entities, Affine2 transform, Context context, int depth)
    {
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var layer = entity.GetString(8, "0");
            var ocs = entity.GetDouble(230, 1) < 0 ? Affine2.Then(Affine2.Scaling(-1, 1), transform) : transform;
            switch (entity.Type)
            {
                case "LINE":
                    AddPath(context, layer, ocs, new LineSegment(entity.Point(10), entity.Point(11)));
                    break;
                case "CIRCLE":
                    AddPath(context, layer, ocs, new ArcSegment(entity.Point(10), entity.GetDouble(40), 0, 2 * Math.PI));
                    break;
                case "ARC":
                    AddPath(context, layer, ocs, ReadArc(entity));
                    break;
                case "LWPOLYLINE":
                    AddPath(context, layer, ocs, ReadLwPolyline(entity));
                    break;
                case "POLYLINE":
                    AddPath(context, layer, ocs, ReadPolyline(entity, entities, ref i, context));
                    break;
                case "ELLIPSE":
                    AddPath(context, layer, transform, ReadEllipse(entity));
                    break;
                case "SPLINE":
                    AddPath(context, layer, transform, ReadSpline(entity, context));
                    break;
                case "INSERT":
                    ConvertInsert(entity, transform, context, depth);
                    break;
                default:
                    context.Ignored[entity.Type] = context.Ignored.GetValueOrDefault(entity.Type) + 1;
                    break;
            }
        }
    }

    private static void ConvertInsert(Entity entity, Affine2 transform, Context context, int depth)
    {
        var name = entity.GetString(2);
        if (!context.Blocks.TryGetValue(name, out var block))
        {
            context.Result.Warnings.Add($"Block '{name}' referenced by INSERT was not found.");
            return;
        }

        if (depth >= MaxBlockNesting)
        {
            context.Result.Warnings.Add($"Block '{name}' is nested too deeply and was skipped.");
            return;
        }

        var scaleX = entity.GetDouble(41, 1);
        var scaleY = entity.GetDouble(42, 1);
        var rotation = entity.GetDouble(50) * Math.PI / 180;
        var columns = Math.Max(1, entity.GetInt(70, 1));
        var rows = Math.Max(1, entity.GetInt(71, 1));
        var columnSpacing = entity.GetDouble(44);
        var rowSpacing = entity.GetDouble(45);
        var insertPoint = entity.Point(10);
        var ocsMirror = entity.GetDouble(230, 1) < 0;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var local = Affine2.Translation(-block.BasePoint.X, -block.BasePoint.Y);
                local = Affine2.Then(local, Affine2.Scaling(scaleX, scaleY));
                local = Affine2.Then(local, Affine2.Translation(column * columnSpacing, row * rowSpacing));
                local = Affine2.Then(local, Affine2.Rotation(rotation));
                local = Affine2.Then(local, Affine2.Translation(insertPoint.X, insertPoint.Y));
                if (ocsMirror)
                {
                    local = Affine2.Then(local, Affine2.Scaling(-1, 1));
                }

                ConvertEntities(block.Entities, Affine2.Then(local, transform), context, depth + 1);
            }
        }
    }

    private static Segment ReadArc(Entity entity)
    {
        var start = entity.GetDouble(50) * Math.PI / 180;
        var end = entity.GetDouble(51) * Math.PI / 180;
        var sweep = end - start;
        while (sweep <= 0)
        {
            sweep += 2 * Math.PI;
        }

        while (sweep > 2 * Math.PI)
        {
            sweep -= 2 * Math.PI;
        }

        return new ArcSegment(entity.Point(10), entity.GetDouble(40), start, sweep);
    }

    private static List<Segment> ReadLwPolyline(Entity entity)
    {
        var vertices = new List<(Vec2 Point, double Bulge)>();
        double? x = null;
        foreach (var group in entity.Groups)
        {
            switch (group.Code)
            {
                case 10:
                    x = group.AsDouble();
                    break;
                case 20 when x.HasValue:
                    vertices.Add((new Vec2(x.Value, group.AsDouble()), 0));
                    x = null;
                    break;
                case 42 when vertices.Count > 0:
                    vertices[^1] = (vertices[^1].Point, group.AsDouble());
                    break;
            }
        }

        var closed = (entity.GetInt(70) & 1) != 0;
        return BuildPolyline(vertices, closed);
    }

    private static List<Segment> ReadPolyline(Entity header, List<Entity> entities, ref int index, Context context)
    {
        var flags = header.GetInt(70);
        var vertices = new List<(Vec2 Point, double Bulge)>();
        while (index + 1 < entities.Count && entities[index + 1].Type == "VERTEX")
        {
            index++;
            var vertex = entities[index];
            var vertexFlags = vertex.GetInt(70);
            // 16 = spline frame control point, 64/128 = polyface mesh vertex: not part of the outline.
            if ((vertexFlags & (16 | 64 | 128)) != 0)
            {
                continue;
            }

            vertices.Add((vertex.Point(10), vertex.GetDouble(42)));
        }

        if (index + 1 < entities.Count && entities[index + 1].Type == "SEQEND")
        {
            index++;
        }

        if ((flags & (16 | 64)) != 0)
        {
            context.Ignored["POLYLINE (3D mesh)"] = context.Ignored.GetValueOrDefault("POLYLINE (3D mesh)") + 1;
            return new List<Segment>();
        }

        return BuildPolyline(vertices, (flags & 1) != 0);
    }

    private static List<Segment> BuildPolyline(List<(Vec2 Point, double Bulge)> vertices, bool closed)
    {
        var segments = new List<Segment>();
        for (var i = 0; i + 1 < vertices.Count; i++)
        {
            segments.Add(ArcSegment.FromBulge(vertices[i].Point, vertices[i + 1].Point, vertices[i].Bulge));
        }

        if (closed && vertices.Count >= 2 && !vertices[^1].Point.IsNear(vertices[0].Point, 1e-9))
        {
            segments.Add(ArcSegment.FromBulge(vertices[^1].Point, vertices[0].Point, vertices[^1].Bulge));
        }

        return segments;
    }

    private static List<Segment> ReadEllipse(Entity entity)
    {
        var center = entity.Point(10);
        var major = entity.Point(11);
        var ratio = entity.GetDouble(40, 1);
        var start = entity.GetDouble(41);
        var end = entity.GetDouble(42, 2 * Math.PI);
        if (entity.GetDouble(230, 1) < 0)
        {
            // Mirrored object coordinate system.
            center = new Vec2(-center.X, center.Y);
            major = new Vec2(-major.X, major.Y);
            (start, end) = (-end, -start);
        }

        var sweep = end - start;
        while (sweep <= 0)
        {
            sweep += 2 * Math.PI;
        }

        var minor = major.PerpendicularLeft * ratio;
        var majorLength = major.Length;
        var count = ArcSegment.SegmentCount(majorLength, sweep, Contour.DefaultFlattenTolerance / 2);
        count = Math.Max(count, 16);
        var points = new List<Vec2>(count + 1);
        for (var k = 0; k <= count; k++)
        {
            var t = start + sweep * k / count;
            points.Add(center + major * Math.Cos(t) + minor * Math.Sin(t));
        }

        return ToLines(points);
    }

    private static List<Segment> ReadSpline(Entity entity, Context context)
    {
        var degree = entity.GetInt(71, 3);
        var knots = entity.All(40).ToList();
        var weights = entity.All(41).ToList();
        var controlPoints = entity.Points(10, 20);
        var fitPoints = entity.Points(11, 21);

        List<Vec2> points;
        if (controlPoints.Count > degree && knots.Count == controlPoints.Count + degree + 1)
        {
            if (weights.Count != controlPoints.Count)
            {
                weights = Enumerable.Repeat(1.0, controlPoints.Count).ToList();
            }

            var samples = Math.Max(32, controlPoints.Count * 16);
            var t0 = knots[degree];
            var t1 = knots[controlPoints.Count];
            points = new List<Vec2>(samples + 1);
            for (var k = 0; k <= samples; k++)
            {
                points.Add(EvaluateNurbs(degree, knots, controlPoints, weights, t0 + (t1 - t0) * k / samples));
            }
        }
        else if (fitPoints.Count > 1)
        {
            points = fitPoints;
            context.Result.Warnings.Add("SPLINE without control points was approximated by its fit points.");
        }
        else
        {
            context.Ignored["SPLINE (invalid)"] = context.Ignored.GetValueOrDefault("SPLINE (invalid)") + 1;
            return new List<Segment>();
        }

        if ((entity.GetInt(70) & 1) != 0 && !points[^1].IsNear(points[0], 1e-9))
        {
            points.Add(points[0]);
        }

        return ToLines(points);
    }

    /// <summary>De Boor evaluation of a (rational) B-spline.</summary>
    private static Vec2 EvaluateNurbs(int degree, List<double> knots, List<Vec2> controlPoints, List<double> weights, double t)
    {
        var n = controlPoints.Count - 1;
        var span = degree;
        while (span < n && t >= knots[span + 1])
        {
            span++;
        }

        var d = new (double X, double Y, double W)[degree + 1];
        for (var j = 0; j <= degree; j++)
        {
            var index = j + span - degree;
            var w = weights[index];
            d[j] = (controlPoints[index].X * w, controlPoints[index].Y * w, w);
        }

        for (var r = 1; r <= degree; r++)
        {
            for (var j = degree; j >= r; j--)
            {
                var left = knots[j + span - degree];
                var right = knots[j + 1 + span - r];
                var alpha = right - left < 1e-12 ? 0 : (t - left) / (right - left);
                d[j] = (
                    (1 - alpha) * d[j - 1].X + alpha * d[j].X,
                    (1 - alpha) * d[j - 1].Y + alpha * d[j].Y,
                    (1 - alpha) * d[j - 1].W + alpha * d[j].W);
            }
        }

        var result = d[degree];
        return result.W == 0 ? new Vec2(result.X, result.Y) : new Vec2(result.X / result.W, result.Y / result.W);
    }

    private static List<Segment> ToLines(List<Vec2> points)
    {
        var segments = new List<Segment>(points.Count);
        for (var i = 1; i < points.Count; i++)
        {
            if (!points[i].IsNear(points[i - 1], 1e-9))
            {
                segments.Add(new LineSegment(points[i - 1], points[i]));
            }
        }

        return segments;
    }

    private static void AddPath(Context context, string layer, Affine2 transform, Segment segment) =>
        AddPath(context, layer, transform, new List<Segment> { segment });

    private static void AddPath(Context context, string layer, Affine2 transform, List<Segment> segments)
    {
        var transformed = new List<Segment>(segments.Count);
        var similarity = transform.IsSimilarity(out _);
        foreach (var segment in segments)
        {
            if (segment.Length < 1e-9)
            {
                continue;
            }

            if (segment is ArcSegment && !similarity)
            {
                // Non-uniform scaling: approximate the arc with lines before transforming.
                var points = new List<Vec2> { segment.Start };
                segment.AppendPoints(points, Contour.DefaultFlattenTolerance / 2);
                transformed.AddRange(ToLines(points).Select(s => s.Transform(transform)));
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

    private readonly record struct GroupPair(int Code, string Value)
    {
        public double AsDouble() =>
            double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public int AsInt() =>
            int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private sealed class Entity
    {
        public Entity(string type)
        {
            Type = type;
        }

        public string Type { get; }

        public List<GroupPair> Groups { get; } = new();

        public string GetString(int code, string fallback = "")
        {
            foreach (var g in Groups)
            {
                if (g.Code == code)
                {
                    return g.Value;
                }
            }

            return fallback;
        }

        public double GetDouble(int code, double fallback = 0)
        {
            foreach (var g in Groups)
            {
                if (g.Code == code)
                {
                    return g.AsDouble();
                }
            }

            return fallback;
        }

        public int GetInt(int code, int fallback = 0)
        {
            foreach (var g in Groups)
            {
                if (g.Code == code)
                {
                    return g.AsInt();
                }
            }

            return fallback;
        }

        public IEnumerable<double> All(int code) => Groups.Where(g => g.Code == code).Select(g => g.AsDouble());

        public Vec2 Point(int xCode) => new(GetDouble(xCode), GetDouble(xCode + 10));

        /// <summary>Repeated X/Y pairs in file order (e.g. spline control points).</summary>
        public List<Vec2> Points(int xCode, int yCode)
        {
            var points = new List<Vec2>();
            double? x = null;
            foreach (var g in Groups)
            {
                if (g.Code == xCode)
                {
                    x = g.AsDouble();
                }
                else if (g.Code == yCode && x.HasValue)
                {
                    points.Add(new Vec2(x.Value, g.AsDouble()));
                    x = null;
                }
            }

            return points;
        }
    }

    private sealed class Block
    {
        public Block(string name, Vec2 basePoint)
        {
            Name = name;
            BasePoint = basePoint;
        }

        public string Name { get; }

        public Vec2 BasePoint { get; }

        public List<Entity> Entities { get; } = new();
    }

    private sealed record Context(DxfImportResult Result, Dictionary<string, Block> Blocks, SortedDictionary<string, int> Ignored);
}
