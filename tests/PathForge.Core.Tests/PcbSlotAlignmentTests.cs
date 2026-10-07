using PathForge.Core.Geometry;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Excellon slots on the machine and alignment holes for double-sided boards.</summary>
public class PcbSlotAlignmentTests
{
    /// <summary>Slot contour as the Excellon reader makes it: G85 from (2, 3) to (6, 3), 1 mm wide.</summary>
    private static Contour Slot(int id = 1)
    {
        var result = ExcellonReader.Read("M48\nMETRIC\nT1C1.0\n%\nT1\nX2.0Y3.0G85X6.0Y3.0\nM30\n");
        var contour = Assert.Single(result.ToContours());
        contour.Id = id;
        return contour;
    }

    private static (CamProject Project, Tool Tool) Project(Tool tool, params Contour[] contours)
    {
        var project = new CamProject();
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 1.6;
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        return (project, tool);
    }

    [Fact]
    public void Excellon_slot_is_recognised_with_its_axis_and_width()
    {
        Assert.True(Slot().TryGetSlot(out var a, out var b, out var width));
        Assert.Equal(1, width, 9);
        Assert.Equal(new[] { 2.0, 6.0 }, new[] { a.X, b.X }.OrderBy(x => x).Select(x => Math.Round(x, 9)));
        Assert.Equal(3, a.Y, 9);

        var circle = new Contour(2, new Segment[] { new ArcSegment(new Vec2(0, 0), 1, 0, 2 * Math.PI) });
        Assert.False(circle.TryGetSlot(out _, out _, out _));
    }

    [Fact]
    public void Drilling_a_slot_drills_every_other_hole_first_along_the_axis()
    {
        var (project, drill) = Project(new Tool { Kind = ToolKind.Drill, Diameter = 1, StepDown = 2, FeedRate = 100, PlungeRate = 50 }, Slot());
        project.Operations.Add(new DrillOperation { ToolId = drill.Id, Depth = 1.8, SlotPitchPercent = 50, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var holes = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Plunge).Select(m => m.Target.XY).ToList();
        // 4 mm at 0.5 mm: 9 holes, both ends included; first the even ones (2.0, 3.0 … 6.0), then the odd ones.
        Assert.Equal(9, holes.Count);
        Assert.All(holes, h => Assert.Equal(3, h.Y, 6));
        Assert.Equal(new[] { 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0 }, holes.Select(h => Math.Round(h.X, 6)).OrderBy(x => x));
        var firstPass = holes.Take(5).Select(h => Math.Round(h.X, 6)).OrderBy(x => x).ToList();
        Assert.True(firstPass.SequenceEqual(new[] { 2.0, 3.0, 4.0, 5.0, 6.0 }) || firstPass.SequenceEqual(new[] { 2.0, 3.0, 4.0, 5.0, 6.0 }.Reverse()));
    }

    [Fact]
    public void Drilling_a_slot_with_another_drill_size_warns()
    {
        var (project, drill) = Project(new Tool { Kind = ToolKind.Drill, Diameter = 0.8, StepDown = 2 }, Slot());
        project.Operations.Add(new DrillOperation { ToolId = drill.Id, Depth = 1.8, ContourIds = { 1 } });

        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("уже") || w.Contains("narrower"));
    }

    [Fact]
    public void Slot_as_wide_as_the_mill_is_routed_along_its_axis()
    {
        var (project, mill) = Project(new Tool { Diameter = 1, StepDown = 0.5, FeedRate = 150, PlungeRate = 50 }, Slot());
        project.Operations.Add(new ProfileOperation { ToolId = mill.Id, Side = ProfileSide.Inside, Depth = 1.8, Entry = EntryMode.Plunge, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = result.Toolpaths.Single().Moves.Where(m => m.Kind != MoveKind.Rapid && m.Target.Z < 0).Select(m => m.Target).ToList();
        Assert.All(cuts, p => Assert.Equal(3, p.Y, 6));
        Assert.Equal(2, cuts.Min(p => p.X), 6);
        Assert.Equal(6, cuts.Max(p => p.X), 6);
        Assert.Equal(-1.8, cuts.Min(p => p.Z), 6);
    }

    [Fact]
    public void Slot_narrower_than_the_mill_warns_that_it_comes_out_wider()
    {
        var (project, mill) = Project(new Tool { Diameter = 1.2, StepDown = 0.5 }, Slot());
        project.Operations.Add(new ProfileOperation { ToolId = mill.Id, Side = ProfileSide.Inside, Depth = 1.8, ContourIds = { 1 } });

        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("шире") || w.Contains("wider"));
    }

    [Fact]
    public void Alignment_holes_are_symmetric_outside_the_board_outline()
    {
        var outline = new Contour(1, new Segment[]
        {
            new LineSegment(new Vec2(10, 5), new Vec2(60, 5)), new LineSegment(new Vec2(60, 5), new Vec2(60, 35)),
            new LineSegment(new Vec2(60, 35), new Vec2(10, 35)), new LineSegment(new Vec2(10, 35), new Vec2(10, 5)),
        }, "board-Edge_Cuts.gbr");
        var copper = new Contour(2, new Segment[] { new ArcSegment(new Vec2(20, 30), 1, 0, 2 * Math.PI) }, "board-F_Cu.gbr");

        var holes = PcbAlignment.Holes(PcbAlignment.BoardBounds(new[] { outline, copper }), 3, 5, 10);

        Assert.Equal(2, holes.Count);
        Assert.All(holes, h => Assert.Equal(PcbAlignment.Layer, h.Layer));
        Assert.True(holes[0].TryGetCircle(out var left, out var r));
        Assert.True(holes[1].TryGetCircle(out var right, out _));
        Assert.Equal(1.5, r, 9);
        Assert.Equal(new Vec2(3.5, 20), left);
        Assert.Equal(new Vec2(66.5, 20), right);
        Assert.Equal(new[] { 10, 11 }, holes.Select(h => h.Id));
    }

    [Fact]
    public void Mirrored_second_side_keeps_the_holes_and_turns_the_copper_over_about_them()
    {
        var outline = new Contour(1, new Segment[]
        {
            new LineSegment(new Vec2(10, 5), new Vec2(60, 5)), new LineSegment(new Vec2(60, 5), new Vec2(60, 35)),
            new LineSegment(new Vec2(60, 35), new Vec2(10, 35)), new LineSegment(new Vec2(10, 35), new Vec2(10, 5)),
        }, "board.GKO");
        var pad = new Contour(2, new Segment[] { new ArcSegment(new Vec2(20, 30), 1, 0, 2 * Math.PI) }, "board.GBL");
        var contours = new List<Contour> { outline, pad };
        contours.AddRange(PcbAlignment.Holes(PcbAlignment.BoardBounds(contours), 3, 5, 3));

        (Vec2 Left, Vec2 Right, Vec2 Pad) ProgramPoints(IEnumerable<Contour> drawing)
        {
            var project = new CamProject();
            project.Contours = drawing.ToList();
            project.Stock.Origin = OriginAnchor.LowerLeft;
            var origin = project.Stock.OriginPoint(project.DrawingBounds());
            Vec2 Centre(int id)
            {
                project.Contours.Single(c => c.Id == id).TryGetCircle(out var c, out _);
                return c - origin;
            }

            var holes = new[] { Centre(3), Centre(4) }.OrderBy(p => p.X).ToArray();
            return (holes[0], holes[1], Centre(2));
        }

        var top = ProgramPoints(contours);
        var bottom = ProgramPoints(contours.Select(c => c.Transformed(Affine2.Scaling(-1, 1))));

        // The pins stay where they are; a point of the board lands mirrored about the middle between them.
        Assert.True(top.Left.IsNear(bottom.Left, 1e-9));
        Assert.True(top.Right.IsNear(bottom.Right, 1e-9));
        Assert.Equal(top.Left.X + top.Right.X - top.Pad.X, bottom.Pad.X, 9);
        Assert.Equal(top.Pad.Y, bottom.Pad.Y, 9);
    }
}
