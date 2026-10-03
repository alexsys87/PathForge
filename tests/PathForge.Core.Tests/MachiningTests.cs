using System.Globalization;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class MachiningTests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static Contour Circle(int id, double x, double y, double r) =>
        new(id, new Segment[] { new ArcSegment(new Vec2(x, y), r, 0, 2 * Math.PI) });

    private static (CamProject Project, Tool Tool) Project(params Contour[] contours)
    {
        var project = new CamProject();
        var tool = new Tool { Diameter = 4, StepDown = 1, StepOverPercent = 50, FeedRate = 1000, PlungeRate = 200 };
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        return (project, tool);
    }

    private static List<Vec3> CutPoints(GenerationResult result, double z) =>
        result.Toolpaths.SelectMany(t => t.Moves)
            .Where(m => m.Kind == MoveKind.Cut && Math.Abs(m.Target.Z - z) < 1e-9)
            .Select(m => m.Target)
            .ToList();

    [Theory]
    [InlineData(3, 1, new[] { -1.0, -2.0, -3.0 })]
    [InlineData(2.5, 1, new[] { -1.0, -2.0, -2.5 })]
    [InlineData(0.5, 2, new[] { -0.5 })]
    public void Pass_depths_never_exceed_step_down(double depth, double step, double[] expected)
    {
        Assert.Equal(expected, ToolpathGenerator.PassDepths(0, depth, step));
    }

    [Theory]
    [InlineData(ProfileSide.Outside, -2, 22)]
    [InlineData(ProfileSide.Inside, 2, 18)]
    [InlineData(ProfileSide.OnLine, 0, 20)]
    public void Profile_offsets_by_tool_radius(ProfileSide side, double min, double max)
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 20, 20));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = side, Depth = 2, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var bottom = CutPoints(result, -2);
        Assert.NotEmpty(bottom);
        var bounds = Bounds2.Of(bottom.Select(p => p.XY));
        Assert.Equal(min, bounds.MinX, 2);
        Assert.Equal(max, bounds.MaxX, 2);
        Assert.Equal(min, bounds.MinY, 2);
        Assert.Equal(max, bounds.MaxY, 2);
    }

    [Theory]
    [InlineData(ProfileSide.Outside, CutDirection.Climb, false)]
    [InlineData(ProfileSide.Outside, CutDirection.Conventional, true)]
    [InlineData(ProfileSide.Inside, CutDirection.Climb, true)]
    [InlineData(ProfileSide.Inside, CutDirection.Conventional, false)]
    public void Climb_milling_cuts_outside_clockwise_and_inside_counter_clockwise(ProfileSide side, CutDirection direction, bool counterClockwise)
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 20, 20));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = side, Direction = direction, Depth = 1, ContourIds = { 1 } });

        var loop = CutPoints(ToolpathGenerator.Generate(project), -1).Select(p => p.XY).ToList();

        Assert.Equal(counterClockwise, Polyline.IsCounterClockwise(loop));
    }

    [Fact]
    public void Profile_goes_down_in_steps_and_never_below_depth()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 20, 20));
        tool.StepDown = 1.5;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 4, ContourIds = { 1 } });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        var levels = moves.Where(m => m.Kind == MoveKind.Cut).Select(m => Math.Round(m.Target.Z, 6)).Distinct().OrderByDescending(z => z).ToList();
        Assert.Equal(new[] { -1.5, -3.0, -4.0 }, levels);
        Assert.True(moves.All(m => m.Target.Z >= -4 - 1e-9));
        Assert.Equal(project.Machine.SafeZ, moves[^1].Target.Z, 9);
    }

    [Fact]
    public void Tabs_lift_the_tool_on_the_last_passes()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 40, 40));
        project.Operations.Add(new ProfileOperation
        {
            ToolId = tool.Id, Depth = 3, ContourIds = { 1 }, TabCount = 4, TabWidth = 3, TabHeight = 1.5,
        });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var tabTop = -1.5;
        var onTabs = CutPoints(result, tabTop);
        Assert.NotEmpty(onTabs);
        // The bottom pass still reaches full depth between the tabs.
        Assert.NotEmpty(CutPoints(result, -3));
    }

    [Fact]
    public void Inner_contours_are_cut_before_the_outline()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 100, 100), Circle(2, 50, 50, 10));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1, 2 } });

        var firstCut = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves.First(m => m.Kind == MoveKind.Cut);

        // Outside offset of the circle lies within radius 12 of its centre.
        Assert.True(firstCut.Target.XY.DistanceTo(new Vec2(50, 50)) < 12.1);
    }

    [Fact]
    public void Pocket_stays_inside_the_wall_and_avoids_islands()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 40, 40), Rectangle(2, 15, 15, 25, 25));
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1, 2 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = result.Toolpaths.Single().Moves.Where(m => m.Kind != MoveKind.Rapid && m.Target.Z < 0).ToList();
        Assert.NotEmpty(cuts);
        foreach (var move in cuts)
        {
            var p = move.Target.XY;
            Assert.InRange(p.X, 2 - 1e-3, 38 + 1e-3);
            Assert.InRange(p.Y, 2 - 1e-3, 38 + 1e-3);
            // Tool centre must keep one radius away from the island 15..25.
            var dx = Math.Max(0, Math.Max(15 - p.X, p.X - 25));
            var dy = Math.Max(0, Math.Max(15 - p.Y, p.Y - 25));
            Assert.True(Math.Sqrt(dx * dx + dy * dy) >= 2 - 1e-3, $"Tool centre {p} cuts into the island");
        }

        Assert.Contains(cuts, m => Math.Abs(m.Target.Z + 2) < 1e-9);
    }

    [Fact]
    public void Pocket_too_small_for_tool_reports_warning()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 3, 3));
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Toolpaths);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Drill_plunges_at_circle_centres_with_pecks()
    {
        var (project, tool) = Project(Circle(1, 10, 10, 1.5), Circle(2, 30, 10, 1.5), Rectangle(3, 0, 0, 5, 5));
        project.Operations.Add(new DrillOperation { ToolId = tool.Id, Depth = 6, PeckDepth = 2, ContourIds = { 1, 2, 3 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Single(result.Warnings); // the rectangle is not a circle
        var plunges = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Plunge).ToList();
        Assert.Equal(6, plunges.Count); // 3 pecks per hole
        Assert.All(plunges, m => Assert.True(m.Target.XY.IsNear(new Vec2(10, 10), 1e-9) || m.Target.XY.IsNear(new Vec2(30, 10), 1e-9)));
        Assert.Equal(-6, plunges.Min(m => m.Target.Z), 9);
    }

    [Fact]
    public void Missing_tool_or_contours_are_reported_not_thrown()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 10, 10));
        project.Operations.Add(new ProfileOperation { Name = "A", ToolId = "missing", ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { Name = "B", ToolId = tool.Id });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Toolpaths);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Gcode_uses_invariant_numbers_and_standard_program_frame()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var (project, tool) = Project(Rectangle(1, 0, 0, 10.5, 10));
            tool.SpindleRpm = 12000;
            project.Operations.Add(new ProfileOperation { Name = "Контур (наружный)", ToolId = tool.Id, Depth = 1.25, ContourIds = { 1 } });
            var result = ToolpathGenerator.Generate(project);

            var gcode = GcodeWriter.Write("Test", result.Toolpaths, project.Machine);
            var lines = gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("G90 G94", lines);
            Assert.Contains("G21", lines);
            Assert.Contains("M3 S12000", lines);
            Assert.Equal("M30", lines[^1]);
            Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("Z-1.25") && l.Contains("F200"));
            Assert.Contains(lines, l => l.Contains("X12.5"));
            Assert.DoesNotContain(lines, l => !l.StartsWith('(') && l.Contains(','));
            // Comment parentheses inside names must not break the comment.
            Assert.Contains("(Контур [наружный])", lines);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Gcode_writes_only_changed_axes_and_feeds()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 10, 10));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.OnLine, Depth = 1, ContourIds = { 1 } });

        var gcode = GcodeWriter.Write("T", ToolpathGenerator.Generate(project).Toolpaths, project.Machine);

        var feedLines = gcode.Split('\n').Where(l => l.StartsWith("G1") && l.Contains("F1000")).ToList();
        Assert.Single(feedLines);
        Assert.Contains("G1 X10 F1000", gcode);
        Assert.Contains("G1 Y10\n", gcode);
    }

    [Fact]
    public void Statistics_sum_lengths()
    {
        var (project, tool) = Project(Rectangle(1, 0, 0, 10, 10));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.OnLine, Depth = 1, ContourIds = { 1 } });
        var result = ToolpathGenerator.Generate(project);

        var stats = ToolpathStatistics.Compute(result.Toolpaths, project.Machine, new Vec3(0, 0, project.Machine.SafeZ));

        // 40 mm around the square plus the plunge from the approach height (1 mm) to -1 mm.
        Assert.Equal(42, stats.CutLength, 6);
        Assert.True(stats.EstimatedTime > TimeSpan.Zero);
    }
}
