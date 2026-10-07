using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Paste stencil, solder mask and silk screen by laser, copper clearing by mill, drilling by diameter.</summary>
public class PcbWorkflowTests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1, string layer = "") => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    }, layer);

    private static Contour Circle(int id, double x, double y, double diameter, string layer = "") =>
        new(id, new Segment[] { new ArcSegment(new Vec2(x, y), diameter / 2, 0, 2 * Math.PI) }, layer);

    private static Tool Preset(string nameEn, int number) => ToolPresets.Cnc3018.First(p => p.NameEn == nameEn).Create(number);

    /// <summary>The KiCad demo board: copper, outline and holes with ids from 1; returns the id lists.</summary>
    private static (CamProject Project, List<int> Copper, int Outline, List<int> Holes) DemoBoard()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "samples", "pcb-demo");
        var copper = GerberReader.ReadFile(Path.Combine(folder, "demo-F_Cu.gbr"), GerberMode.Copper);
        var outline = GerberReader.ReadFile(Path.Combine(folder, "demo-Edge_Cuts.gbr"), GerberMode.Outline);
        var drill = ExcellonReader.ReadFile(Path.Combine(folder, "demo.drl"));
        var project = CamProject.CreateDefault();
        project.Tools.Clear();
        var id = 1;
        foreach (var contour in copper.Contours.Concat(outline.Contours).Concat(drill.ToContours()))
        {
            contour.Id = id++;
            project.Contours.Add(contour);
        }

        var copperIds = project.Contours.Take(copper.Contours.Count).Select(c => c.Id).ToList();
        var outlineId = project.Contours[copper.Contours.Count].Id;
        var holeIds = project.Contours.Skip(copper.Contours.Count + 1).Select(c => c.Id).ToList();
        return (project, copperIds, outlineId, holeIds);
    }

    [Fact]
    public void Paste_stencil_windows_are_reduced_and_cut_to_size_inside_a_frame()
    {
        var pads = new[] { Rectangle(1, 0, 0, 1.5, 1), Rectangle(2, 3, 0, 4.5, 1), Rectangle(3, 10, 10, 10.15, 10.15) };
        var stencil = PasteStencil.Build(pads, new PasteStencilSettings { Reduction = 0.05, FrameMargin = 10 }, "laser", 0.1, 50);

        // The tiny pad (0.15 mm) vanishes; two windows and the sheet frame are left.
        var warning = Assert.Single(stencil.Warnings);
        Assert.Contains("1", warning);
        Assert.Equal(3, stencil.Contours.Count);
        Assert.Equal(new[] { 50, 51, 52 }, stencil.Contours.Select(c => c.Id));
        Assert.All(stencil.Contours, c => Assert.Equal(PasteStencil.Layer, c.Layer));
        var window = stencil.Contours.Select(c => c.GetBounds()).OrderBy(b => b.MinX).ToList();
        Assert.Equal(1.4, window[1].Width, 3);
        Assert.Equal(0.9, window[1].Height, 3);
        var frame = window[0];
        Assert.Equal(-9.95, frame.MinX, 3);
        Assert.Equal(4.45 + 10, frame.MaxX, 3);

        // With the frame the windows are holes: "parts to size" moves them in by half the kerf.
        Assert.Equal(KerfCompensation.Parts, stencil.Operation.Kerf);
        Assert.Equal(0.1, stencil.Operation.KerfWidth);
        Assert.True(stencil.Operation.AirAssist);
        var deltas = ToolpathGenerator.KerfDeltas(stencil.Contours, stencil.Operation);
        Assert.Equal(2, deltas.Values.Count(d => d < 0));
        Assert.Equal(1, deltas.Values.Count(d => d > 0));

        // Without the frame the windows are the outer contours: "openings to size" moves them in too.
        var bare = PasteStencil.Build(pads.Take(2), new PasteStencilSettings { FrameMargin = 0 }, "laser", 0.1, 1);
        Assert.Equal(KerfCompensation.Openings, bare.Operation.Kerf);
        Assert.All(ToolpathGenerator.KerfDeltas(bare.Contours, bare.Operation).Values, d => Assert.True(d < 0));
    }

    [Fact]
    public void Solder_mask_openings_are_burned_inside_the_contours_only()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        var laser = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        project.Contours.Add(Rectangle(1, 0, 0, 2, 1));
        project.Contours.Add(Rectangle(2, 5, 0, 5.05, 3)); // narrower than the spot
        project.Operations.Add(new LaserPcbOperation
        {
            Name = "Mask", ToolId = laser.Id, Clearing = LaserPcbClearing.Inside, Passes = 2, CrossHatch = true, LineSpacing = 0.05,
            ContourIds = { 1, 2 },
        });

        var result = ToolpathGenerator.Generate(project);
        var burned = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut && m.Power > 0).ToList();

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("1", warning);
        var half = laser.Diameter / 2;
        Assert.All(burned, m =>
        {
            Assert.InRange(m.Target.X, half - 1e-3, 2 - half + 1e-3);
            Assert.InRange(m.Target.Y, half - 1e-3, 1 - half + 1e-3);
        });
        // Cross-hatched: lines along X in the first pass, along Y in the second.
        Assert.Contains(burned, m => Math.Abs(m.Target.X - (2 - half)) < 1e-3);
        Assert.Contains(burned, m => Math.Abs(m.Target.Y - (1 - half)) < 1e-3);
    }

    [Fact]
    public void Copper_clearing_mills_everything_but_the_isolation_band()
    {
        var (project, copper, outline, _) = DemoBoard();
        var mill = Preset("End mill Ø2 (removing excess copper)", 1);
        var engraver = Preset("Engraver 20°, tip 0.1", 2);
        project.Tools.AddRange(new[] { mill, engraver });
        var isolation = new IsolationOperation { Name = "Isolation", ToolId = engraver.Id, ContourIds = copper };
        var band = ToolpathGenerator.IsolationBandWidth(isolation, engraver);
        var clearing = new CopperClearingOperation
        {
            Name = "Clear", ToolId = mill.Id, KeepDistance = band - 0.05, Margin = 1, BoardContourIds = { outline },
            ContourIds = copper.Append(outline).ToList(),
        };
        project.Operations.Add(isolation);
        project.Operations.Add(clearing);

        var result = ToolpathGenerator.Generate(project);
        var moves = result.Toolpaths.Single(t => t.Operation == clearing).Moves;

        Assert.InRange(band, 0.2, 0.6);
        // The mill edge never comes closer to the copper than the band (less the overlap).
        var copperRings = project.Contours.Where(c => copper.Contains(c.Id)).Select(c => c.Flatten(0.01)).ToList();
        var cuts = moves.Where(m => m.Kind == MoveKind.Cut && m.Target.Z < 0).Select(m => m.Target.XY).ToList();
        Assert.NotEmpty(cuts);
        foreach (var p in cuts)
        {
            var inside = copperRings.Count(r => Polyline.Contains(r, p)) % 2 == 1;
            Assert.False(inside);
            var distance = copperRings.Min(r => r.Zip(r.Skip(1).Append(r[0])).Min(s => DistanceToSegment(p, s.First, s.Second)));
            Assert.True(distance >= mill.Radius + clearing.KeepDistance - 0.02, $"{p} is {distance:0.###} from the copper");
        }

        // Inside the outline plus the margin.
        var board = project.Contours.Single(c => c.Id == outline).GetBounds();
        Assert.All(cuts, p => Assert.InRange(p.X, board.MinX - 1 + mill.Radius - 0.02, board.MaxX + 1 - mill.Radius + 0.02));
        Assert.All(moves.Where(m => m.Kind != MoveKind.Rapid), m => Assert.True(m.Target.Z >= -0.1 - 1e-9));
        Assert.Equal(2, result.Toolpaths.Count);
    }

    [Fact]
    public void Copper_clearing_reports_copper_left_where_the_mill_does_not_fit()
    {
        var project = CamProject.CreateDefault();
        project.Tools.Clear();
        var mill = Preset("End mill Ø2 (removing excess copper)", 1);
        project.Tools.Add(mill);
        // Two pads 3 mm apart: a 1.6 mm gap after the 0.7 mm bands, too narrow for the Ø2 mill.
        project.Contours.Add(Rectangle(1, 0, 0, 5, 5));
        project.Contours.Add(Rectangle(2, 8, 0, 13, 5));
        project.Operations.Add(new CopperClearingOperation { Name = "Clear", ToolId = mill.Id, KeepDistance = 0.7, Margin = 4, ContourIds = { 1, 2 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("мм²") || w.Contains("mm²"));
        Assert.Single(result.Toolpaths);
    }

    [Fact]
    public void Holes_are_grouped_by_diameter_and_get_the_nearest_drill()
    {
        var contours = new List<Contour>
        {
            Circle(1, 0, 0, 0.8), Circle(2, 5, 0, 0.8), Circle(3, 10, 0, 1.0), Circle(4, 15, 0, 0.9),
            Circle(5, 20, 0, 1.3), Circle(6, 25, 0, 3.2), Rectangle(7, 0, 5, 2, 7),
        };
        var drills = new[] { Preset("Drill Ø0.8", 1), Preset("Drill Ø1.0", 2), Preset("Drill Ø1.2", 3) };

        var plan = DrillPlanner.Plan(contours, drills.Reverse(), depth: 2);

        // 0.8 → Ø0.8; 0.9 is as near to 0.8 as to 1.0 → the larger one; 1.0 → Ø1.0; 1.3 → Ø1.2; 3.2 is milled.
        Assert.Equal(new[] { 0.8, 1.0, 1.2 }, plan.Groups.Select(g => g.Drill.Diameter));
        Assert.Equal(new[] { 1, 2 }, plan.Groups[0].ContourIds);
        Assert.Equal(new[] { 3, 4 }, plan.Groups[1].ContourIds);
        Assert.Equal(new[] { 0.9, 1.0 }, plan.Groups[1].Diameters);
        Assert.Equal(new[] { 5 }, plan.Groups[2].ContourIds);
        Assert.Equal(3, plan.Operations.Count);
        Assert.All(plan.Operations, o => Assert.Equal(2, o.Depth));
        Assert.Equal(plan.Groups.Select(g => g.Drill.Id), plan.Operations.Select(o => o.ToolId));
        // Rectangle skipped, 3.2 mm too large; 0.9 and 1.3 are within 0.1 mm of their drills.
        Assert.Equal(2, plan.Warnings.Count);

        var none = DrillPlanner.Plan(contours, Array.Empty<Tool>(), 2);
        Assert.Empty(none.Operations);
        Assert.Single(none.Warnings, w => w.Contains("Drill…") || w.Contains("Сверло…"));
    }

    [Fact]
    public void Planned_drilling_pauses_for_every_drill_change()
    {
        var (project, _, _, holes) = DemoBoard();
        project.Machine.Dialect = GcodeDialect.Grbl;
        var drills = new[] { Preset("Drill Ø1.0", 1), Preset("Drill Ø0.8", 2) };
        project.Tools.AddRange(drills);
        var plan = DrillPlanner.Plan(project.Contours.Where(c => holes.Contains(c.Id)), project.Tools, depth: 1.8);
        project.Operations.AddRange(plan.Operations);

        var result = ToolpathGenerator.Generate(project);
        var lines = GcodeWriter.Write("p", result, project.Machine).Split('\n');

        // The demo has two 0.8 mm holes and one 1.0 mm hole: thinnest drill first, one pause between them.
        Assert.Empty(plan.Warnings);
        Assert.Contains(plan.Operations[0].Name, new[] { "Drilling Ø0.8", "Сверловка Ø0.8" });
        Assert.Empty(result.Warnings);
        Assert.Single(lines, l => l.StartsWith("M0"));
    }

    [Fact]
    public void Clearing_and_inside_mode_survive_saving_the_project()
    {
        var project = new CamProject();
        project.Operations.Add(new CopperClearingOperation { KeepDistance = 0.35, Margin = 2, BoardContourIds = { 7 }, ContourIds = { 1, 2 } });
        project.Operations.Add(new LaserPcbOperation { Clearing = LaserPcbClearing.Inside });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var clearing = Assert.IsType<CopperClearingOperation>(copy.Operations[0]);
        Assert.Equal(0.35, clearing.KeepDistance);
        Assert.Equal(new[] { 7 }, clearing.BoardContourIds);
        Assert.Equal(LaserPcbClearing.Inside, Assert.IsType<LaserPcbOperation>(copy.Operations[1]).Clearing);
    }

    [Theory]
    [InlineData("board-F_Paste.gbr", PcbFileKind.Paste)]
    [InlineData("board-B_Mask.gbr", PcbFileKind.SolderMask)]
    [InlineData("board-F_Silkscreen.gbr", PcbFileKind.Silkscreen)]
    [InlineData("Board.GBO", PcbFileKind.Silkscreen)]
    [InlineData("board-F_Cu.gbr", null)]
    public void Auxiliary_layers_are_known_by_name(string name, PcbFileKind? expected) =>
        Assert.Equal(expected, PcbFileDetector.AuxiliaryLayerKind(name));

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / ab.LengthSquared, 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}
