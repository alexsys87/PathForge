using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Finger-joint boxes, corner relief, panel cutouts, dial scales, chamfers, helical holes, drawing edits.</summary>
public class BoxPanelTests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static Contour Polygon(int id, params (double X, double Y)[] points) =>
        new(id, points.Select((p, k) => (Segment)new LineSegment(new Vec2(p.X, p.Y), new Vec2(points[(k + 1) % points.Length].X, points[(k + 1) % points.Length].Y))));

    private static Contour Circle(int id, double x, double y, double diameter) =>
        new(id, new Segment[] { new ArcSegment(new Vec2(x, y), diameter / 2, 0, 2 * Math.PI) });

    private static (CamProject Project, Tool Tool) MillProject(Tool? tool = null)
    {
        var project = CamProject.CreateDefault();
        project.Tools.Clear();
        tool ??= new Tool { Id = "mill", Kind = ToolKind.EndMill, Diameter = 3.175, StepDown = 1, StepOverPercent = 40 };
        project.Tools.Add(tool);
        project.Stock.Origin = OriginAnchor.Drawing;
        return (project, tool);
    }

    private static List<Vec3> CutPoints(GenerationResult result) =>
        result.Toolpaths.SelectMany(t => t.Moves).Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();

    // ---- Corner relief -------------------------------------------------------------------------

    [Theory]
    [InlineData(CornerRelief.None)]
    [InlineData(CornerRelief.Dogbone)]
    [InlineData(CornerRelief.TBone)]
    public void Corner_relief_lets_the_cutter_reach_the_corners_of_a_hole(CornerRelief relief)
    {
        var (project, tool) = MillProject();
        project.Contours.Add(Rectangle(1, 0, 0, 20, 12));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.Inside, Depth = 1, CornerRelief = relief, ContourIds = { 1 } });

        var points = CutPoints(ToolpathGenerator.Generate(project)).Select(p => p.XY).ToList();
        var corners = new[] { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, 12), new Vec2(0, 12) };
        foreach (var corner in corners)
        {
            var nearest = points.Min(p => p.DistanceTo(corner));
            if (relief == CornerRelief.None)
            {
                Assert.Equal(tool.Radius * Math.Sqrt(2), nearest, 2);
            }
            else
            {
                Assert.Equal(tool.Radius, nearest, 2);
            }
        }

        // Without relief the cutter stays inside the walls; the ears of a relief go a little into them (by design).
        var inside = points.Where(p => p.X >= tool.Radius - 1e-3 && p.X <= 20 - tool.Radius + 1e-3 && p.Y >= tool.Radius - 1e-3 && p.Y <= 12 - tool.Radius + 1e-3);
        Assert.Equal(relief == CornerRelief.None, inside.Count() == points.Count);
        if (relief == CornerRelief.TBone)
        {
            // Along the longer (20 mm) edges: the ears lie on the lines y = r and y = 12 − r.
            Assert.All(points.Where(p => p.X < tool.Radius - 1e-3 || p.X > 20 - tool.Radius + 1e-3),
                p => Assert.True(Math.Abs(p.Y - tool.Radius) < 1e-2 || Math.Abs(p.Y - (12 - tool.Radius)) < 1e-2, $"{p}"));
        }
    }

    [Fact]
    public void Outside_profile_relieves_only_the_inner_corners_of_a_notch()
    {
        var (project, tool) = MillProject();
        // A 30×20 plate with a 10×5 notch in the top edge: two inner (unreachable) corners at (10,15) and (20,15).
        project.Contours.Add(Polygon(1, (0, 0), (30, 0), (30, 20), (20, 20), (20, 15), (10, 15), (10, 20), (0, 20)));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.Outside, Depth = 1, CornerRelief = CornerRelief.Dogbone, ContourIds = { 1 } });

        var points = CutPoints(ToolpathGenerator.Generate(project)).Select(p => p.XY).ToList();

        Assert.Equal(tool.Radius, points.Min(p => p.DistanceTo(new Vec2(10, 15))), 2);
        Assert.Equal(tool.Radius, points.Min(p => p.DistanceTo(new Vec2(20, 15))), 2);
        // Outer corners are wrapped around: the cutter stays a radius away, never cutting into the plate.
        Assert.Equal(tool.Radius, points.Min(p => p.DistanceTo(new Vec2(0, 0))), 2);
        Assert.DoesNotContain(points, p => p.X > 1e-3 && p.X < 30 - 1e-3 && p.Y > 1e-3 && p.Y < 15 - 1e-3);
    }

    // ---- Chamfer and helical holes -------------------------------------------------------------

    [Fact]
    public void Chamfer_depth_follows_the_width_and_the_bit_angle()
    {
        var vbit = new Tool { Id = "v", Kind = ToolKind.VBit, Diameter = 6, TipDiameter = 0.2, TipAngle = 90, StepDown = 0.5 };
        var (project, _) = MillProject(vbit);
        project.Contours.Add(Rectangle(1, 0, 0, 40, 30));
        project.Contours.Add(Circle(2, 20, 15, 3.4));
        project.Operations.Add(new ChamferOperation { ToolId = vbit.Id, ChamferWidth = 1, Side = ProfileSide.Outside, ContourIds = { 1 } });
        project.Operations.Add(new ChamferOperation { ToolId = vbit.Id, ChamferWidth = 1.3, Side = ProfileSide.Inside, ContourIds = { 2 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var edge = result.Toolpaths[0].Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();
        Assert.Equal(-1, edge.Min(p => p.Z), 6);
        Assert.Equal(2, edge.Select(p => Math.Round(p.Z, 6)).Distinct().Count(z => z < 0));
        // The tip runs half its width outside the edge.
        Assert.Equal(-0.1, edge.Where(p => p.Z < 0).Min(p => p.X), 2);
        var countersink = result.Toolpaths[1].Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();
        Assert.Equal(-1.3, countersink.Min(p => p.Z), 6);
        Assert.All(countersink.Where(p => p.Z < 0), p => Assert.Equal(1.7 - 0.1, p.XY.DistanceTo(new Vec2(20, 15)), 2));

        Assert.Equal(1, ChamferOperation.TipDepth(vbit, 1), 9);
        var sixty = new Tool { Kind = ToolKind.VBit, TipAngle = 60 };
        Assert.Equal(Math.Sqrt(3), ChamferOperation.TipDepth(sixty, 1), 9);
    }

    [Fact]
    public void Helical_holes_clear_from_the_centre_out_with_a_counterbore()
    {
        var (project, tool) = MillProject();
        project.Contours.Add(Circle(1, 50, 40, 10));
        project.Contours.Add(Circle(2, 10, 10, 3.4));
        project.Operations.Add(new HelixHoleOperation { ToolId = tool.Id, Depth = 4, ContourIds = { 1 } });
        project.Operations.Add(new HelixHoleOperation { ToolId = tool.Id, Depth = 4, CounterboreDiameter = 6, CounterboreDepth = 2, ContourIds = { 2 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var big = result.Toolpaths[0].Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();
        var radii = big.Select(p => p.XY.DistanceTo(new Vec2(50, 40))).ToList();
        Assert.Equal((10 - tool.Diameter) / 2, radii.Max(), 1e-6);
        Assert.Equal(-4, big.Min(p => p.Z), 6);
        // The last (outer) ring finishes the wall: the core is gone.
        Assert.Contains(radii, r => r <= tool.Radius + 1e-6);
        Assert.True(radii.IndexOf(radii.Max()) > radii.FindIndex(r => r > 0.06 && r <= tool.Radius + 1e-6));

        var small = result.Toolpaths[1].Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();
        var counterbore = small.Where(p => p.XY.DistanceTo(new Vec2(10, 10)) > (3.4 - tool.Diameter) / 2 + 0.01).ToList();
        Assert.NotEmpty(counterbore);
        Assert.All(counterbore, p => Assert.True(p.Z >= -2 - 1e-6));
        Assert.Equal(-4, small.Min(p => p.Z), 6);
        Assert.Equal((6 - tool.Diameter) / 2, small.Max(p => p.XY.DistanceTo(new Vec2(10, 10))), 1e-6);
    }

    // ---- Drawing edits -------------------------------------------------------------------------

    [Fact]
    public void Drawing_edits_move_turn_scale_delete_and_copy_with_the_operations()
    {
        var project = new CamProject();
        project.Contours.Add(Rectangle(1, 0, 0, 10, 10));
        project.Contours.Add(Circle(2, 30, 5, 4));
        project.Operations.Add(new ProfileOperation { ContourIds = { 1, 2 } });
        project.Operations.Add(new LaserPcbOperation { BoardContourIds = { 1 } });

        DrawingEdits.Move(project, new[] { 1 }, 5, -2);
        Assert.Equal(new Bounds2(5, -2, 15, 8), project.Contours[0].GetBounds());

        DrawingEdits.Rotate(project, new[] { 1 }, 90, new Vec2(5, -2));
        var turned = project.Contours[0].GetBounds();
        Assert.Equal(-5, turned.MinX, 6);
        Assert.Equal(8, turned.MaxY, 6);

        DrawingEdits.Scale(project, new[] { 2 }, 2, new Vec2(30, 5));
        Assert.True(project.Contours[1].TryGetCircle(out _, out var radius));
        Assert.Equal(4, radius, 6);

        var copies = DrawingEdits.RectangularArray(project, new[] { 2 }, 3, 2, 10, 20);
        Assert.Equal(5, copies.Count);
        Assert.Equal(7, project.Contours.Count);
        Assert.Equal(7, project.Operations[0].ContourIds.Count);
        Assert.Contains(project.Contours, c => c.TryGetCircle(out var center, out _) && center.IsNear(new Vec2(50, 25), 1e-9));

        var ring = DrawingEdits.CircularArray(project, new[] { 1 }, 4, 360, new Vec2(0, 0));
        Assert.Equal(3, ring.Count);
        Assert.Equal(4, ((LaserPcbOperation)project.Operations[1]).BoardContourIds.Count);

        Assert.Equal(4, DrawingEdits.Delete(project, ring.Append(1).ToList()));
        Assert.Empty(((LaserPcbOperation)project.Operations[1]).BoardContourIds);
        Assert.Equal(6, project.Operations[0].ContourIds.Count);
    }

    [Fact]
    public void Moved_text_letters_move_their_text_and_turned_ones_become_plain_contours()
    {
        var project = new CamProject();
        var text = new TextItem { X = 1, Y = 2 };
        project.Texts.Add(text);
        project.Contours.Add(new Contour(1, Rectangle(0, 0, 0, 2, 3).Segments) { TextId = text.Id });
        project.Contours.Add(new Contour(2, Rectangle(0, 3, 0, 5, 3).Segments) { TextId = text.Id });

        DrawingEdits.Move(project, new[] { 1, 2 }, 10, 0);
        Assert.Equal(11, text.X);
        Assert.Single(project.Texts);

        DrawingEdits.Rotate(project, new[] { 1, 2 }, 45, Vec2.Zero);
        Assert.Empty(project.Texts);
        Assert.All(project.Contours, c => Assert.Null(c.TextId));
    }

    // ---- Finger-joint box ----------------------------------------------------------------------

    /// <summary>Which parts hold a point of the box (in box coordinates, outside = 0).</summary>
    private static int Owners(List<FingerBox.BoxPart> parts, Vec3 outer, double t, Vec3 p, double frontHeight)
    {
        bool In(FingerBox.Part kind, Vec2 local) =>
            parts.Single(x => x.Kind == kind).Outline.Count(r => Polyline.Contains(r, local)) % 2 == 1;

        var count = 0;
        if (p.Z < t && In(FingerBox.Part.Bottom, new Vec2(p.X, p.Y)))
        {
            count++;
        }

        if (p.Y < t && p.Z < frontHeight && In(FingerBox.Part.Front, new Vec2(outer.X - p.X, p.Z)))
        {
            count++;
        }

        if (p.Y > outer.Y - t && In(FingerBox.Part.Back, new Vec2(p.X, p.Z)))
        {
            count++;
        }

        if (p.X < t && In(FingerBox.Part.Left, new Vec2(p.Y, p.Z)))
        {
            count++;
        }

        if (p.X > outer.X - t && In(FingerBox.Part.Right, new Vec2(outer.Y - p.Y, p.Z)))
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData(BoxLid.None)]
    [InlineData(BoxLid.Sliding)]
    public void Every_bit_of_the_box_shell_belongs_to_exactly_one_part(BoxLid lid)
    {
        var settings = new FingerBoxSettings { Width = 80, Depth = 50, Height = 30, Thickness = 3, FingerWidth = 8, Lid = lid };
        var warnings = new List<string>();
        var parts = FingerBox.Parts(settings, warnings, out var outer);
        const double t = 3;
        var frontHeight = lid == BoxLid.Sliding ? outer.Z - 2 * t - FingerBox.LidClearance : outer.Z;

        Assert.Empty(warnings);
        Assert.Equal(new Vec3(86, 56, lid == BoxLid.Sliding ? 39 : 33), outer);
        // Sample the shell (off any boundary) and count the parts that claim each point.
        var step = 0.37;
        var problems = new List<string>();
        for (var x = step / 2; x < outer.X; x += step)
        {
            for (var y = step / 2; y < outer.Y; y += step)
            {
                for (var z = step / 2; z < outer.Z; z += step)
                {
                    var shell = x < t || x > outer.X - t || y < t || y > outer.Y - t || z < t;
                    if (!shell)
                    {
                        continue;
                    }

                    var expected = lid == BoxLid.Sliding && y < t && z > frontHeight && x > t && x < outer.X - t ? 0 : 1;
                    var owners = Owners(parts, outer, t, new Vec3(x, y, z), frontHeight);
                    if (owners != expected && problems.Count < 5)
                    {
                        problems.Add($"({x:0.##}, {y:0.##}, {z:0.##}): {owners}");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Box_parts_are_laid_out_without_overlap_and_get_lids_and_grooves()
    {
        var settings = new FingerBoxSettings { Width = 80, Depth = 50, Height = 30, Thickness = 3, Gap = 4, SheetWidth = 200, X = 10, Y = 20 };
        var open = FingerBox.Build(settings, 1);
        Assert.Equal(5, open.Outlines.Count);
        Assert.Empty(open.Grooves);
        var boxes = open.Outlines.Select(c => c.GetBounds()).ToList();
        Assert.All(boxes, b => Assert.True(b.MinX >= 10 - 1e-6 && b.MinY >= 20 - 1e-6 && b.MaxX <= 210 + 1e-6));
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var overlap = boxes[i].MinX < boxes[j].MaxX && boxes[j].MinX < boxes[i].MaxX && boxes[i].MinY < boxes[j].MaxY && boxes[j].MinY < boxes[i].MaxY;
                Assert.False(overlap, $"parts {i} and {j} overlap");
            }
        }

        Assert.All(open.Outlines, c => Assert.True(c.IsClosed));

        settings.Lid = BoxLid.Overlay;
        var overlay = FingerBox.Build(settings, 1);
        Assert.Equal(7, overlay.Outlines.Count);
        Assert.Contains(overlay.Outlines, c => Math.Abs(c.GetBounds().Width - 86) < 1e-6 && Math.Abs(c.GetBounds().Height - 56) < 1e-6);

        settings.Lid = BoxLid.Sliding;
        var sliding = FingerBox.Build(settings, 1);
        Assert.Equal(6, sliding.Outlines.Count);
        Assert.Equal(3, sliding.Grooves.Count);
        Assert.All(sliding.Grooves, g => Assert.Equal(FingerBox.GrooveLayer, g.Layer));
        Assert.Equal(Enumerable.Range(1, 9), sliding.Outlines.Concat(sliding.Grooves).Select(c => c.Id).Order());
    }

    [Fact]
    public void Fit_widens_the_notches_and_narrows_the_fingers()
    {
        var tight = FingerBox.Parts(new FingerBoxSettings { Fit = 0 }, new List<string>(), out _);
        var loose = FingerBox.Parts(new FingerBoxSettings { Fit = 0.2 }, new List<string>(), out _);
        double Area(FingerBox.BoxPart part) => part.Outline.Sum(r => Polyline.SignedArea(r));

        foreach (var (a, b) in tight.Zip(loose))
        {
            Assert.True(Area(b) < Area(a), a.Name);
        }

        var tiny = new List<string>();
        FingerBox.Parts(new FingerBoxSettings { Width = 20, Depth = 20, Height = 20, FingerWidth = 2, Thickness = 3 }, tiny, out _);
        Assert.Single(tiny);
    }

    // ---- Panel cutouts and dial scales ---------------------------------------------------------

    [Fact]
    public void Every_panel_cutout_builds_closed_contours_at_its_place()
    {
        foreach (var cutout in PanelCutouts.All)
        {
            var contours = PanelCutouts.Place(cutout, cutout.Defaults, new Vec2(100, 50), 0, 1);
            Assert.NotEmpty(contours);
            Assert.All(contours, c => Assert.True(c.IsClosed, cutout.NameEn));
            Assert.All(contours, c => Assert.Equal(PanelCutouts.Layer, c.Layer));
            var bounds = contours.Aggregate(Bounds2.Empty, (b, c) => b.Union(c.GetBounds()));
            Assert.InRange(bounds.Center.X, 90, 110);
        }
    }

    [Fact]
    public void Panel_cutouts_have_the_right_sizes()
    {
        PanelCutout Find(string en) => PanelCutouts.All.Single(c => c.NameEn == en);

        var dHole = Bounds2.Of(Assert.Single(PanelCutouts.Place(Find("Hole with a flat"), new[] { 7.0, 6.0 }, Vec2.Zero, 0, 1)).Flatten(0.0001));
        Assert.Equal(7, dHole.Width, 2);
        Assert.Equal(-3.5, dHole.MinY, 3);
        Assert.Equal(2.5, dHole.MaxY, 6);

        var db9 = PanelCutouts.Place(Find("D-Sub DB9 (DE-9)"), Find("D-Sub DB9 (DE-9)").Defaults, Vec2.Zero, 0, 1);
        Assert.Equal(3, db9.Count);
        Assert.Equal(2, db9.Count(c => c.IsCircle));
        Assert.Equal(25, db9.Where(c => c.IsCircle).Select(c => c.TryGetCircle(out var center, out _) ? center.X : 0).Max() * 2, 1e-9);

        var rect = PanelCutouts.Place(Find("Rounded rectangle"), new[] { 30.0, 20, 3 }, new Vec2(5, 5), 90, 1).Single();
        var b = rect.GetBounds();
        Assert.Equal(20, b.Width, 6);
        Assert.Equal(30, b.Height, 6);
        Assert.Equal(30 * 20 - (4 - Math.PI) * 9, Math.Abs(Polyline.SignedArea(rect.Flatten(0.001))), 1);

        var vents = PanelCutouts.Place(Find("Vent: holes"), new[] { 40.0, 30, 4, 6 }, Vec2.Zero, 0, 1);
        Assert.True(vents.Count > 20);
        Assert.All(vents, c => Assert.True(Math.Abs(c.GetBounds().MinX) <= 20 + 1e-6 && Math.Abs(c.GetBounds().MaxY) <= 15 + 1e-6));
        var honeycomb = PanelCutouts.Place(Find("Vent: honeycomb"), new[] { 40.0, 30, 6, 1.5 }, Vec2.Zero, 0, 1);
        Assert.True(honeycomb.Count > 10);
        Assert.All(honeycomb, c => Assert.Equal(6, c.Segments.Count));

        var corners = PanelCutouts.Place(Find("Corner mounting holes"), new[] { 100.0, 60, 5, 3.2 }, new Vec2(50, 30), 0, 1);
        Assert.Equal(new[] { 5.0, 95, 95, 5 }, corners.Select(c => c.TryGetCircle(out var center, out _) ? Math.Round(center.X, 6) : 0).ToArray());
    }

    [Fact]
    public void Dial_scale_has_a_tick_per_division_and_labels_on_the_long_ones()
    {
        var settings = new DialScaleSettings { X = 10, Y = 10, Radius = 15, Divisions = 10, MajorEvery = 5, LabelFrom = 0, LabelTo = 10, LabelHeight = 2 };
        var lines = DialScale.Build(settings, 1);
        var ticks = lines.Where(c => c.Segments.Count == 1 && Math.Abs(c.Length - 1.5) < 1e-6 || Math.Abs(c.Length - 3) < 1e-6).ToList();

        // 11 ticks (3 long: 0, 5, 10), labels "0" (6 segments), "5" (5), "10" (2 + 6).
        Assert.Equal(11, lines.Count(c => c.Segments.Count == 1 && (Math.Abs(c.Length - 1.5) < 1e-6 || Math.Abs(c.Length - 3) < 1e-6)));
        Assert.Equal(11 + 6 + 5 + 8, lines.Count);
        Assert.All(lines, c => Assert.Equal(DialScale.Layer, c.Layer));
        // The first tick points to the lower left (225°), the last to the lower right (−45°).
        Assert.Contains(lines, c => c.Start.IsNear(new Vec2(10 + 15 * Math.Cos(225 * Math.PI / 180), 10 + 15 * Math.Sin(225 * Math.PI / 180)), 1e-6));
        Assert.Contains(lines, c => c.Start.IsNear(new Vec2(10 + 15 * Math.Cos(-45 * Math.PI / 180), 10 + 15 * Math.Sin(-45 * Math.PI / 180)), 1e-6));
        Assert.NotEmpty(ticks);

        settings.StrokeWidth = 0.4;
        settings.Arc = true;
        var outlines = DialScale.Build(settings, 1);
        Assert.All(outlines, c => Assert.True(c.IsClosed));
        // Ticks, the arc and one outline per digit group (digits joined into one shape each).
        Assert.InRange(outlines.Count, 11 + 1, 11 + 1 + 3 * 2);
    }

    [Fact]
    public void Box_cut_by_laser_and_mill_with_new_operations_survives_saving()
    {
        var project = new CamProject();
        project.Operations.Add(new ProfileOperation { CornerRelief = CornerRelief.TBone });
        project.Operations.Add(new ChamferOperation { ChamferWidth = 0.8, Side = ProfileSide.Inside });
        project.Operations.Add(new HelixHoleOperation { HoleDiameter = 5, CounterboreDiameter = 9, CounterboreDepth = 2.5 });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.Equal(CornerRelief.TBone, Assert.IsType<ProfileOperation>(copy.Operations[0]).CornerRelief);
        Assert.Equal(0.8, Assert.IsType<ChamferOperation>(copy.Operations[1]).ChamferWidth);
        var helix = Assert.IsType<HelixHoleOperation>(copy.Operations[2]);
        Assert.Equal((5.0, 9.0, 2.5), (helix.HoleDiameter, helix.CounterboreDiameter, helix.CounterboreDepth));
    }
}
