using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Excess copper of a PCB removed with a larger end mill, leaving a band around the copper to the engraver.</summary>
public static partial class ToolpathGenerator
{
    /// <summary>Uncut copper smaller than this (mm²) is not reported.</summary>
    private const double LeftCopperTolerance = 0.5;

    private static void GenerateCopperClearing(CopperClearingOperation operation, Dictionary<int, Contour> contours, OperationContext context)
    {
        var board = operation.BoardContourIds.ToHashSet();
        var copperContours = operation.ContourIds.Where(id => contours.ContainsKey(id) && !board.Contains(id))
            .Select(id => contours[id]).Where(c => c.IsClosed).ToList();
        if (copperContours.Count == 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: выберите контуры меди (замкнутые).", $"{context.Label}: select copper contours (closed)."));
            return;
        }

        var tool = context.Tool;
        var stepOver = tool.StepOver;
        if (stepOver <= 0 || stepOver > tool.Diameter)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: перекрытие должно быть от 1 до 100 % диаметра.", $"{context.Label}: the step-over must be 1 to 100 % of the diameter."));
            return;
        }

        var copper = ClipperBridge.EvenOddRegion(copperContours.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
        var area = PcbClearingArea(operation.BoardContourIds, operation.Margin, contours, ClipperBridge.FromPaths(copper), context.Warnings, context.Label);
        var region = ClipperBridge.Difference(area, ClipperBridge.Offset(copper, Math.Max(0, operation.KeepDistance)));
        var levels = InwardRings(region, tool.Radius, stepOver, operation.Direction);
        if (levels.Count == 0)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: фреза Ø{tool.Diameter:0.##} нигде не помещается между медью — удалять нечего.",
                $"{context.Label}: the Ø{tool.Diameter:0.##} mill fits nowhere between the copper — nothing to remove."));
            return;
        }

        // What the mill cannot reach (narrower than it): floating copper left between the isolated tracks.
        var reached = ClipperBridge.Offset(ClipperBridge.Offset(region, -tool.Radius), tool.Radius);
        var left = ClipperBridge.Area(ClipperBridge.Difference(region, reached));
        if (left > LeftCopperTolerance)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: в узких местах останется около {left:0.#} мм² меди — фреза Ø{tool.Diameter:0.##} туда не проходит. " +
                "Эти островки отделены от дорожек изоляцией; чтобы убрать и их, возьмите фрезу меньше или добавьте проходов изоляции.",
                $"{context.Label}: about {left:0.#} mm² of copper stays in narrow places — the Ø{tool.Diameter:0.##} mill does not get there. " +
                "These islands are separated from the tracks by the isolation; to remove them too, use a smaller mill or more isolation passes."));
        }

        CutRingLevels(levels, stepOver, context);
    }

    /// <summary>
    /// Width of the band an isolation operation cuts around the copper (mm): the engraver's cutting width at the
    /// depth plus a step for every further pass.
    /// </summary>
    public static double IsolationBandWidth(IsolationOperation operation, Tool tool)
    {
        var width = tool.Kind == ToolKind.VBit ? tool.CuttingDiameter(operation.Depth) : tool.Diameter;
        var step = Math.Max(0.01, width * (1 - Math.Clamp(operation.OverlapPercent, 0, 90) / 100));
        return width + (Math.Max(1, operation.Passes) - 1) * step;
    }
}
