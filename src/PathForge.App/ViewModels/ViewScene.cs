using PathForge.Core.Geometry;
using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

public enum ContourState
{
    Normal,

    /// <summary>Used by the operation selected in the list.</summary>
    InOperation,

    /// <summary>Selected with the mouse.</summary>
    Selected,
}

public sealed record SceneContour(int Id, IReadOnlyList<Vec2> Points, bool Closed, ContourState State);

public sealed record SceneToolpath(IReadOnlyList<ToolMove> Moves, Vec3 Start, bool Highlighted);

/// <summary>Immutable snapshot of what the viewport draws.</summary>
public sealed record ViewScene(IReadOnlyList<SceneContour> Contours, IReadOnlyList<SceneToolpath> Toolpaths, Bounds2 Bounds)
{
    public static readonly ViewScene Empty = new(Array.Empty<SceneContour>(), Array.Empty<SceneToolpath>(), Bounds2.Empty);
}

/// <summary>Mouse click on the drawing: the contour hit (or null) and whether Ctrl was held.</summary>
public sealed record ContourClick(int? ContourId, bool Additive);

/// <summary>Contours inside the rectangle dragged with Shift; added to the selection when Ctrl was held too.</summary>
public sealed record ContourBoxSelection(IReadOnlyList<int> ContourIds, bool Additive);
