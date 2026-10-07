using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Parameters of a solder paste stencil cut by laser from film.</summary>
public sealed class PasteStencilSettings
{
    /// <summary>
    /// Every window is made smaller by this much on each side (mm): less paste, no bridges between fine-pitch pads.
    /// </summary>
    public double Reduction { get; set; } = 0.05;

    /// <summary>Margin of the film sheet around the windows (mm); 0 = only the windows are cut.</summary>
    public double FrameMargin { get; set; } = 10;
}

/// <summary>Window and frame contours of a paste stencil and the laser operation that cuts them.</summary>
public sealed record PasteStencil(List<Contour> Contours, LaserVectorOperation Operation, List<string> Warnings)
{
    /// <summary>Layer of the stencil's contours.</summary>
    public const string Layer = "Paste stencil";

    /// <summary>Windows smaller than this (mm, either side) after the reduction are left out.</summary>
    private const double MinWindow = 0.1;

    /// <param name="paste">Closed contours of the paste layer (the pad areas that get paste).</param>
    /// <param name="kerf">Width the beam burns away (the laser spot): the windows come out to size.</param>
    /// <param name="firstContourId">Id for the first new contour; the others follow.</param>
    public static PasteStencil Build(IEnumerable<Contour> paste, PasteStencilSettings settings, string toolId, double kerf, int firstContourId)
    {
        var warnings = new List<string>();
        var closed = paste.Where(c => c.IsClosed).ToList();
        var region = ClipperBridge.EvenOddRegion(closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(0.01)));
        var reduction = Math.Max(0, settings.Reduction);
        var windows = ClipperBridge.FromPaths(reduction > 0 ? ClipperBridge.Offset(region, -reduction) : region)
            .Where(r => r.Count >= 3)
            .ToList();
        var total = ClipperBridge.FromPaths(region).Count(Polyline.IsCounterClockwise);
        windows = windows.Where(r => Bounds2.Of(r).Width >= MinWindow && Bounds2.Of(r).Height >= MinWindow).ToList();
        var lost = total - windows.Count(Polyline.IsCounterClockwise);
        if (closed.Count == 0)
        {
            warnings.Add(Loc.T("Трафарет: выберите замкнутые контуры слоя пасты.", "Stencil: select closed contours of the paste layer."));
        }
        else if (lost > 0)
        {
            warnings.Add(Loc.T(
                $"Трафарет: {lost} окон после уменьшения на {reduction:0.###} мм стали меньше {MinWindow} мм и пропущены — уменьшите «Уменьшение окон».",
                $"Stencil: {lost} windows became smaller than {MinWindow} mm after shrinking by {reduction:0.###} mm and were left out — reduce “Window reduction”."));
        }

        var nextId = firstContourId;
        var contours = windows.Select(r => Polygon(nextId++, r)).ToList();
        var frame = settings.FrameMargin > 0 && contours.Count > 0;
        if (frame)
        {
            var b = contours.Aggregate(Bounds2.Empty, (bounds, c) => bounds.Union(c.GetBounds()));
            var m = settings.FrameMargin;
            contours.Add(Polygon(nextId, new List<Vec2>
            {
                new(b.MinX - m, b.MinY - m), new(b.MaxX + m, b.MinY - m), new(b.MaxX + m, b.MaxY + m), new(b.MinX - m, b.MaxY + m),
            }));
        }

        var operation = new LaserVectorOperation
        {
            Name = Loc.T("Трафарет пасты", "Paste stencil"),
            Mode = LaserVectorMode.Line,
            PowerPercent = 100,
            Speed = 600,
            Passes = 2,
            AirAssist = true,
            // The windows are holes in the sheet when there is a frame, the only (outer) contours without one.
            Kerf = frame ? KerfCompensation.Parts : KerfCompensation.Openings,
            KerfWidth = Math.Max(0, kerf),
            ToolId = toolId,
            ContourIds = contours.Select(c => c.Id).ToList(),
        };
        return new PasteStencil(contours, operation, warnings);
    }

    private static Contour Polygon(int id, List<Vec2> ring) =>
        new(id, ring.Select((p, k) => (Segment)new LineSegment(p, ring[(k + 1) % ring.Count])).ToList(), Layer);
}
