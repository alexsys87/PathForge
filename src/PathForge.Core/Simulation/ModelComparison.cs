using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Simulation;

/// <summary>How far the simulated stock is from the model: per cell and in summary.</summary>
/// <param name="Deviation">Simulated minus model height for every cell of the field (mm); NaN where there is no model.</param>
/// <param name="MaxGouge">Deepest cut below the model (mm, ≥ 0).</param>
/// <param name="MaxLeftover">Thickest material left above the model (mm, ≥ 0).</param>
/// <param name="WithinPercent">Share of the model area within the tolerance.</param>
/// <param name="GougePercent">Share of the model area cut too deep.</param>
/// <param name="LeftoverPercent">Share of the model area with material left.</param>
public sealed record ComparisonResult(float[] Deviation, double MaxGouge, double MaxLeftover, double WithinPercent, double GougePercent, double LeftoverPercent);

/// <summary>
/// Compares the simulated stock with the 3D model of the relief operations (picture or STL): where material is
/// left (the tool could not reach, or a too large step) and where the tool went too deep (gouges).
/// The model is evaluated once for the cells of the simulation field.
/// </summary>
public sealed class ModelComparison
{
    private readonly float[] _target;

    public ModelComparison(CamProject project, GenerationResult generation, HeightField field)
    {
        Field = field;
        _target = new float[field.Width * field.Height];
        Array.Fill(_target, float.NaN);
        var contours = project.Contours.ToDictionary(c => c.Id);
        var zShift = project.Stock.ZShift;
        foreach (var relief in project.Operations.OfType<ReliefOperation>().Where(o => o.Enabled))
        {
            if (ToolpathGenerator.ReliefSurface(relief) is not { } surface)
            {
                continue;
            }

            var mask = relief.LimitToContours
                ? ToolpathGenerator.BoundaryMask(relief.ContourIds.Where(contours.ContainsKey).Select(id => contours[id]).ToList(), surface)
                : null;
            for (var j = 0; j < field.Height; j++)
            {
                for (var i = 0; i < field.Width; i++)
                {
                    // Field cells are in program coordinates; the relief in drawing coordinates.
                    var p = field.CellCenter(i, j) + generation.Origin;
                    var c = (int)Math.Floor((p.X - surface.OriginX) / surface.CellSize);
                    var r = (int)Math.Floor((p.Y - surface.OriginY) / surface.CellSize);
                    if (c < 0 || r < 0 || c >= surface.Columns || r >= surface.Rows || (mask is not null && !mask[r * surface.Columns + c]))
                    {
                        continue;
                    }

                    var z = (float)(relief.StartZ + surface[c, r] + zShift);
                    var index = j * field.Width + i;
                    _target[index] = float.IsNaN(_target[index]) ? z : Math.Min(_target[index], z);
                }
            }
        }

        HasModel = _target.Any(t => !float.IsNaN(t));
    }

    public HeightField Field { get; }

    /// <summary>The project has a relief over the stock to compare with.</summary>
    public bool HasModel { get; }

    /// <param name="tolerance">Deviations up to this are counted as on the model (mm).</param>
    public ComparisonResult Compare(double tolerance)
    {
        var deviation = new float[_target.Length];
        double maxGouge = 0, maxLeftover = 0;
        int cells = 0, within = 0, gouged = 0, left = 0;
        var heights = Field.Heights;
        for (var k = 0; k < _target.Length; k++)
        {
            var target = _target[k];
            if (float.IsNaN(target))
            {
                deviation[k] = float.NaN;
                continue;
            }

            var d = heights[k] - target;
            deviation[k] = d;
            cells++;
            if (d < -tolerance)
            {
                gouged++;
                maxGouge = Math.Max(maxGouge, -d);
            }
            else if (d > tolerance)
            {
                left++;
                maxLeftover = Math.Max(maxLeftover, d);
            }
            else
            {
                within++;
            }
        }

        double Percent(int n) => cells == 0 ? 0 : 100.0 * n / cells;
        return new ComparisonResult(deviation, maxGouge, maxLeftover, Percent(within), Percent(gouged), Percent(left));
    }

    /// <summary>
    /// Deviation for a display copy made by <see cref="HeightField.Downsample"/>: each block keeps its strongest
    /// deviation (a gouge wins over left material of the same size), so small defects stay visible.
    /// </summary>
    public static float[] Downsample(HeightField field, float[] deviation, HeightField display)
    {
        if (ReferenceEquals(field, display) || display.Width == field.Width)
        {
            return deviation;
        }

        var factor = (int)Math.Round(display.CellSize / field.CellSize);
        var result = new float[display.Width * display.Height];
        Array.Fill(result, float.NaN);
        for (var j = 0; j < field.Height; j++)
        {
            for (var i = 0; i < field.Width; i++)
            {
                var d = deviation[j * field.Width + i];
                if (float.IsNaN(d))
                {
                    continue;
                }

                var target = j / factor * display.Width + i / factor;
                var current = result[target];
                if (float.IsNaN(current) || Math.Abs(d) > Math.Abs(current) || (Math.Abs(d) == Math.Abs(current) && d < 0))
                {
                    result[target] = d;
                }
            }
        }

        return result;
    }
}
