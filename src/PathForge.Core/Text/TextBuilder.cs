using System.Globalization;
using System.Text;
using PathForge.Core.Geometry;
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

    private const int MaxCurveSegments = 64;

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
            var width = glyphs.Sum(g => font.AdvanceWidth(g) * scale) + item.LetterSpacing * Math.Max(0, glyphs.Count - 1);
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

                var originX = penX;
                foreach (var contour in font.GetOutline(glyphs[i]))
                {
                    var ring = FlattenGlyphContour(contour, p => new Vec2(originX + p.X * scale, baseline + p.Y * scale), tolerance);
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

        // Reading order: letter by letter, line by line.
        rings = rings
            .OrderByDescending(r => Math.Round(r.Max(p => p.Y) / lineHeight))
            .ThenBy(r => r.Min(p => p.X))
            .ToList();
        return new TextOutline(rings, missing);
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
            warnings.Add($"В шрифте «{font.DisplayName}» нет символов: {string.Join(" ", outline.MissingCharacters)}");
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

    /// <summary>
    /// Polyline of one glyph contour. Consecutive off-curve points have an implied on-curve point
    /// half-way between them; each quadratic piece is split so that its chord error stays within the tolerance.
    /// </summary>
    internal static List<Vec2> FlattenGlyphContour(List<GlyphPoint> points, Func<GlyphPoint, Vec2> map, double tolerance)
    {
        var sequence = new List<(Vec2 P, bool On)>(points.Count + 2);
        var first = points.FindIndex(p => p.OnCurve);
        if (first < 0)
        {
            var a = map(points[^1]);
            var b = map(points[0]);
            sequence.Add(((a + b) / 2, true));
            sequence.AddRange(points.Select(p => (map(p), false)));
        }
        else
        {
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[(first + i) % points.Count];
                sequence.Add((map(p), p.OnCurve));
            }
        }

        sequence.Add(sequence[0]);

        var result = new List<Vec2> { sequence[0].P };
        var current = sequence[0].P;
        var index = 1;
        while (index < sequence.Count)
        {
            if (sequence[index].On)
            {
                current = sequence[index].P;
                result.Add(current);
                index++;
                continue;
            }

            var control = sequence[index].P;
            var next = sequence[index + 1];
            Vec2 end;
            if (next.On)
            {
                end = next.P;
                index += 2;
            }
            else
            {
                end = (control + next.P) / 2;
                index++;
            }

            var curvature = (current - 2 * control + end).Length;
            var count = Math.Clamp((int)Math.Ceiling(Math.Sqrt(curvature / (4 * Math.Max(tolerance, 1e-4)))), 1, MaxCurveSegments);
            for (var k = 1; k <= count; k++)
            {
                var t = (double)k / count;
                var u = 1 - t;
                result.Add(u * u * current + 2 * u * t * control + t * t * end);
            }

            current = end;
        }

        // Drop the closing point and repeated points.
        var clean = new List<Vec2>(result.Count);
        foreach (var p in result)
        {
            if (clean.Count == 0 || !clean[^1].IsNear(p, 1e-9))
            {
                clean.Add(p);
            }
        }

        if (clean.Count > 1 && clean[^1].IsNear(clean[0], 1e-9))
        {
            clean.RemoveAt(clean.Count - 1);
        }

        return clean;
    }

    /// <summary>Short description of a text for lists.</summary>
    public static string Describe(TextItem item)
    {
        var text = item.Text.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
        if (text.Length > 24)
        {
            text = text[..24] + "…";
        }

        return string.Create(CultureInfo.CurrentCulture, $"«{text}» — {item.FontName}, {item.HeightMm:0.##} мм");
    }
}
