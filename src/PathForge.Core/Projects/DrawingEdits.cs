using PathForge.Core.Geometry;
using PathForge.Core.Machining;

namespace PathForge.Core.Projects;

/// <summary>
/// Editing of selected contours: move, turn, scale, delete, and rectangular or circular arrays. Operations follow:
/// moved contours stay in them, copies are added to every operation that has the original, deleted ones are
/// removed. Text letters moved as a whole move their text; turned, scaled or copied letters become plain contours.
/// </summary>
public static class DrawingEdits
{
    public static Bounds2 Bounds(CamProject project, IReadOnlyCollection<int> ids) =>
        project.Contours.Where(c => ids.Contains(c.Id)).Aggregate(Bounds2.Empty, (b, c) => b.Union(c.GetBounds()));

    public static void Move(CamProject project, IReadOnlyCollection<int> ids, double dx, double dy) =>
        Transform(project, ids, Affine2.Translation(dx, dy));

    /// <summary>Turns the contours by <paramref name="degrees"/> (counter-clockwise) around <paramref name="center"/>.</summary>
    public static void Rotate(CamProject project, IReadOnlyCollection<int> ids, double degrees, Vec2 center) =>
        Transform(project, ids, About(Affine2.Rotation(degrees * Math.PI / 180), center));

    /// <summary>Scales the contours uniformly by <paramref name="factor"/> around <paramref name="center"/>.</summary>
    public static void Scale(CamProject project, IReadOnlyCollection<int> ids, double factor, Vec2 center)
    {
        if (factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        Transform(project, ids, About(Affine2.Scaling(factor, factor), center));
    }

    /// <summary>Applies a similarity transform (move, turn, uniform scale, mirror) to the contours.</summary>
    public static void Transform(CamProject project, IReadOnlyCollection<int> ids, Affine2 transform)
    {
        var selected = project.Contours.Where(c => ids.Contains(c.Id)).ToList();
        var translation = Math.Abs(transform.M11 - 1) < 1e-12 && Math.Abs(transform.M22 - 1) < 1e-12 &&
                          Math.Abs(transform.M12) < 1e-12 && Math.Abs(transform.M21) < 1e-12;
        foreach (var contour in selected)
        {
            contour.Segments = contour.Transformed(transform).Segments;
        }

        foreach (var textId in selected.Select(c => c.TextId).OfType<string>().Distinct().ToList())
        {
            var letters = project.Contours.Where(c => c.TextId == textId).ToList();
            var whole = letters.All(c => ids.Contains(c.Id));
            if (translation && whole && project.Texts.FirstOrDefault(t => t.Id == textId) is { } text)
            {
                // The text keeps its letters: it moves with them.
                text.X += transform.Tx;
                text.Y += transform.Ty;
                continue;
            }

            Detach(project, textId);
        }
    }

    /// <summary>Removes the contours (and from all operations); returns how many were removed.</summary>
    public static int Delete(CamProject project, IReadOnlyCollection<int> ids)
    {
        var removed = project.Contours.Where(c => ids.Contains(c.Id)).ToList();
        foreach (var textId in removed.Select(c => c.TextId).OfType<string>().Distinct().ToList())
        {
            if (project.Contours.Where(c => c.TextId == textId).All(c => ids.Contains(c.Id)))
            {
                project.Texts.RemoveAll(t => t.Id == textId);
            }
            else
            {
                Detach(project, textId);
            }
        }

        project.Contours.RemoveAll(c => ids.Contains(c.Id));
        foreach (var operation in project.Operations)
        {
            operation.ContourIds.RemoveAll(ids.Contains);
            BoardContourIds(operation)?.RemoveAll(ids.Contains);
        }

        return removed.Count;
    }

    /// <summary>
    /// Copies in a grid of <paramref name="columns"/> × <paramref name="rows"/> (the original is the first cell),
    /// <paramref name="stepX"/> and <paramref name="stepY"/> apart. Returns the ids of the copies.
    /// </summary>
    public static List<int> RectangularArray(CamProject project, IReadOnlyCollection<int> ids, int columns, int rows, double stepX, double stepY)
    {
        var transforms = new List<Affine2>();
        for (var row = 0; row < Math.Max(1, rows); row++)
        {
            for (var column = 0; column < Math.Max(1, columns); column++)
            {
                if (row > 0 || column > 0)
                {
                    transforms.Add(Affine2.Translation(column * stepX, row * stepY));
                }
            }
        }

        return Copy(project, ids, transforms);
    }

    /// <summary>
    /// <paramref name="count"/> items (the original included) around <paramref name="center"/>, spread evenly over
    /// <paramref name="sweepDegrees"/> (360 = a full circle, the last item does not land on the first). Copies are
    /// turned with the circle when <paramref name="rotateItems"/>, otherwise only moved. Returns the ids of the copies.
    /// </summary>
    public static List<int> CircularArray(CamProject project, IReadOnlyCollection<int> ids, int count, double sweepDegrees, Vec2 center, bool rotateItems = true)
    {
        count = Math.Max(1, count);
        var full = Math.Abs(Math.Abs(sweepDegrees) - 360) < 1e-9;
        var step = count == 1 ? 0 : sweepDegrees / (full ? count : count - 1);
        var anchor = Bounds(project, ids).Center;
        var transforms = new List<Affine2>();
        for (var i = 1; i < count; i++)
        {
            var turn = About(Affine2.Rotation(i * step * Math.PI / 180), center);
            if (!rotateItems)
            {
                var moved = turn.Apply(anchor);
                turn = Affine2.Translation(moved.X - anchor.X, moved.Y - anchor.Y);
            }

            transforms.Add(turn);
        }

        return Copy(project, ids, transforms);
    }

    /// <summary>Adds the copy ids to every operation that has the original (and to the board outlines of PCB operations).</summary>
    internal static void AddCopiesToOperations(CamProject project, Dictionary<int, List<int>> copyIds)
    {
        foreach (var operation in project.Operations)
        {
            operation.ContourIds.AddRange(operation.ContourIds.Where(copyIds.ContainsKey).SelectMany(id => copyIds[id]).ToList());
            if (BoardContourIds(operation) is { } board)
            {
                board.AddRange(board.Where(copyIds.ContainsKey).SelectMany(id => copyIds[id]).ToList());
            }
        }
    }

    private static List<int> Copy(CamProject project, IReadOnlyCollection<int> ids, List<Affine2> transforms)
    {
        var originals = project.Contours.Where(c => ids.Contains(c.Id)).ToList();
        var nextId = project.NextContourId();
        var copyIds = new Dictionary<int, List<int>>();
        var added = new List<int>();
        foreach (var transform in transforms)
        {
            foreach (var original in originals)
            {
                // A copy of a text's letters is plain geometry: only the original text stays editable.
                var copy = new Contour(nextId++, original.Segments.Select(s => s.Transform(transform)), original.Layer);
                project.Contours.Add(copy);
                added.Add(copy.Id);
                if (!copyIds.TryGetValue(original.Id, out var list))
                {
                    copyIds[original.Id] = list = new List<int>();
                }

                list.Add(copy.Id);
            }
        }

        AddCopiesToOperations(project, copyIds);
        return added;
    }

    /// <summary>The text's letters become plain contours and the text is removed from the text list.</summary>
    private static void Detach(CamProject project, string textId)
    {
        foreach (var letter in project.Contours.Where(c => c.TextId == textId))
        {
            letter.TextId = null;
        }

        project.Texts.RemoveAll(t => t.Id == textId);
    }

    private static List<int>? BoardContourIds(Operation operation) => operation switch
    {
        LaserPcbOperation pcb => pcb.BoardContourIds,
        CopperClearingOperation clearing => clearing.BoardContourIds,
        _ => null,
    };

    private static Affine2 About(Affine2 transform, Vec2 center) =>
        Affine2.Then(Affine2.Then(Affine2.Translation(-center.X, -center.Y), transform), Affine2.Translation(center.X, center.Y));
}
