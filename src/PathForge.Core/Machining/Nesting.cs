using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Projects;

namespace PathForge.Core.Machining;

/// <summary>Sheet and rules for laying out parts.</summary>
public sealed class NestingSettings
{
    public double SheetWidth { get; set; } = 300;

    public double SheetHeight { get; set; } = 180;

    /// <summary>Free border along the sheet edges (mm).</summary>
    public double Margin { get; set; } = 5;

    /// <summary>Gap between parts (mm): at least the cutter diameter, better a little more.</summary>
    public double Spacing { get; set; } = 5;

    /// <summary>Parts may be turned by 90°, 180° and 270° to fit better.</summary>
    public bool AllowRotation { get; set; } = true;

    /// <summary>How many copies of every part to lay out.</summary>
    public int Copies { get; set; } = 1;
}

/// <summary>Result of a layout: what was placed and how well the sheet is used.</summary>
public sealed record NestingResult(int Placed, int NotPlaced, double UsedPercent, List<string> Warnings);

/// <summary>
/// Lays out parts on a sheet. A part is a closed outer contour with everything inside it (holes, engravings,
/// text). Parts are placed largest first, each in the lowest and then leftmost position where its real outline
/// (grown by half the spacing) does not touch the others, and then slid down and left into the gaps: the parts
/// interlock where their shapes allow, not only as rectangles.
/// </summary>
public static class Nesting
{
    private const double Tolerance = 0.05;

    /// <summary>
    /// Arranges the parts made of <paramref name="contourIds"/> (all contours when empty) on the sheet whose
    /// lower-left corner is the drawing origin. Copies get new contours, added to the operations of the originals.
    /// Parts that do not fit are put to the right of the sheet.
    /// </summary>
    public static NestingResult Arrange(CamProject project, IReadOnlyCollection<int> contourIds, NestingSettings settings)
    {
        var warnings = new List<string>();
        var chosen = contourIds.Count == 0 ? project.Contours : project.Contours.Where(c => contourIds.Contains(c.Id)).ToList();
        var parts = FindParts(chosen, warnings);
        if (parts.Count == 0)
        {
            warnings.Add(Loc.T("Нет замкнутых контуров — раскладывать нечего.", "No closed contours — nothing to lay out."));
            return new NestingResult(0, 0, 0, warnings);
        }

        // Copies of every part.
        var copies = Math.Clamp(settings.Copies, 1, 500);
        var all = new List<Part>();
        foreach (var part in parts)
        {
            all.Add(part);
            for (var k = 1; k < copies; k++)
            {
                all.Add(part with { Copy = k });
            }
        }

        var texts = project.Texts.ToDictionary(t => t.Id);
        var rotatable = settings.AllowRotation;
        var placed = new List<(Vec2[] Shape, Bounds2 Box)>();
        var spacing = Math.Max(0, settings.Spacing);
        var area = new Bounds2(settings.Margin, settings.Margin, settings.SheetWidth - settings.Margin, settings.SheetHeight - settings.Margin);
        var placedCount = 0;
        var usedArea = 0.0;
        var parkX = settings.SheetWidth + 10 + spacing;
        var parkY = 0.0;
        var nextId = project.NextContourId();
        var newContours = new List<Contour>();
        var copyIds = new Dictionary<int, List<int>>();

        foreach (var part in all.OrderByDescending(p => p.Area))
        {
            // Text items cannot be turned: parts with text keep their orientation.
            var angles = rotatable && part.Contours.All(c => c.TextId is null) ? new[] { 0, 90, 180, 270 } : new[] { 0 };
            // The turns are tried in parallel; the lowest, then leftmost place wins (ties: the smaller turn).
            var options = new (Affine2 Transform, Vec2[] Shape, Bounds2 Box)?[angles.Length];
            Parallel.For(0, angles.Length, k =>
            {
                var turn = Affine2.Rotation(angles[k] * Math.PI / 180);
                var outline = part.Outline.Select(turn.Apply).ToArray();
                var bounds = Bounds2.Of(outline);
                if (bounds.Width > area.Width + 1e-9 || bounds.Height > area.Height + 1e-9 ||
                    FindPlace(outline, bounds, area, placed, spacing) is not { } offset)
                {
                    return;
                }

                var box = new Bounds2(offset.X + bounds.MinX, offset.Y + bounds.MinY, offset.X + bounds.MaxX, offset.Y + bounds.MaxY);
                options[k] = (Affine2.Then(turn, Affine2.Translation(offset.X, offset.Y)), outline.Select(p => p + offset).ToArray(), box);
            });

            (Affine2 Transform, Vec2[] Shape, Bounds2 Box)? best = null;
            foreach (var option in options)
            {
                if (option is { } candidate && (best is null || candidate.Box.MinY < best.Value.Box.MinY - Tolerance ||
                    (Math.Abs(candidate.Box.MinY - best.Value.Box.MinY) <= Tolerance && candidate.Box.MinX < best.Value.Box.MinX)))
                {
                    best = candidate;
                }
            }

            Affine2 transform;
            if (best is { } chosenPlace)
            {
                placed.Add((chosenPlace.Shape, chosenPlace.Box));
                transform = chosenPlace.Transform;
                placedCount++;
                usedArea += Math.Abs(Polyline.SignedArea(part.Outline));
            }
            else
            {
                // Does not fit: parked to the right of the sheet, one above the other.
                transform = Affine2.Translation(parkX - part.Bounds.MinX, parkY - part.Bounds.MinY);
                parkY += part.Bounds.Height + spacing;
            }

            if (part.Copy == 0)
            {
                foreach (var contour in part.Contours)
                {
                    var moved = contour.Transformed(transform);
                    contour.Segments = moved.Segments;
                }

                // Texts follow their letters (parts with text are only moved, never turned).
                foreach (var textId in part.Contours.Select(c => c.TextId).OfType<string>().Distinct())
                {
                    if (texts.TryGetValue(textId, out var text))
                    {
                        var at = transform.Apply(new Vec2(text.X, text.Y));
                        text.X = at.X;
                        text.Y = at.Y;
                    }
                }
            }
            else
            {
                foreach (var contour in part.Contours)
                {
                    // A copy of a text's letters is ordinary geometry: only the original text stays editable.
                    var copy = new Contour(nextId++, contour.Transformed(transform).Segments, contour.Layer);
                    newContours.Add(copy);
                    if (!copyIds.TryGetValue(contour.Id, out var ids))
                    {
                        copyIds[contour.Id] = ids = new List<int>();
                    }

                    ids.Add(copy.Id);
                }
            }
        }

        project.Contours.AddRange(newContours);
        foreach (var operation in project.Operations)
        {
            var extra = operation.ContourIds.Where(copyIds.ContainsKey).SelectMany(id => copyIds[id]).ToList();
            operation.ContourIds.AddRange(extra);
            if (operation is LaserPcbOperation pcb)
            {
                pcb.BoardContourIds.AddRange(pcb.BoardContourIds.Where(copyIds.ContainsKey).SelectMany(id => copyIds[id]).ToList());
            }
        }

        var sheet = Math.Max(1e-9, area.Width * area.Height);
        var notPlaced = all.Count - placedCount;
        if (notPlaced > 0)
        {
            warnings.Add(Loc.T(
                $"Не поместилось деталей: {notPlaced} — они справа от листа. Уменьшите отступы или число копий, или возьмите лист больше.",
                $"Parts that did not fit: {notPlaced} — they are to the right of the sheet. Reduce the gaps or the copies, or use a larger sheet."));
        }

        return new NestingResult(placedCount, notPlaced, 100 * usedArea / sheet, warnings);
    }

    /// <summary>Outer closed contours with the contours that lie inside them.</summary>
    internal static List<Part> FindParts(IReadOnlyList<Contour> contours, List<string> warnings)
    {
        var closed = contours.Where(c => c.IsClosed)
            .Select(c => (Contour: c, Ring: c.Flatten(Tolerance)))
            .Where(c => c.Ring.Count >= 3)
            .ToList();
        // A contour is an outer border when no other closed contour contains it.
        var outers = closed.Where(c => !closed.Any(o => !ReferenceEquals(o.Contour, c.Contour) &&
                                                      Math.Abs(Polyline.SignedArea(o.Ring)) > Math.Abs(Polyline.SignedArea(c.Ring)) &&
                                                      Polyline.Contains(o.Ring, c.Ring[0])))
            .ToList();
        var parts = outers.Select(o => new Part(new List<Contour> { o.Contour }, o.Ring)).ToList();
        var loose = 0;
        foreach (var contour in contours)
        {
            if (outers.Any(o => ReferenceEquals(o.Contour, contour)))
            {
                continue;
            }

            var points = contour.Flatten(Tolerance);
            var owner = points.Count == 0 ? null : parts
                .Where(p => Polyline.Contains(p.Outline, points[0]))
                .OrderBy(p => Math.Abs(Polyline.SignedArea(p.Outline)))
                .FirstOrDefault();
            if (owner is null)
            {
                loose++;
                continue;
            }

            owner.Contours.Add(contour);
        }

        if (loose > 0)
        {
            warnings.Add(Loc.T(
                $"Контуров вне деталей: {loose} — они остались на месте (раскладываются только замкнутые контуры и то, что внутри них).",
                $"Contours outside any part: {loose} — they stayed where they were (only closed contours and what is inside them are laid out)."));
        }

        return parts;
    }

    /// <summary>
    /// Offset that puts the part (shape <paramref name="grown"/>, bounds <paramref name="bounds"/> at the origin) into
    /// the area without touching the placed parts: the lowest-leftmost of the candidate corners, slid down and left.
    /// </summary>
    private static Vec2? FindPlace(Vec2[] outline, Bounds2 bounds, Bounds2 area, List<(Vec2[] Shape, Bounds2 Box)> placed, double spacing)
    {
        var xs = new List<double> { area.MinX };
        var ys = new List<double> { area.MinY };
        foreach (var (_, box) in placed)
        {
            xs.Add(box.MaxX + spacing);
            ys.Add(box.MaxY + spacing);
            xs.Add(box.MinX);
            ys.Add(box.MinY);
        }

        // Besides the corners next to the placed parts, a coarse grid: a part may fit into the free side of
        // another part's shape (a triangle next to a triangle) where no rectangle corner lies.
        var step = Math.Max(2 * Math.Max(spacing, 1), Math.Max(area.Width, area.Height) / 40);
        for (var x = area.MinX + step; x < area.MaxX; x += step)
        {
            xs.Add(x);
        }

        for (var y = area.MinY + step; y < area.MaxY; y += step)
        {
            ys.Add(y);
        }

        var candidates = ys.Distinct().OrderBy(y => y)
            .SelectMany(y => xs.Distinct().OrderBy(x => x).Select(x => new Vec2(x - bounds.MinX, y - bounds.MinY)));
        foreach (var offset in candidates)
        {
            if (Fits(offset))
            {
                return Slide(offset);
            }
        }

        return null;

        bool Fits(Vec2 offset)
        {
            var box = new Bounds2(offset.X + bounds.MinX, offset.Y + bounds.MinY, offset.X + bounds.MaxX, offset.Y + bounds.MaxY);
            if (box.MinX < area.MinX - 1e-9 || box.MinY < area.MinY - 1e-9 || box.MaxX > area.MaxX + 1e-9 || box.MaxY > area.MaxY + 1e-9)
            {
                return false;
            }

            foreach (var (shape, other) in placed)
            {
                if (TooClose(outline, offset, box, shape, other, spacing))
                {
                    return false;
                }
            }

            return true;
        }

        // Gravity: down and to the left while there is room, in shrinking steps.
        Vec2 Slide(Vec2 offset)
        {
            for (var step = Math.Max(2, spacing); step >= 0.25; step /= 2)
            {
                var moved = true;
                for (var guard = 0; moved && guard < 2000; guard++)
                {
                    moved = false;
                    if (Fits(offset - new Vec2(0, step)))
                    {
                        offset -= new Vec2(0, step);
                        moved = true;
                    }
                    else if (Fits(offset - new Vec2(step, 0)))
                    {
                        offset -= new Vec2(step, 0);
                        moved = true;
                    }
                }
            }

            return offset;
        }
    }

    /// <summary>
    /// The outlines <paramref name="a"/> (moved by <paramref name="offset"/>) and <paramref name="b"/> are closer than
    /// <paramref name="gap"/>: their edges cross, one lies inside the other, or an edge comes nearer than the gap.
    /// </summary>
    internal static bool TooClose(Vec2[] a, Vec2 offset, Bounds2 boxA, Vec2[] b, Bounds2 boxB, double gap)
    {
        const double Eps = 1e-9;
        var reach = gap - 1e-6;
        if (boxA.MaxX + reach <= boxB.MinX || boxB.MaxX + reach <= boxA.MinX || boxA.MaxY + reach <= boxB.MinY || boxB.MaxY + reach <= boxA.MinY)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            var p1 = a[i] + offset;
            var p2 = a[(i + 1) % a.Length] + offset;
            var minX = Math.Min(p1.X, p2.X) - reach;
            var maxX = Math.Max(p1.X, p2.X) + reach;
            var minY = Math.Min(p1.Y, p2.Y) - reach;
            var maxY = Math.Max(p1.Y, p2.Y) + reach;
            if (maxX < boxB.MinX || minX > boxB.MaxX || maxY < boxB.MinY || minY > boxB.MaxY)
            {
                continue;
            }

            for (var j = 0; j < b.Length; j++)
            {
                var q1 = b[j];
                var q2 = b[(j + 1) % b.Length];
                if (Math.Max(q1.X, q2.X) < minX || Math.Min(q1.X, q2.X) > maxX || Math.Max(q1.Y, q2.Y) < minY || Math.Min(q1.Y, q2.Y) > maxY)
                {
                    continue;
                }

                // Crossing edges.
                var d1 = Cross(q1, q2, p1);
                var d2 = Cross(q1, q2, p2);
                var d3 = Cross(p1, p2, q1);
                var d4 = Cross(p1, p2, q2);
                if (((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps)) && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps)))
                {
                    return true;
                }

                // Otherwise the nearest points of two segments include an end of one of them.
                if (Polyline.DistanceToSegment(p1, q1, q2) < reach || Polyline.DistanceToSegment(p2, q1, q2) < reach ||
                    Polyline.DistanceToSegment(q1, p1, p2) < reach || Polyline.DistanceToSegment(q2, p1, p2) < reach)
                {
                    return true;
                }
            }
        }

        // Far from every edge: still too close if one lies inside the other.
        return Polyline.Contains(b, a[0] + offset) || Polyline.Contains(a.Select(p => p + offset).ToList(), b[0]);
    }

    private static double Cross(Vec2 a, Vec2 b, Vec2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static Bounds2 BoundsOf(List<List<Vec2>> rings) => rings.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));

    /// <summary>A part: its outer border and all its contours; <paramref name="Copy"/> 0 is the original.</summary>
    internal sealed record Part(List<Contour> Contours, List<Vec2> Outline, int Copy = 0)
    {
        public Bounds2 Bounds { get; } = Bounds2.Of(Outline);

        public double Area => Bounds.Width * Bounds.Height;
    }
}
