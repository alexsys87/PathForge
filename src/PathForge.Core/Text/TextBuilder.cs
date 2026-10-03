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
