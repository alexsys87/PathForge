using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Adaptive pockets, rest machining and face milling.</summary>
public class AdaptivePocketTests
{
    private static Contour Polygon(int id, params (double X, double Y)[] points) =>
        new(id, points.Select((p, i) => (Segment)new LineSegment(new Vec2(p.X, p.Y), new Vec2(points[(i + 1) % points.Length].X, points[(i + 1) % points.Length].Y))));

    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) =>
        Polygon(id, (x0, y0), (x1, y0), (x1, y1), (x0, y1));

    private static (CamProject Project, Tool Tool, PocketOperation Pocket) PocketProject(double diameter, params Contour[] contours)
    {
        var project = new CamProject();
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 10;
        var tool = new Tool { Diameter = diameter, StepDown = 1, StepOverPercent = 40, FeedRate = 300, PlungeRate = 100 };
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        var pocket = new PocketOperation
        {
            ToolId = tool.Id, Depth = 1, Strategy = PocketStrategy.Adaptive, Entry = EntryMode.Ramp,
            ContourIds = contours.Select(c => c.Id).ToList(),
        };
        project.Operations.Add(pocket);
        return (project, tool, pocket);
    }

    /// <summary>
    /// Material removal on a grid at the first depth level: returns the largest engagement angle of the cutter edge
    /// (degrees of its circumference in material, averaged over 1 mm of flat moves) and the material left in the pocket.
    /// A straight cut taking a strip <c>a</c> wide engages acos(1 − a/R); a full-width slot 180°.
    /// </summary>
    private static (double MaxAngle, double LeftArea) Engagement(List<ToolMove> moves, double radius, Func<Vec2, bool> material,
        Bounds2 bounds, double level)
    {
        const double cell = 0.1;
        var nx = (int)Math.Ceiling(bounds.Width / cell) + 1;
        var ny = (int)Math.Ceiling(bounds.Height / cell) + 1;
        var grid = new bool[nx, ny];
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                grid[i, j] = material(new Vec2(bounds.MinX + (i + 0.5) * cell, bounds.MinY + (j + 0.5) * cell));
            }
        }

        int Remove(Vec2 c)
        {
            var removed = 0;
            var r = (int)Math.Ceiling(radius / cell);
            var ci = (int)Math.Floor((c.X - bounds.MinX) / cell);
            var cj = (int)Math.Floor((c.Y - bounds.MinY) / cell);
            for (var i = Math.Max(0, ci - r); i <= Math.Min(nx - 1, ci + r); i++)
            {
                for (var j = Math.Max(0, cj - r); j <= Math.Min(ny - 1, cj + r); j++)
                {
                    var p = new Vec2(bounds.MinX + (i + 0.5) * cell, bounds.MinY + (j + 0.5) * cell);
                    if (grid[i, j] && p.DistanceTo(c) <= radius)
                    {
                        grid[i, j] = false;
                        removed++;
                    }
                }
            }

            return removed;
        }

        // Points just outside the front half of the cutter edge that are still material.
        double Angle(Vec2 c, Vec2 direction)
        {
            var inMaterial = 0;
            for (var k = 0; k < 120; k++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * k / 120);
                var u = new Vec2(cos, sin);
                if (u.X * direction.X + u.Y * direction.Y < 0)
                {
                    continue;
                }

                var p = c + u * (radius + cell);
                var i = (int)Math.Floor((p.X - bounds.MinX) / cell);
                var j = (int)Math.Floor((p.Y - bounds.MinY) / cell);
                if (i >= 0 && j >= 0 && i < nx && j < ny && grid[i, j])
                {
                    inMaterial++;
                }
            }

            return inMaterial * 3.0;
        }

        var maxWidth = 0.0;
        // Removed area and travelled distance of the samples of the last millimetre.
        var window = new Queue<(double Removed, double Distance)>();
        var removedSum = 0.0;
        var distanceSum = 0.0;
        var position = moves[0].Target;
        foreach (var move in moves)
        {
            var target = move.Target;
            // Every feed move below the stock top cuts this layer (the entry helix too); only flat moves are measured.
            var cutting = move.Kind != MoveKind.Rapid && Math.Min(position.Z, target.Z) < -1e-6;
            var flat = Math.Abs(position.Z - level) < 1e-6 && Math.Abs(target.Z - level) < 1e-6;
            var length = position.XY.DistanceTo(target.XY);
            if (cutting)
            {
                var samples = Math.Max(1, (int)Math.Ceiling(length / cell));
                for (var s = 1; s <= samples; s++)
                {
                    var at = Vec2.Lerp(position.XY, target.XY, (double)s / samples);
                    var angle = Angle(at, (target.XY - position.XY) / Math.Max(1e-9, length));
                    Remove(at);
                    if (!flat || length < 1e-9)
                    {
                        window.Clear();
                        removedSum = 0;
                        distanceSum = 0;
                        continue;
                    }

                    window.Enqueue((angle * length / samples, length / samples));
                    removedSum += angle * length / samples;
                    distanceSum += length / samples;
                    while (window.Count > 1 && distanceSum - window.Peek().Distance >= 1)
                    {
                        var (r, d) = window.Dequeue();
                        removedSum -= r;
                        distanceSum -= d;
                    }

                    if (distanceSum >= 1 - 1e-9)
                    {
                        var w = removedSum / distanceSum;
                        if (w > maxWidth)
                        {
                            maxWidth = w;
                            MaxAt = (at, moves.IndexOf(move));
                        }
                    }
                }
            }
            else
            {
                window.Clear();
                removedSum = 0;
                distanceSum = 0;
            }

            position = target;
        }

        var left = 0;
        foreach (var m in grid)
        {
            left += m ? 1 : 0;
        }

        return (maxWidth, left * cell * cell);
    }

    internal static (Vec2 P, int Move) MaxAt;

    private static List<ToolMove> FirstLevel(CamProject project) =>
        ToolpathGenerator.Generate(project).Toolpaths.Single().Moves.TakeWhile(m => m.Target.Z >= -1 - 1e-9).ToList();

    [Fact]
    public void Adaptive_pocket_never_takes_more_than_about_the_step_even_in_corners()
    {
        var (project, tool, pocket) = PocketProject(6, Rectangle(1, 0, 0, 30, 24));
        var step = tool.Diameter * pocket.AdaptiveStepOverPercent / 100;

        var result = ToolpathGenerator.Generate(project);
        Assert.Empty(result.Warnings);
        var moves = FirstLevel(project);
        // Material just above the first level: the pocket minus corners the round tool cannot reach anyway.
        bool Inside(Vec2 p) => p.X > 0 && p.X < 30 && p.Y > 0 && p.Y < 24;
        var (maxWidth, left) = Engagement(moves, tool.Radius, Inside, new Bounds2(-4, -4, 34, 28), -1);
        var straight = Math.Acos(1 - step / tool.Radius) * 180 / Math.PI;

        Assert.True(maxWidth <= straight * 1.6, $"{maxWidth:0.00} at {MaxAt.P.X:0.##},{MaxAt.P.Y:0.##} move {MaxAt.Move}: " + string.Join(" | ", moves.Skip(Math.Max(0, MaxAt.Move - 4)).Take(6).Select(m => $"{m.Kind} {m.Target.X:0.##},{m.Target.Y:0.##},{m.Target.Z:0.##}")));
        Assert.True(maxWidth >= straight * 0.6, $"{maxWidth:0} vs {straight:0}");
        // Only the corners (r = 3) stay: 4 × (1 − π/4) × 9 ≈ 7.7 mm².
        Assert.True(left < 9, $"left {left:0.0} mm²");
    }

    [Fact]
    public void Offset_pocket_takes_the_full_width_somewhere_for_comparison()
    {
        var (project, tool, pocket) = PocketProject(6, Rectangle(1, 0, 0, 30, 24));
        pocket.Strategy = PocketStrategy.Offset;
        bool Inside(Vec2 p) => p.X > 0 && p.X < 30 && p.Y > 0 && p.Y < 24;

        var (maxWidth, _) = Engagement(FirstLevel(project), tool.Radius, Inside, new Bounds2(-4, -4, 34, 28), -1);

        Assert.True(maxWidth > 150, $"{maxWidth:0}");
    }

    [Fact]
    public void Adaptive_pocket_with_an_island_and_an_inner_corner_clears_everything()
    {
        // L-shaped pocket with a round island.
        var l = Polygon(1, (0, 0), (40, 0), (40, 15), (15, 15), (15, 35), (0, 35));
        var island = new Contour(2, new Segment[] { new ArcSegment(new Vec2(7.5, 7.5), 2, 0, 2 * Math.PI) });
        var (project, tool, pocket) = PocketProject(4, l, island);
        var step = tool.Diameter * pocket.AdaptiveStepOverPercent / 100;

        var result = ToolpathGenerator.Generate(project);
        Assert.Empty(result.Warnings);
        bool Inside(Vec2 p) =>
            ((p.X > 0 && p.X < 40 && p.Y > 0 && p.Y < 15) || (p.X > 0 && p.X < 15 && p.Y > 0 && p.Y < 35)) &&
            p.DistanceTo(new Vec2(7.5, 7.5)) > 2;
        var (maxWidth, left) = Engagement(FirstLevel(project), tool.Radius, Inside, new Bounds2(-3, -3, 43, 38), -1);
        var straight = Math.Acos(1 - step / tool.Radius) * 180 / Math.PI;

        var first = FirstLevel(project);
        // Two cores (one per arm): where their fronts meet in the passage beside the island the cutter briefly
        // has material on two sides — about twice a straight cut, still half of a full-width slot (180°).
        Assert.True(maxWidth < straight * 2.3, $"{maxWidth:0.00} at {MaxAt.P.X:0.##},{MaxAt.P.Y:0.##} move {MaxAt.Move}: " + string.Join(" | ", first.Skip(Math.Max(0, MaxAt.Move - 6)).Take(9).Select(m => $"{m.Kind} {m.Target.X:0.##},{m.Target.Y:0.##},{m.Target.Z:0.##}")));
        Assert.True(left < 6, $"left {left:0.0} mm²");
        // The island is never touched.
        Assert.All(result.Toolpaths.Single().Moves.Where(m => m.Kind != MoveKind.Rapid && m.Target.Z < 0),
            m => Assert.True(m.Target.XY.DistanceTo(new Vec2(7.5, 7.5)) >= 2 + tool.Radius - 0.02));
    }

    [Fact]
    public void Adaptive_pocket_stays_inside_and_cuts_every_level()
    {
        var (project, tool, pocket) = PocketProject(3, Rectangle(1, 0, 0, 20, 10));
        pocket.Depth = 2.5;
        pocket.Allowance = 0.2;

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        var cuts = moves.Where(m => m.Kind != MoveKind.Rapid && m.Target.Z < 0).ToList();
        var limit = tool.Radius + 0.2 - 0.02;
        Assert.All(cuts, m => Assert.True(m.Target.X >= limit && m.Target.X <= 20 - limit && m.Target.Y >= limit && m.Target.Y <= 10 - limit,
            $"{m.Target.X:0.###}, {m.Target.Y:0.###}"));
        Assert.Equal(new[] { -1.0, -2.0, -2.5 }, cuts.Select(m => Math.Round(m.Target.Z, 6)).Distinct().Where(z => z is -1 or -2 or -2.5).OrderByDescending(z => z));
        Assert.Equal(-2.5, cuts.Min(m => m.Target.Z), 6);
    }

    [Fact]
    public void Narrow_pocket_warns_that_it_is_cut_full_width()
    {
        var (project, _, _) = PocketProject(6, Rectangle(1, 0, 0, 40, 6.5));

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("полную ширину") || w.Contains("full tool width"));
        Assert.Single(result.Toolpaths);
    }

    [Fact]
    public void Trochoid_advances_one_step_per_turn_along_the_path()
    {
        var path = new List<Vec2> { new(0, 0), new(10, 0) };

        var points = ToolpathGenerator.Trochoid(path, 1, 0.5, climb: true);

        // There and back along the degenerate loop (20 mm) at 0.5 mm per turn: 40 turns of radius 1.
        Assert.All(points, p => Assert.InRange(p.Y, -1 - 1e-9, 1 + 1e-9));
        Assert.Equal(11, points.Max(p => p.X), 2);
        Assert.InRange(points.Min(p => p.X), -1, -0.7);
        Assert.Equal(new Vec2(1, 0), points[0]);
        Assert.True(points[^1].IsNear(points[0], 1e-9));
    }

    [Fact]
    public void Trochoid_advance_keeps_the_straight_cut_load()
    {
        // R 3, loop 1.5, step 0.9: the front of the loop is curved, so the advance is well below the step.
        var advance = ToolpathGenerator.TrochoidAdvance(3, 1.5, 0.9);

        Assert.InRange(advance, 0.3, 0.4);
        // Cos of the engagement at the loop front equals 1 − step / R.
        var b = 1.5 + advance;
        Assert.Equal(1 - 0.9 / 3, (Math.Pow(4.5, 2) - b * b - 9) / (6 * b), 6);
    }

    [Fact]
    public void Rest_machining_cuts_only_the_corners_the_larger_tool_left()
    {
        var (project, tool, pocket) = PocketProject(2, Rectangle(1, 0, 0, 20, 20));
        pocket.Strategy = PocketStrategy.Offset;
        pocket.RestFromDiameter = 6;

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut && m.Target.Z < 0).Select(m => m.Target.XY).ToList();
        Assert.NotEmpty(cuts);
        var corners = new[] { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, 20), new Vec2(0, 20) };
        // The 6 mm tool leaves material within about 1.25 mm of each corner; the 2 mm tool only goes there.
        Assert.All(cuts, p => Assert.True(corners.Min(c => c.DistanceTo(p)) < 4.5, $"{p.X:0.##}, {p.Y:0.##}"));
        Assert.Contains(cuts, p => p.DistanceTo(new Vec2(1, 1)) < 0.05);
    }

    [Fact]
    public void Rest_machining_with_a_larger_previous_allowance_runs_along_the_whole_wall()
    {
        var (project, _, pocket) = PocketProject(2, Rectangle(1, 0, 0, 20, 20));
        pocket.RestFromDiameter = 6;
        pocket.RestFromAllowance = 0.5;

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        // The 0.5 mm left by the larger tool along the walls: the small tool runs the whole left wall (x = 1).
        var alongWall = moves.Zip(moves.Skip(1))
            .Where(m => m.Second.Kind == MoveKind.Cut && m.Second.Target.Z < 0 && Math.Abs(m.First.Target.X - 1) < 0.01 && Math.Abs(m.Second.Target.X - 1) < 0.01)
            .Sum(m => Math.Abs(m.Second.Target.Y - m.First.Target.Y));
        Assert.True(alongWall > 15, $"{alongWall:0.##}");
    }

    [Fact]
    public void Rest_machining_needs_a_smaller_tool()
    {
        var (project, _, pocket) = PocketProject(6, Rectangle(1, 0, 0, 20, 20));
        pocket.RestFromDiameter = 4;

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Toolpaths);
        Assert.Contains(result.Warnings, w => w.Contains("меньше") || w.Contains("smaller"));
    }

    [Fact]
    public void Facing_covers_the_area_in_a_zigzag_with_the_centre_on_the_edges()
    {
        var project = CamProject.CreateDefault();
        var tool = new Tool { Diameter = 6, StepDown = 0.2, StepOverPercent = 40, FeedRate = 500, PlungeRate = 100 };
        project.Tools.Add(tool);
        project.Operations.Add(new FacingOperation { ToolId = tool.Id, X = 10, Y = 20, Width = 50, Height = 30, Depth = 0.5 });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut).Select(m => m.Target).ToList();
        // No contours: the facing area is the drawing, its lower-left corner the work zero.
        Assert.Equal(0, cuts.Min(p => p.X), 6);
        Assert.Equal(50, cuts.Max(p => p.X), 6);
        Assert.Equal(0, cuts.Min(p => p.Y), 6);
        Assert.Equal(30, cuts.Max(p => p.Y), 6);
        Assert.Equal(new[] { -0.2, -0.4, -0.5 }, cuts.Select(p => Math.Round(p.Z, 6)).Distinct().OrderByDescending(z => z));
        var rows = cuts.Where(p => Math.Abs(p.Z + 0.5) < 1e-9).Select(p => Math.Round(p.Y, 6)).Distinct().OrderBy(y => y).ToList();
        Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.True(pair.Second - pair.First <= 2.4 + 1e-9));
    }

    [Fact]
    public void Facing_area_follows_the_selected_contours_with_the_margin()
    {
        var area = new FacingOperation { Margin = 3, ContourIds = { 1 } }.Area(new Dictionary<int, Contour> { [1] = Rectangle(1, 5, 5, 25, 15) });

        Assert.Equal(new Bounds2(2, 2, 28, 18), area);
    }

    [Fact]
    public void Facing_lines_along_y_and_a_small_overhang_warns_about_corners()
    {
        var lines = ToolpathGenerator.FacingLines(new Bounds2(0, 0, 10, 40), 3, 0, 2, RasterAxis.Y);
        Assert.All(lines, l => Assert.Equal(l.A.X, l.B.X));
        Assert.Equal(3, lines.Min(l => l.A.X), 6);
        Assert.Equal(7, lines.Max(l => l.A.X), 6);

        var project = CamProject.CreateDefault();
        var tool = new Tool { Diameter = 6, StepDown = 1, StepOverPercent = 40 };
        project.Tools.Add(tool);
        project.Operations.Add(new FacingOperation { ToolId = tool.Id, Width = 20, Height = 20, OverhangPercent = 10 });
        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("21"));
    }

    [Fact]
    public void New_operations_survive_saving()
    {
        var project = CamProject.CreateDefault();
        project.Operations.Add(new PocketOperation { Strategy = PocketStrategy.Adaptive, AdaptiveStepOverPercent = 12, RestFromDiameter = 6, RestFromAllowance = 0.3 });
        project.Operations.Add(new FacingOperation { X = 1, Y = 2, Width = 3, Height = 4, Axis = RasterAxis.Y, OverhangPercent = 70, Margin = 5 });
        project.Operations.Add(new DrillOperation { SlotPitchPercent = 30 });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var pocket = Assert.IsType<PocketOperation>(copy.Operations[0]);
        Assert.Equal(PocketStrategy.Adaptive, pocket.Strategy);
        Assert.Equal(12, pocket.AdaptiveStepOverPercent);
        Assert.Equal(6, pocket.RestFromDiameter);
        Assert.Equal(0.3, pocket.RestFromAllowance);
        var facing = Assert.IsType<FacingOperation>(copy.Operations[1]);
        Assert.Equal((1, 2, 3, 4, RasterAxis.Y, 70, 5), (facing.X, facing.Y, facing.Width, facing.Height, facing.Axis, facing.OverhangPercent, facing.Margin));
        Assert.Equal(30, Assert.IsType<DrillOperation>(copy.Operations[2]).SlotPitchPercent);
    }
}
