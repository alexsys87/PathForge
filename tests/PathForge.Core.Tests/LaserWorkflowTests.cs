using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Micro-tabs, air assist, focus test, living hinge and the job frame.</summary>
public class LaserWorkflowTests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static (CamProject Project, Tool Laser) LaserProject(params Contour[] contours)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        var laser = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        project.Contours.AddRange(contours);
        return (project, laser);
    }

    private static void AssertBounds(Bounds2 expected, Bounds2 actual)
    {
        Assert.Equal(expected.MinX, actual.MinX, 6);
        Assert.Equal(expected.MinY, actual.MinY, 6);
        Assert.Equal(expected.MaxX, actual.MaxX, 6);
        Assert.Equal(expected.MaxY, actual.MaxY, 6);
    }

    private static string[] Lines(string gcode) => gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lengths of the runs of beam-off feed moves (the gaps the tabs leave).</summary>
    private static List<double> Gaps(IReadOnlyList<ToolMove> moves)
    {
        var gaps = new List<double>();
        var previous = moves[0].Target.XY;
        var gap = 0.0;
        foreach (var move in moves.Skip(1))
        {
            var length = move.Target.XY.DistanceTo(previous);
            previous = move.Target.XY;
            if (move.Kind == MoveKind.Cut && move.Power == 0)
            {
                gap += length;
                continue;
            }

            if (gap > 0)
            {
                gaps.Add(gap);
                gap = 0;
            }
        }

        if (gap > 0)
        {
            gaps.Add(gap);
        }

        return gaps;
    }

    [Fact]
    public void Micro_tabs_leave_gaps_on_the_outer_contour_in_every_pass_but_not_in_holes()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 40, 20), Rectangle(2, 10, 5, 20, 15));
        project.Operations.Add(new LaserVectorOperation
        {
            ToolId = laser.Id, Passes = 2, TabCount = 3, TabWidth = 0.5, ContourIds = { 1, 2 },
        });

        var result = ToolpathGenerator.Generate(project);
        var moves = result.Toolpaths.Single().Moves;

        Assert.Empty(result.Warnings);
        // 3 tabs × 2 passes, each the tab width plus the beam spot.
        var gaps = Gaps(moves);
        Assert.Equal(6, gaps.Count);
        Assert.All(gaps, g => Assert.Equal(0.5 + laser.Diameter, g, 6));
        // The hole (cut first) has no gaps: its 40 mm are burned in full in both passes.
        var burned = moves.Where(m => m.Kind == MoveKind.Cut && m.Power > 0).ToList();
        var stats = ToolpathStatistics.Compute(result.Toolpaths, project.Machine, new Vec3(0, 0, 0));
        Assert.Equal(2 * (120 + 40), stats.CutLength, 3);
        Assert.NotEmpty(burned);
    }

    [Fact]
    public void Micro_tabs_on_a_too_short_contour_are_skipped_with_a_warning()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 2, 2));
        project.Operations.Add(new LaserVectorOperation { Name = "Small", ToolId = laser.Id, TabCount = 8, TabWidth = 1, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Single(result.Warnings);
        Assert.Empty(Gaps(result.Toolpaths.Single().Moves));
    }

    [Fact]
    public void Open_lines_are_burned_from_their_nearer_end()
    {
        // The line is drawn from right to left; the laser starts at X0 Y0, next to its end.
        var line = new Contour(1, new Segment[] { new LineSegment(new Vec2(30, 0), new Vec2(1, 0)) });
        var (project, laser) = LaserProject(line);
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, ContourIds = { 1 } });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;
        var firstBurn = moves.First(m => m.Kind == MoveKind.Cut);

        Assert.Equal(new Vec2(30, 0), firstBurn.Target.XY);
        Assert.Equal(new Vec2(1, 0), moves.Last(m => m.Kind != MoveKind.Cut).Target.XY);
    }

    [Fact]
    public void Air_assist_is_switched_on_per_operation_and_off_at_the_end()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10), Rectangle(2, 30, 0, 40, 10));
        project.Operations.Add(new LaserVectorOperation { Name = "Engrave", ToolId = laser.Id, Mode = LaserVectorMode.Fill, FillSpacing = 1, ContourIds = { 2 } });
        project.Operations.Add(new LaserVectorOperation { Name = "Cut", ToolId = laser.Id, AirAssist = true, ContourIds = { 1 } });
        project.Operations.Add(new LaserVectorOperation { Name = "Mark", ToolId = laser.Id, ContourIds = { 2 } });

        var lines = Lines(GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine)).ToList();

        var engrave = lines.IndexOf("(Engrave)");
        var cut = lines.IndexOf("(Cut)");
        var mark = lines.IndexOf("(Mark)");
        Assert.Equal(1, lines.Count(l => l == "M8"));
        Assert.Equal(1, lines.Count(l => l == "M9"));
        Assert.InRange(lines.IndexOf("M8"), cut + 1, mark - 1);
        Assert.Equal(mark + 1, lines.IndexOf("M9"));
        Assert.DoesNotContain(lines.Take(cut), l => l is "M8" or "M9");
        Assert.True(engrave >= 0);

        // M7 when the air pump hangs on the mist output; switched off before the program end.
        project.Operations.RemoveAt(2);
        project.Machine.AirAssistCommand = CoolantCommand.Mist;
        lines = Lines(GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine)).ToList();
        Assert.Contains("M7", lines);
        Assert.True(lines.IndexOf("M9") < lines.LastIndexOf("M5"));
    }

    [Fact]
    public void Air_assist_is_off_while_the_program_waits_for_a_tool_change()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10));
        var second = laser.Clone();
        second.Id = "second";
        second.Number = 2;
        project.Tools.Add(second);
        project.Operations.Add(new LaserVectorOperation { Name = "A", ToolId = laser.Id, AirAssist = true, ContourIds = { 1 } });
        project.Operations.Add(new LaserVectorOperation { Name = "B", ToolId = second.Id, AirAssist = true, ContourIds = { 1 } });

        var lines = Lines(GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine)).ToList();
        var pause = lines.FindIndex(l => l.StartsWith("M0"));

        Assert.Equal("M9", lines[pause - 2]);
        Assert.Equal("M5", lines[pause - 1]);
        Assert.Equal(2, lines.Count(l => l == "M8"));
        Assert.Contains(lines.Skip(pause), l => l == "M8");
    }

    [Fact]
    public void Focus_test_burns_each_line_at_its_own_height()
    {
        var settings = new LaserFocusTestSettings { ZFrom = -2, ZTo = 2, Steps = 5, X = 10, Y = 5 };
        var test = LaserFocusTest.Build(settings, "laser", 100);

        // Five lines and the labels: "-2" "-1" "0" "1" "2".
        Assert.Equal(6, test.Operations.Count);
        Assert.Equal(new[] { -2.0, -1, 0, 1, 2, 0 }, test.Operations.Select(o => o.StartZ));
        Assert.All(test.Contours, c => Assert.Equal(LaserFocusTest.Layer, c.Layer));
        Assert.Equal(Enumerable.Range(100, test.Contours.Count), test.Contours.Select(c => c.Id));
        var bounds = test.Contours.Aggregate(Bounds2.Empty, (b, c) => b.Union(c.GetBounds()));
        Assert.Equal(10, bounds.MinX, 6);
        Assert.Equal(5, bounds.MinY, 6);
        // The lines get one above the other, the bottom one at ZFrom.
        var lineYs = test.Operations.Take(5).Select(o => test.Contours.Single(c => c.Id == o.ContourIds.Single()).GetBounds().MinY).ToList();
        Assert.Equal(lineYs.OrderBy(y => y), lineYs);

        var (project, laser) = LaserProject();
        project.Contours.AddRange(test.Contours);
        foreach (var operation in test.Operations)
        {
            operation.ToolId = laser.Id;
            project.Operations.Add(operation);
        }

        var result = ToolpathGenerator.Generate(project);
        var lines = Lines(GcodeWriter.Write("p", result, project.Machine));
        Assert.Empty(result.Warnings);
        Assert.Contains(lines, l => l.StartsWith("G0") && l.Contains("Z-2"));
        Assert.Contains(lines, l => l.StartsWith("G0") && l.Contains("Z2"));
        Assert.DoesNotContain(lines, l => l is "M8");
    }

    [Fact]
    public void Living_hinge_staggers_the_slots_and_cuts_every_second_line_to_the_edge()
    {
        var settings = new LivingHingeSettings { X = 0, Y = 0, Width = 6, Height = 60, SlotLength = 15, Bridge = 3, Spacing = 1.5 };
        var hinge = LivingHinge.Build(settings, "laser", 1);

        Assert.Empty(hinge.Warnings);
        Assert.Equal(LaserVectorMode.Line, hinge.Operation.Mode);
        Assert.True(hinge.Operation.AirAssist);
        Assert.Equal(hinge.Contours.Select(c => c.Id), hinge.Operation.ContourIds);
        var slots = hinge.Contours.Select(c => (Box: c.GetBounds(), c)).ToList();
        Assert.All(slots, s => Assert.Equal(s.Box.MinX, s.Box.MaxX, 9));
        var columns = slots.GroupBy(s => Math.Round(s.Box.MinX, 6)).OrderBy(g => g.Key).ToList();
        // Lines at 0, 1.5 … 6; 57 mm of slots and bridges give three 16 mm slots on the even lines.
        Assert.Equal(new[] { 0, 1.5, 3, 4.5, 6 }, columns.Select(g => g.Key));
        Assert.Equal(new[] { 3.0, 22, 41 }, columns[0].Select(s => Math.Round(s.Box.MinY, 6)).OrderBy(y => y));
        Assert.All(columns[0], s => Assert.Equal(16, s.Box.Height, 6));
        // Odd lines: bridges in the middle of the even slots, the end slots run to the edge.
        Assert.Equal(new[] { 0.0, 12.5, 31.5, 50.5 }, columns[1].Select(s => Math.Round(s.Box.MinY, 6)).OrderBy(y => y));
        Assert.Equal(60, columns[1].Max(s => s.Box.MaxY), 6);

        settings.ToEdge = false;
        var inner = LivingHinge.Build(settings, "laser", 1);
        Assert.All(inner.Contours, c => Assert.InRange(c.GetBounds().MinY, 3 - 1e-9, 57 + 1e-9));
        Assert.All(inner.Contours, c => Assert.InRange(c.GetBounds().MaxY, 3 - 1e-9, 57 + 1e-9));

        // Slots along X: the same pattern turned by 90°.
        settings.Along = RasterAxis.X;
        (settings.Width, settings.Height) = (settings.Height, settings.Width);
        var turned = LivingHinge.Build(settings, "laser", 1);
        Assert.Equal(inner.Contours.Count, turned.Contours.Count);
        Assert.All(turned.Contours, c => Assert.Equal(c.GetBounds().MinY, c.GetBounds().MaxY, 9));
    }

    [Fact]
    public void Living_hinge_is_cut_line_by_line_in_a_zigzag()
    {
        var hinge = LivingHinge.Build(new LivingHingeSettings { Width = 3, Height = 60 }, "laser", 1);
        var (project, laser) = LaserProject(hinge.Contours.ToArray());
        project.Stock.Origin = OriginAnchor.Drawing;
        hinge.Operation.ToolId = laser.Id;
        project.Operations.Add(hinge.Operation);

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;
        // Each line is finished before the next one starts: X changes only between lines.
        var xs = moves.Where(m => m.Kind == MoveKind.Cut).Select(m => Math.Round(m.Target.X, 6)).ToList();
        var changes = xs.Zip(xs.Skip(1)).Count(p => p.First != p.Second);
        Assert.Equal(xs.Distinct().Count() - 1, changes);
        var stats = ToolpathStatistics.Compute(new[] { new Toolpath(hinge.Operation, laser, moves) }, project.Machine, new Vec3(0, 0, 0));
        Assert.True(stats.RapidLength < 3 * 60, $"travel {stats.RapidLength}");
    }

    [Fact]
    public void Too_small_hinge_area_gives_a_warning()
    {
        var hinge = LivingHinge.Build(new LivingHingeSettings { Width = 10, Height = 5, Bridge = 3 }, "laser", 1);

        Assert.Empty(hinge.Contours);
        Assert.Single(hinge.Warnings);
    }

    [Fact]
    public void Frame_work_area_covers_the_feed_moves_only()
    {
        var gcode = string.Join('\n',
            "G21 G90",
            "G0 X-50 Y-50 (travel from far away)",
            "G0 X10 Y5",
            "M4 S0",
            "G1 X20 S500 F1000",
            "X30",
            "G3 X40 Y15 I0 J10",
            "G0 X100 Y100",
            "G91 G1 X5 Y-1",
            "G90 G53 G0 X0 Y0",
            "G1 X200 Y200",
            "M5");

        var area = GrblFrame.WorkArea(gcode);

        // The relative move from (100,100) counts; the G53 move does not, and the move after it has no known start.
        Assert.Equal(10, area.MinX, 6);
        Assert.Equal(5, area.MinY, 6);
        Assert.Equal(200, area.MaxX, 6);
        Assert.Equal(200, area.MaxY, 6);

        var circle = GrblFrame.WorkArea("G0 X0 Y0\nG3 X0 Y0 I10 J0");
        AssertBounds(new Bounds2(0, -10, 20, 10), circle);

        // Clockwise from (30,5) around (30,15) to (40,15) is three quarters of a circle: it bulges left and up.
        AssertBounds(new Bounds2(20, 5, 40, 25), GrblFrame.WorkArea("G0X30Y5\nG2X40Y15I0J10"));
        AssertBounds(new Bounds2(30, 5, 40, 15), GrblFrame.WorkArea("G0X30Y5\nG3X40Y15I0J10"));
        AssertBounds(new Bounds2(20, 5, 40, 25), GrblFrame.WorkArea("G0X30Y5\nG2X40Y15R-10"));
        AssertBounds(new Bounds2(30, 5, 40, 15), GrblFrame.WorkArea("G0X30Y5\nG3X40Y15R10"));

        var inches = GrblFrame.WorkArea("G20\nG0 X0 Y0\nG1 X1 Y2");
        AssertBounds(new Bounds2(0, 0, 25.4, 50.8), inches);

        Assert.True(GrblFrame.WorkArea("G0 X10 Y10\nG0 Z5").IsEmpty);

        // The program's own safe height (a milling frame goes up at least that far).
        GrblFrame.WorkArea("G0 Z15\nG0 X0 Y0\nG1 Z-1 F100\nG1 X10\nG0 Z15", out var top);
        Assert.Equal(15, top);
        GrblFrame.WorkArea("G20\nG0 Z1", out top);
        Assert.Equal(25.4, top, 6);
    }

    [Fact]
    public void Frame_of_a_generated_program_matches_the_burned_area()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10), Rectangle(2, 30, 5, 50, 25));
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Machine.UseArcs = true;
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, ContourIds = { 1, 2 } });

        var area = GrblFrame.WorkArea(GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine));

        AssertBounds(new Bounds2(0, 0, 50, 25), area);
    }

    [Fact]
    public void Frame_traces_the_area_at_low_laser_power_without_touching_z()
    {
        var area = new Bounds2(5, 2, 45, 32);
        var laser = GrblFrame.Build(area, new GrblFrameOptions(1500, 1, Laser: true, SpindleMaxS: 1000, SafeZ: 5));

        Assert.Equal(new[]
        {
            "G21G90G94", "G0X5Y2", "M4S10", "G1X45Y2F1500", "G1X45Y32", "G1X5Y32", "G1X5Y2", "M5S0",
        }, laser);
        Assert.Empty(GrblProgram.Prepare(string.Join('\n', laser)).Problems);

        var dark = GrblFrame.Build(area, new GrblFrameOptions(1500, 0, Laser: true, SpindleMaxS: 1000, SafeZ: 5));
        Assert.DoesNotContain(dark, l => l.StartsWith('M'));

        var mill = GrblFrame.Build(area, new GrblFrameOptions(800, 1, Laser: false, SpindleMaxS: 1000, SafeZ: 5));
        Assert.Equal("G0Z5", mill[1]);
        Assert.DoesNotContain(mill, l => l.StartsWith('M'));
    }

    [Fact]
    public void Segment_digits_draw_a_minus_and_a_decimal_point()
    {
        var test = LaserFocusTest.Build(new LaserFocusTestSettings { ZFrom = -0.5, ZTo = 0.5, Steps = 3 }, "l", 1);
        var labels = test.Contours.Where(c => test.Operations[^1].ContourIds.Contains(c.Id)).ToList();

        // "-0.5": minus (1) + 0 (6) + point (1) + 5 (5); "0": 6; "0.5": 6 + 1 + 5.
        Assert.Equal(13 + 6 + 12, labels.Count);
    }

    [Fact]
    public void New_settings_survive_saving_the_project()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10));
        project.Machine.AirAssistCommand = CoolantCommand.Mist;
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, AirAssist = true, TabCount = 4, TabWidth = 0.3, ContourIds = { 1 } });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        var operation = (LaserVectorOperation)copy.Operations.Single();

        Assert.Equal(CoolantCommand.Mist, copy.Machine.AirAssistCommand);
        Assert.True(operation.AirAssist);
        Assert.Equal(4, operation.TabCount);
        Assert.Equal(0.3, operation.TabWidth);
    }
}
