using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.Core.Tests;

public class VCarveEntryTests
{
    private static Contour Rectangle(double x0, double y0, double x1, double y1, int id = 1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static CamProject NewProject(Tool tool, Operation operation, params Contour[] contours)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        operation.ToolId = tool.Id;
        operation.ContourIds.AddRange(contours.Select(c => c.Id));
        project.Operations.Add(operation);
        return project;
    }

    private static Tool VBit(double angle = 60, double tip = 0, double stepDown = 10) =>
        new() { Kind = ToolKind.VBit, Diameter = 6, TipAngle = angle, TipDiameter = tip, StepDown = stepDown };

    /// <summary>Cutting moves (start and end points) below the stock top.</summary>
    private static List<Vec3> CutPoints(GenerationResult result) =>
        result.Toolpaths.SelectMany(t => t.Moves).Where(m => m.Kind != MoveKind.Rapid && m.Target.Z < -1e-6).Select(m => m.Target).ToList();

    private static void AssertNoGouge(List<Vec3> points, List<Vec2> boundary, Tool tool)
    {
        var tan = Math.Tan(tool.TipAngle * Math.PI / 360);
        foreach (var p in points)
        {
            Assert.True(Polyline.Contains(boundary, p.XY), $"{p} outside the shape");
            var radius = tool.TipDiameter / 2 + -p.Z * tan;
            var distance = Polyline.DistanceTo(boundary, p.XY, closed: true);
            Assert.True(radius <= distance + 0.02, $"{p}: cutting radius {radius:0.###} > distance to wall {distance:0.###}");
        }
    }

    [Fact]
    public void V_carve_goes_as_deep_as_the_shape_is_wide_without_touching_the_walls()
    {
        var tool = VBit();
        var shape = Rectangle(0, 0, 20, 4);
        var project = NewProject(tool, new VCarveOperation { Depth = 10 }, shape);

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var points = CutPoints(result);
        // Half width 2 mm with a 60° bit: 2 / tan 30° = 3.46 mm.
        Assert.InRange(points.Min(p => p.Z), -3.47, -3.3);
        AssertNoGouge(points, shape.Flatten(), tool);

        // The deepest cut runs along the centre line.
        var deepest = points.Where(p => p.Z < -3.3).ToList();
        Assert.All(deepest, p => Assert.InRange(p.Y, 1.8, 2.2));
        Assert.Contains(deepest, p => p.X < 3);
        Assert.Contains(deepest, p => p.X > 17);
    }

    [Fact]
    public void V_carve_reaches_into_sharp_corners()
    {
        var tool = VBit();
        var shape = Rectangle(0, 0, 10, 10);
        var project = NewProject(tool, new VCarveOperation { Depth = 20 }, shape);

        var points = CutPoints(ToolpathGenerator.Generate(project));

        AssertNoGouge(points, shape.Flatten(), tool);
        // Close to every corner along the diagonal there is a shallow cut.
        foreach (var corner in new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10) })
        {
            Assert.Contains(points, p => p.XY.DistanceTo(corner) < 0.3);
        }

        Assert.InRange(points.Min(p => p.Z), -5 / Math.Tan(Math.PI / 6) - 0.01, -5 / Math.Tan(Math.PI / 6) + 0.2);
    }

    [Fact]
    public void V_carve_depth_limit_gives_a_flat_bottom_in_steps()
    {
        var tool = VBit(angle: 90, tip: 0.2, stepDown: 0.5);
        var shape = Rectangle(0, 0, 12, 8);
        var project = NewProject(tool, new VCarveOperation { Depth = 1.2 }, shape);

        var result = ToolpathGenerator.Generate(project);

        var points = CutPoints(result);
        AssertNoGouge(points, shape.Flatten(), tool);
        Assert.Equal(-1.2, points.Min(p => p.Z), 6);
        // Pass levels -0.5 / -1.0 / -1.2 are visible.
        Assert.Contains(points, p => Math.Abs(p.Z + 0.5) < 1e-6);
        Assert.Contains(points, p => Math.Abs(p.Z + 1.0) < 1e-6);
        // The flat bottom reaches the middle of the shape.
        Assert.Contains(points, p => Math.Abs(p.Z + 1.2) < 1e-6 && Math.Abs(p.Y - 4) < 0.3);
    }

    [Fact]
    public void V_carve_letter_with_a_hole_stays_out_of_the_hole()
    {
        var tool = VBit();
        var outer = Rectangle(0, 0, 12, 12, 1);
        var hole = Rectangle(4, 4, 8, 8, 2);
        var project = NewProject(tool, new VCarveOperation { Depth = 10 }, outer, hole);

        var points = CutPoints(ToolpathGenerator.Generate(project));

        Assert.NotEmpty(points);
        var holeRing = hole.Flatten();
        var tan = Math.Tan(Math.PI / 6);
        foreach (var p in points)
        {
            Assert.False(Polyline.Contains(holeRing, p.XY), $"{p} inside the hole");
            var radius = -p.Z * tan;
            var distance = Math.Min(Polyline.DistanceTo(outer.Flatten(), p.XY, true), Polyline.DistanceTo(holeRing, p.XY, true));
            Assert.True(radius <= distance + 0.02, $"{p} gouges a wall");
        }

        // The ring between the walls is 4 mm wide: 2 / tan 30°.
        // Widest at the corners of the ring: the circle touching the outer corner walls and the hole corner.
        Assert.InRange(points.Min(p => p.Z), -4.07, -3.95);
    }

    [Fact]
    public void V_carve_of_real_letters_does_not_touch_the_outlines()
    {
        const string fontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf";
        if (!File.Exists(fontPath))
        {
            return;
        }

        var project = new CamProject();
        var item = new TextItem { Text = "Ag", HeightMm = 15 };
        project.Texts.Add(item);
        TextBuilder.Apply(project, item, TrueTypeFont.LoadFile(fontPath));
        var tool = VBit(angle: 60, tip: 0.1);
        var full = NewProject(tool, new VCarveOperation { Depth = 1.5, StepMm = 0.1 }, project.Contours.ToArray());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ToolpathGenerator.Generate(full);
        watch.Stop();

        Assert.True(watch.Elapsed.TotalSeconds < 20, $"took {watch.Elapsed}");
        var rings = project.Contours.Select(c => c.Flatten()).ToList();
        var tan = Math.Tan(Math.PI / 6);
        var points = CutPoints(result);
        Assert.Equal(-1.5, points.Min(p => p.Z), 6);
        foreach (var p in points)
        {
            Assert.True(rings.Count(r => Polyline.Contains(r, p.XY)) % 2 == 1, $"{p} outside the letters");
            var radius = 0.05 + -p.Z * tan;
            var distance = rings.Min(r => Polyline.DistanceTo(r, p.XY, true));
            Assert.True(radius <= distance + 0.02, $"{p} gouges a letter");
        }
    }

    [Fact]
    public void V_carve_needs_a_v_bit()
    {
        var project = NewProject(new Tool(), new VCarveOperation(), Rectangle(0, 0, 10, 10));

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("V-фрез", StringComparison.Ordinal));
        Assert.Empty(result.Toolpaths);
    }

    [Fact]
    public void V_carve_operation_is_saved_with_the_project()
    {
        var project = NewProject(VBit(), new VCarveOperation { Depth = 2, StepMm = 0.05, FlatStepMm = 0.3 }, Rectangle(0, 0, 10, 10));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var operation = Assert.IsType<VCarveOperation>(Assert.Single(loaded.Operations));
        Assert.Equal(0.05, operation.StepMm);
        Assert.Equal(0.3, operation.FlatStepMm);
    }

    // ---- Helix entry ----------------------------------------------------------------------

    private static Tool EndMill() => new() { Diameter = 3, StepDown = 1, FeedRate = 400, PlungeRate = 100 };

    /// <summary>No plunges into the material (except back down behind a tab) and no descent steeper than the ramp.</summary>
    private static void AssertGentleDescent(GenerationResult result, double rampAngle, double? tabTop = null)
    {
        var moves = result.Toolpaths.SelectMany(t => t.Moves).ToList();
        var previous = moves[0].Target;
        foreach (var move in moves.Skip(1))
        {
            if (move.Kind == MoveKind.Plunge && move.Target.Z < -1e-6)
            {
                Assert.True(tabTop is { } top && Math.Abs(previous.Z - top) < 1e-6, $"plunge to {move.Target}");
            }

            var drop = previous.Z - move.Target.Z;
            if (move.Kind == MoveKind.Cut && drop > 1e-9 && move.Target.Z < -1e-6)
            {
                var run = previous.XY.DistanceTo(move.Target.XY);
                Assert.True(drop <= run * Math.Tan(rampAngle * Math.PI / 180) + 1e-6, $"too steep to {move.Target}");
            }

            previous = move.Target;
        }
    }

    [Fact]
    public void Helix_profile_spirals_down_lap_by_lap_without_plunging()
    {
        var circle = new Contour(1, new Segment[] { new ArcSegment(new Vec2(0, 0), 10, 0, 2 * Math.PI) });
        var operation = new ProfileOperation { Depth = 3, Entry = EntryMode.Helix, RampAngle = 3 };
        var project = NewProject(EndMill(), operation, circle);

        var result = ToolpathGenerator.Generate(project);

        AssertGentleDescent(result, 3);
        var cuts = CutPoints(result);
        Assert.Equal(-3, cuts.Min(p => p.Z), 6);
        // One descending lap per millimetre plus a flat lap: about 4 laps of 2π·11.5 mm.
        var moves = result.Toolpaths.Single().Moves;
        var length = 0.0;
        for (var i = 1; i < moves.Count; i++)
        {
            if (moves[i].Kind == MoveKind.Cut)
            {
                length += moves[i - 1].Target.XY.DistanceTo(moves[i].Target.XY);
            }
        }

        Assert.InRange(length, 4 * 2 * Math.PI * 11.5 * 0.98, 4 * 2 * Math.PI * 11.5 * 1.02);
        Assert.All(cuts.Where(p => p.Z < -2.999), p => Assert.Equal(11.5, p.XY.Length, 2));
    }

    [Fact]
    public void Helix_pocket_enters_on_a_spiral()
    {
        var operation = new PocketOperation { Depth = 2, Entry = EntryMode.Helix, RampAngle = 5 };
        var project = NewProject(EndMill(), operation, Rectangle(0, 0, 30, 20));

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        AssertGentleDescent(result, 5);
        Assert.Equal(-2, CutPoints(result).Min(p => p.Z), 6);
    }

    [Fact]
    public void Helix_with_tabs_falls_back_to_the_zigzag_ramp()
    {
        var operation = new ProfileOperation { Depth = 3, Entry = EntryMode.Helix, RampAngle = 3, TabCount = 2, TabHeight = 1 };
        var project = NewProject(EndMill(), operation, Rectangle(0, 0, 60, 40));

        var result = ToolpathGenerator.Generate(project);

        AssertGentleDescent(result, 3, tabTop: -2);
        Assert.Equal(-3, CutPoints(result).Min(p => p.Z), 6);
    }

    // ---- Lead arcs ------------------------------------------------------------------------

    [Fact]
    public void Outside_profile_lead_arcs_start_and_end_away_from_the_part()
    {
        var square = Rectangle(0, 0, 20, 20);
        var tool = EndMill();
        var operation = new ProfileOperation { Depth = 2, Lead = LeadMode.Arc, LeadRadius = 2 };
        var project = NewProject(tool, operation, square);

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var moves = result.Toolpaths.Single().Moves;
        var firstPlunge = moves.First(m => m.Kind == MoveKind.Plunge);
        var boundary = square.Flatten();
        // Enters at tool radius + lead radius from the part.
        Assert.Equal(1.5 + 2, Polyline.DistanceTo(boundary, firstPlunge.Target.XY, true), 2);
        var lastCut = moves.Last(m => m.Kind == MoveKind.Cut);
        Assert.Equal(1.5 + 2, Polyline.DistanceTo(boundary, lastCut.Target.XY, true), 2);
        foreach (var p in CutPoints(result))
        {
            Assert.False(Polyline.Contains(boundary, p.XY));
            Assert.True(Polyline.DistanceTo(boundary, p.XY, true) >= 1.5 - 0.01, $"{p} cuts into the part");
        }
    }

    [Fact]
    public void Inside_profile_leads_stay_in_the_hole_and_shrink_when_needed()
    {
        var hole = Rectangle(0, 0, 8, 8);
        var tool = EndMill();
        var operation = new ProfileOperation { Side = ProfileSide.Inside, Depth = 1, Lead = LeadMode.Arc, LeadRadius = 5 };
        var project = NewProject(tool, operation, hole);

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var boundary = hole.Flatten();
        var points = CutPoints(result);
        Assert.All(points, p => Assert.True(Polyline.Contains(boundary, p.XY) && Polyline.DistanceTo(boundary, p.XY, true) >= 1.5 - 0.01, $"{p}"));
        // The lead brings the tool in from the middle of the hole, not straight down on the wall.
        var plunge = result.Toolpaths.Single().Moves.First(m => m.Kind == MoveKind.Plunge);
        Assert.True(Polyline.DistanceTo(boundary, plunge.Target.XY, true) > 2);
    }

    [Fact]
    public void Lead_arcs_with_tabs_and_ramp_keep_the_tabs()
    {
        var square = Rectangle(0, 0, 40, 40);
        var operation = new ProfileOperation
        {
            Depth = 3, Lead = LeadMode.Arc, LeadRadius = 2, TabCount = 4, TabHeight = 1, Entry = EntryMode.Ramp,
        };
        var project = NewProject(EndMill(), operation, square);

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        AssertGentleDescent(result, operation.RampAngle, tabTop: -2);
        var points = CutPoints(result);
        Assert.Equal(-3, points.Min(p => p.Z), 6);
        Assert.Contains(points, p => Math.Abs(p.Z + 2) < 1e-6);
    }

    [Fact]
    public void Lead_arcs_are_written_as_arcs_in_the_opposite_turn_of_the_profile()
    {
        var square = Rectangle(0, 0, 20, 20);
        var project = NewProject(EndMill(), new ProfileOperation { Depth = 1, Lead = LeadMode.Arc, LeadRadius = 3 }, square);
        project.Machine.UseArcs = true;

        var gcode = GcodeWriter.Write("lead", ToolpathGenerator.Generate(project), project.Machine);

        // Climb outside runs clockwise around the part (G2 corners); the leads turn the other way.
        Assert.Contains(gcode.Split('\n'), l => l.StartsWith("G3 ", StringComparison.Ordinal));
        Assert.Contains(gcode.Split('\n'), l => l.StartsWith("G2 ", StringComparison.Ordinal));
    }
}
