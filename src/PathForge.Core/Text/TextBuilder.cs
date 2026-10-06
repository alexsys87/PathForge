using System.Globalization;
using System.Text;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Text;

/// <summary>Outlines of a laid-out text in drawing coordinates (mm).</summary>
public sealed record TextOutline(List<List<Vec2>> Rings, List<string> MissingCharacters);

/// <summary>Lays out text items with a TrueType font and keeps their contours in the project up to date.</summary>
public static class TextBuilder
{
    /// <summary>Chord tolerance for the curved parts of the letters (mm).</summary>
    public const double DefaultTolerance = 0.01;

    /// <summary>
    /// Closed outline rings of the text. Overlapping glyph parts and letters are merged (non-zero rule),
    /// so the rings are clean outer borders and holes that work with the even-odd rule of the operations.
    /// </summary>
    public static TextOutline Layout(TrueTypeFont font, TextItem item, double tolerance = DefaultTolerance)
    {
        var missing = new List<string>();
        var polygons = new List<IReadOnlyList<Vec2>>();
        if (item.HeightMm <= 0 || string.IsNullOrEmpty(item.Text))
        {
            return new TextOutline(new List<List<Vec2>>(), missing);
        }

        if (item.Variation.Count > 0 && font.IsVariable)
        {
            font = font.WithVariation(item.Variation);
        }

        var bend = ArcMapping(item, tolerance);

        var scale = item.HeightMm / Math.Max(1, font.CapHeight);
        var lineHeight = (font.Ascender - font.Descender + font.LineGap) * scale;
        if (lineHeight <= 0)
        {
            lineHeight = font.UnitsPerEm * 1.2 * scale;
        }

        lineHeight *= item.LineSpacing > 0 ? item.LineSpacing : 1;
        var lines = item.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var runes = lines[lineIndex].EnumerateRunes().Where(r => !Rune.IsControl(r)).ToList();
            var glyphs = runes.Select(r => font.GlyphIndex(r.Value)).ToList();
            // Pair kerning: how much closer (negative) each letter moves to the previous one.
            var kerning = new double[glyphs.Count];
            for (var i = 1; i < glyphs.Count && item.Kerning; i++)
            {
                kerning[i] = font.Kerning(glyphs[i - 1], glyphs[i]) * scale;
            }

            var width = glyphs.Sum(g => font.AdvanceWidth(g) * scale) + kerning.Sum() + item.LetterSpacing * Math.Max(0, glyphs.Count - 1);
            var penX = item.Alignment switch
            {
                TextAlignment.Center => item.X - width / 2,
                TextAlignment.Right => item.X - width,
                _ => item.X,
            };
            var baseline = item.Y - lineIndex * lineHeight;

            for (var i = 0; i < glyphs.Count; i++)
            {
                if (glyphs[i] == 0 && !Rune.IsWhiteSpace(runes[i]))
                {
                    var text = runes[i].ToString();
                    if (!missing.Contains(text))
                    {
                        missing.Add(text);
                    }
                }

                penX += kerning[i];
                var originX = penX;
                foreach (var path in font.GetPaths(glyphs[i]))
                {
                    var ring = path.Flatten(p => new Vec2(originX + p.X * scale, baseline + p.Y * scale), tolerance);
                    if (bend is not null)
                    {
                        ring = bend(ring);
                    }

                    if (ring.Count >= 3)
                    {
                        polygons.Add(ring);
                    }
                }

                penX += font.AdvanceWidth(glyphs[i]) * scale + item.LetterSpacing;
            }
        }

        var rings = polygons.Count == 0
            ? new List<List<Vec2>>()
            : ClipperBridge.FromPaths(ClipperBridge.Union(ClipperBridge.ToPaths(polygons)));

        if (item.Mirrored)
        {
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Count; i++)
                {
                    ring[i] = ring[i] with { X = 2 * item.X - ring[i].X };
                }

                ring.Reverse();
            }
        }

        // Reading order: letter by letter, line by line (along the arc: by the angle around the centre).
        rings = item.ArcRadius == 0
            ? rings.OrderByDescending(r => Math.Round(r.Max(p => p.Y) / lineHeight)).ThenBy(r => r.Min(p => p.X)).ToList()
            : rings.OrderBy(r => ReadingAngle(item, r)).ToList();
        return new TextOutline(rings, missing);
    }

    /// <summary>
    /// Bends a laid-out ring around the arc of <see cref="TextItem.ArcRadius"/>: the baseline becomes the circle,
    /// distance along the baseline becomes arc length, height above it the distance from the circle.
    /// Long straight edges are split first so that they follow the curve within the tolerance.
    /// </summary>
    private static Func<List<Vec2>, List<Vec2>>? ArcMapping(TextItem item, double tolerance)
    {
        if (item.ArcRadius == 0)
        {
            return null;
        }

        var radius = Math.Abs(item.ArcRadius);
        var top = item.ArcRadius > 0;
        var center = new Vec2(item.X, top ? item.Y - radius : item.Y + radius);
        // Chord error of a piece of length L on radius R is L² / 8R.
        var maxPiece = Math.Max(0.05, Math.Sqrt(8 * radius * Math.Max(1e-4, tolerance)));

        Vec2 Map(Vec2 p)
        {
            var along = p.X - item.X;
            var up = p.Y - item.Y;
            var angle = top ? Math.PI / 2 - along / radius : -Math.PI / 2 + along / radius;
            var distance = top ? radius + up : radius - up;
            return new Vec2(center.X + distance * Math.Cos(angle), center.Y + distance * Math.Sin(angle));
        }

        return ring =>
        {
            var result = new List<Vec2>(ring.Count * 2);
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var pieces = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / maxPiece));
                for (var k = 0; k < pieces; k++)
                {
                    result.Add(Map(Vec2.Lerp(a, b, (double)k / pieces)));
                }
            }

            return result;
        };
    }

    /// <summary>Position of a ring along an arc text, increasing in reading direction.</summary>
    private static double ReadingAngle(TextItem item, List<Vec2> ring)
    {
        var radius = Math.Abs(item.ArcRadius);
        var center = new Vec2(item.X, item.ArcRadius > 0 ? item.Y - radius : item.Y + radius);
        var c = ring.Aggregate(Vec2.Zero, (s, p) => s + p) / ring.Count;
        var angle = Math.Atan2(c.Y - center.Y, c.X - center.X);
        // Over the top the text runs clockwise from the left, under the bottom counter-clockwise.
        return item.ArcRadius > 0 ? -NormalizeAngle(angle - Math.PI / 2) : NormalizeAngle(angle + Math.PI / 2);
    }

    private static double NormalizeAngle(double a)
    {
        while (a <= -Math.PI)
        {
            a += 2 * Math.PI;
        }

        while (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }

        return a;
    }

    /// <summary>
    /// Rebuilds the contours of <paramref name="item"/>. Operations that used the old contours of the text
    /// get the new ones, so the text can be edited after the operations were set up.
    /// </summary>
    public static List<string> Apply(CamProject project, TextItem item, TrueTypeFont font)
    {
        var warnings = new List<string>();
        var outline = Layout(font, item);
        if (outline.MissingCharacters.Count > 0)
        {
            warnings.Add(Loc.T($"В шрифте «{font.DisplayName}» нет символов: {string.Join(" ", outline.MissingCharacters)}", $"The font “{font.DisplayName}” has no glyphs for: {string.Join(" ", outline.MissingCharacters)}"));
        }

        var nextId = project.NextContourId();
        var oldIds = project.Contours.Where(c => c.TextId == item.Id).Select(c => c.Id).ToHashSet();
        project.Contours.RemoveAll(c => c.TextId == item.Id);

        var newIds = new List<int>();
        foreach (var ring in outline.Rings)
        {
            var segments = new List<Segment>(ring.Count);
            for (var i = 0; i < ring.Count; i++)
            {
                segments.Add(new LineSegment(ring[i], ring[(i + 1) % ring.Count]));
            }

            project.Contours.Add(new Contour(nextId, segments, item.Layer) { TextId = item.Id });
            newIds.Add(nextId++);
        }

        foreach (var operation in project.Operations)
        {
            if (operation.ContourIds.RemoveAll(oldIds.Contains) > 0)
            {
                operation.ContourIds.AddRange(newIds);
            }
        }

        if (string.IsNullOrEmpty(item.FontName))
        {
            item.FontName = font.DisplayName;
        }

        return warnings;
    }

    /// <summary>Removes the text and its contours (also from the operations).</summary>
    public static void Remove(CamProject project, TextItem item)
    {
        var ids = project.Contours.Where(c => c.TextId == item.Id).Select(c => c.Id).ToHashSet();
        project.Contours.RemoveAll(c => ids.Contains(c.Id));
        foreach (var operation in project.Operations)
        {
            operation.ContourIds.RemoveAll(ids.Contains);
        }

        project.Texts.Remove(item);
    }

    /// <summary>
    /// Mirrors a text item left-right around X = 0 together with the drawing (the contours themselves
    /// are transformed by the caller).
    /// </summary>
    public static void MirrorX(TextItem item)
    {
        item.X = -item.X;
        item.Mirrored = !item.Mirrored;
    }

    /// <summary>Polyline of one TrueType glyph contour (quadratic points) after mapping its points.</summary>
    internal static List<Vec2> FlattenGlyphContour(List<GlyphPoint> points, Func<GlyphPoint, Vec2> map, double tolerance) =>
        GlyphPath.FromQuadraticPoints(points.Select(p => (map(p), p.OnCurve)).ToList()).Flatten(p => p, tolerance);

    /// <summary>Short description of a text for lists.</summary>
    public static string Describe(TextItem item)
    {
        var text = item.Text.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
        if (text.Length > 24)
        {
            text = text[..24] + "…";
        }

        return Loc.T($"«{text}» — {item.FontName}, {item.HeightMm:0.##} мм", $"“{text}” — {item.FontName}, {item.HeightMm:0.##} mm");
    }
}
