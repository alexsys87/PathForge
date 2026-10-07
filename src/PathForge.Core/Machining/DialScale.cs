using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Parameters of a dial scale around a pot or a switch.</summary>
public sealed class DialScaleSettings
{
    /// <summary>Centre of the dial (the shaft) in drawing coordinates.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Radius where the ticks start (mm): a little larger than the knob.</summary>
    public double Radius { get; set; } = 15;

    /// <summary>Angle of the first tick (degrees, counter-clockwise from +X): 225 = lower left.</summary>
    public double StartAngle { get; set; } = 225;

    /// <summary>Angle the scale covers clockwise (degrees): 270 for a pot, less for a switch.</summary>
    public double Sweep { get; set; } = 270;

    /// <summary>Number of steps between the first and the last tick.</summary>
    public int Divisions { get; set; } = 10;

    /// <summary>Every this many ticks is long and labelled (1 = all).</summary>
    public int MajorEvery { get; set; } = 1;

    public double MinorLength { get; set; } = 1.5;

    public double MajorLength { get; set; } = 3;

    /// <summary>Label of the first tick; the others go evenly up to <see cref="LabelTo"/>.</summary>
    public double LabelFrom { get; set; }

    public double LabelTo { get; set; } = 10;

    /// <summary>Height of the digits (mm, 0 = no labels).</summary>
    public double LabelHeight { get; set; } = 2.5;

    /// <summary>Draw the arc along the start of the ticks.</summary>
    public bool Arc { get; set; }

    /// <summary>
    /// 0: ticks and digits are lines (engraving along the line, laser). Above 0: they are closed outlines of this
    /// width (V-carving, pocket).
    /// </summary>
    public double StrokeWidth { get; set; }
}

/// <summary>Contours of a dial scale: ticks on an arc with numbers at the long ones.</summary>
public static class DialScale
{
    public static string Layer => Loc.T("Шкала", "Scale");

    public static List<Contour> Build(DialScaleSettings settings, int firstContourId)
    {
        var center = new Vec2(settings.X, settings.Y);
        var divisions = Math.Clamp(settings.Divisions, 1, 360);
        var major = Math.Max(1, settings.MajorEvery);
        var r = Math.Max(0.5, settings.Radius);
        // Each group is stroked together (a digit's segments join into one outline).
        var groups = new List<List<List<Vec2>>>();
        var arcs = new List<ArcSegment>();
        for (var i = 0; i <= divisions; i++)
        {
            var angle = (settings.StartAngle - settings.Sweep * i / divisions) * Math.PI / 180;
            var direction = new Vec2(Math.Cos(angle), Math.Sin(angle));
            var isMajor = i % major == 0 || i == divisions;
            var length = isMajor ? settings.MajorLength : settings.MinorLength;
            groups.Add(new List<List<Vec2>> { new() { center + direction * r, center + direction * (r + Math.Max(0.1, length)) } });
            if (!isMajor || settings.LabelHeight <= 0)
            {
                continue;
            }

            var value = settings.LabelFrom + (settings.LabelTo - settings.LabelFrom) * i / divisions;
            var text = Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
            var h = settings.LabelHeight;
            var w = SegmentDigits.Width(text, h);
            // The label's box sits beyond the tick, its centre on the tick's line.
            var reach = r + settings.MajorLength + h * 0.6 + Math.Abs(direction.X) * w / 2 + Math.Abs(direction.Y) * h / 2;
            var labelCenter = center + direction * reach;
            var digits = SegmentDigits.Text(text, labelCenter - new Vec2(w / 2, h / 2), h).ToList();
            groups.Add(digits.Select(d => d.Flatten(0.01)).ToList());
        }

        if (settings.Arc)
        {
            arcs.Add(new ArcSegment(center, r, settings.StartAngle * Math.PI / 180, -settings.Sweep * Math.PI / 180));
        }

        var contours = new List<Contour>();
        var id = firstContourId;
        if (settings.StrokeWidth <= 0)
        {
            foreach (var line in groups.SelectMany(g => g))
            {
                contours.Add(new Contour(id++, line.Zip(line.Skip(1)).Select(p => (Segment)new LineSegment(p.First, p.Second)), Layer));
            }

            contours.AddRange(arcs.Select(a => new Contour(id++, new Segment[] { a }, Layer)));
            return contours;
        }

        var half = settings.StrokeWidth / 2;
        var strokes = groups.Select(g => g.Select(l => (IReadOnlyList<Vec2>)l).ToList()).ToList();
        strokes.AddRange(arcs.Select(a => new List<IReadOnlyList<Vec2>> { new Contour(0, new Segment[] { a }).Flatten(0.01) }));
        foreach (var group in strokes)
        {
            var region = new Clipper2Lib.Paths64();
            foreach (var line in group)
            {
                region = ClipperBridge.Union(region, ClipperBridge.Stroke(line, half));
            }

            foreach (var ring in ClipperBridge.FromPaths(region).Where(ring => ring.Count >= 3))
            {
                contours.Add(new Contour(id++, ring.Select((p, k) => (Segment)new LineSegment(p, ring[(k + 1) % ring.Count])), Layer));
            }
        }

        return contours;
    }
}
