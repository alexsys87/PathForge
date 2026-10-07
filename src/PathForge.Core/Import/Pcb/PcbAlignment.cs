using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Import.Pcb;

/// <summary>
/// Alignment (registration) holes for a double-sided board milled on a router: two holes outside the board on its
/// horizontal centre line, at the same distance left and right. They are drilled through the board into the spoil
/// board; pins put into them hold the board when it is turned over left to right. Because the holes are symmetric,
/// the drawing mirrored in X ("Mirror X (bottom layer)") has the same bounds and the same work zero: the second side
/// lines up with the first without moving the zero.
/// </summary>
public static class PcbAlignment
{
    /// <summary>Layer of the alignment holes.</summary>
    public static string Layer => Loc.T("Базовые отверстия", "Alignment holes");

    /// <summary>The two holes outside <paramref name="board"/> with <paramref name="margin"/> between the board and the hole edge.</summary>
    public static List<Contour> Holes(Bounds2 board, double diameter, double margin, int firstId)
    {
        var radius = Math.Max(0.1, diameter) / 2;
        var y = (board.MinY + board.MaxY) / 2;
        var offset = Math.Max(0, margin) + radius;
        return new List<Contour>
        {
            new(firstId, new Segment[] { new ArcSegment(new Vec2(board.MinX - offset, y), radius, 0, 2 * Math.PI) }, Layer),
            new(firstId + 1, new Segment[] { new ArcSegment(new Vec2(board.MaxX + offset, y), radius, 0, 2 * Math.PI) }, Layer),
        };
    }

    /// <summary>
    /// Board area for the holes: the board outline contours when there are any (by the layer name), otherwise
    /// everything except earlier alignment holes.
    /// </summary>
    public static Bounds2 BoardBounds(IEnumerable<Contour> contours)
    {
        var drawing = contours.Where(c => c.Layer != Layer).ToList();
        var outline = drawing.Where(c => PcbFileDetector.IsOutlineFileName(c.Layer)).ToList();
        var source = outline.Count > 0 ? outline : drawing;
        return source.Aggregate(Bounds2.Empty, (bounds, c) => bounds.Union(c.GetBounds()));
    }
}
