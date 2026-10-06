using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Import;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>End-to-end run on the demo drawing shipped in /samples.</summary>
public class SampleTests
{
    [Fact]
    public void Demo_plate_imports_and_produces_gcode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "demo-plate.dxf");
        var import = DxfReader.ReadFile(path);
        var contours = ContourBuilder.Build(import.Paths);

        Assert.Empty(import.Warnings);
        // Outline, 4 holes, pocket outline, island, slot.
        Assert.Equal(8, contours.Count);
        Assert.All(contours, c => Assert.True(c.IsClosed));
        Assert.Equal(5, contours.Count(c => c.TryGetCircle(out _, out _)));

        var project = CamProject.CreateDefault();
        project.Contours = contours;
        var mill = project.Tools[0];
        var drill = project.Tools[2];
        drill.Diameter = 5;
        var holes = contours.Where(c => c.TryGetCircle(out _, out var r) && r < 3).Select(c => c.Id).ToList();
        var pocket = contours.Where(c => c.Layer == "POCKET").Select(c => c.Id).ToList();
        var outline = contours.Where(c => c.Layer is "OUTLINE" or "SLOT").Select(c => c.Id).ToList();
        project.Operations.Add(new DrillOperation { Name = "Holes", ToolId = drill.Id, Depth = 6, ContourIds = holes });
        project.Operations.Add(new PocketOperation { Name = "Pocket", ToolId = mill.Id, Depth = 3, ContourIds = pocket });
        project.Operations.Add(new ProfileOperation { Name = "Cut out", ToolId = mill.Id, Depth = 6, TabCount = 4, ContourIds = outline });

        var result = ToolpathGenerator.Generate(project);
        var gcode = GcodeWriter.Write(project.Name, result.Toolpaths, project.Machine);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Toolpaths.Count);
        Assert.Contains("M30", gcode);
        Assert.True(gcode.Split('\n').Length > 100);
    }

    [Fact]
    public void Demo_board_isolation_drilling_and_cut_out()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "samples", "pcb-demo");
        var copper = GerberReader.ReadFile(Path.Combine(folder, "demo-F_Cu.gbr"), GerberMode.Copper);
        var outline = GerberReader.ReadFile(Path.Combine(folder, "demo-Edge_Cuts.gbr"), GerberMode.Outline);
        var drill = ExcellonReader.ReadFile(Path.Combine(folder, "demo.drl"));

        Assert.Empty(copper.Warnings);
        Assert.Empty(outline.Warnings);
        Assert.Empty(drill.Warnings);
        // Two nets: left pad - track - square pad, and right pad - track - oval pad.
        Assert.Equal(2, copper.Contours.Count(c => Polyline.IsCounterClockwise(c.Flatten())));
        Assert.True(Assert.Single(outline.Contours).IsClosed);
        Assert.Equal(3, drill.Holes.Count);

        var project = CamProject.CreateDefault();
        var id = 1;
        foreach (var contour in copper.Contours.Concat(outline.Contours).Concat(drill.ToContours()))
        {
            contour.Id = id++;
            project.Contours.Add(contour);
        }

        var engraver = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.VBit).Create(4);
        var drillBit = ToolPresets.Cnc3018.First(p => p.Name == "Сверло Ø0,8").Create(5);
        var corn = ToolPresets.Cnc3018.First(p => p.Name.StartsWith("Кукуруза Ø1,0", StringComparison.Ordinal)).Create(6);
        project.Tools.AddRange(new[] { engraver, drillBit, corn });
        var copperIds = project.Contours.Take(copper.Contours.Count).Select(c => c.Id).ToList();
        var outlineId = project.Contours[copper.Contours.Count].Id;
        var holeIds = project.Contours.Skip(copper.Contours.Count + 1).Select(c => c.Id).ToList();
        project.Stock.Thickness = 1.6;
        project.Operations.Add(new IsolationOperation { Name = "Isolation", ToolId = engraver.Id, ContourIds = copperIds });
        project.Operations.Add(new DrillOperation { Name = "Drill", ToolId = drillBit.Id, Depth = 1.8, ContourIds = holeIds });
        project.Operations.Add(new ProfileOperation
        {
            Name = "Cut out", ToolId = corn.Id, Depth = 1.8, TabCount = 4, TabWidth = 2, ContourIds = { outlineId },
            Entry = EntryMode.Ramp,
        });

        var result = ToolpathGenerator.Generate(project);
        var gcode = GcodeWriter.Write(project.Name, result, project.Machine);

        // The 1.0 mm hole is drilled with the 0.8 mm bit: one expected warning.
        Assert.Single(result.Warnings);
        Assert.Equal(3, result.Toolpaths.Count);
        Assert.Contains("G2 ", gcode);
        Assert.All(gcode.Split('\n'), l => Assert.True(l.Length <= 80));
    }

    [Theory]
    [InlineData(LaserPcbClearing.Isolation)]
    [InlineData(LaserPcbClearing.All)]
    public void Demo_board_by_laser_burns_the_paint_around_the_copper(LaserPcbClearing clearing)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "samples", "pcb-demo");
        var copper = GerberReader.ReadFile(Path.Combine(folder, "demo-F_Cu.gbr"), GerberMode.Copper);
        var outline = GerberReader.ReadFile(Path.Combine(folder, "demo-Edge_Cuts.gbr"), GerberMode.Outline);
        var drill = ExcellonReader.ReadFile(Path.Combine(folder, "demo.drl"));
        var project = CamProject.CreateDefault();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        var id = 1;
        foreach (var contour in copper.Contours.Concat(outline.Contours).Concat(drill.ToContours()))
        {
            contour.Id = id++;
            project.Contours.Add(contour);
        }

        var laser = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        // Everything selected: the outline is told apart as the board, the holes leave etched centre marks in the pads.
        project.Operations.Add(new LaserPcbOperation
        {
            Name = "Laser PCB", ToolId = laser.Id, Clearing = clearing, ContourIds = project.Contours.Select(c => c.Id).ToList(),
            BoardContourIds = { copper.Contours.Count + 1 },
        });

        var result = ToolpathGenerator.Generate(project);
        var gcode = GcodeWriter.Write(project.Name, result, project.Machine);

        Assert.Empty(result.Warnings);
        Assert.Single(result.Toolpaths);
        Assert.Contains("M4 S0", gcode);
        Assert.All(gcode.Split('\n'), l => Assert.True(l.Length <= 80));
    }

    [Fact]
    public void Altium_style_demo_board_matches_the_kicad_one()
    {
        // Same board as pcb-demo, written the way Altium Designer exports it: inch 2:5, macros, G74 arcs, NC drill .TXT.
        var folder = Path.Combine(AppContext.BaseDirectory, "samples", "pcb-altium");
        var copper = GerberReader.ReadFile(Path.Combine(folder, "demo.GTL"), GerberMode.Copper);
        var outline = GerberReader.ReadFile(Path.Combine(folder, "demo.GKO"), GerberMode.Outline);
        var holes = ExcellonReader.ReadFile(Path.Combine(folder, "demo-RoundHoles.TXT"));
        var slots = ExcellonReader.ReadFile(Path.Combine(folder, "demo-SlotHoles.TXT"));

        Assert.Empty(copper.Warnings);
        Assert.Empty(outline.Warnings);
        Assert.Empty(holes.Warnings);
        Assert.Empty(slots.Warnings);
        Assert.Equal(2, copper.Contours.Count(c => Polyline.IsCounterClockwise(c.Flatten())));

        var board = Assert.Single(outline.Contours);
        Assert.True(board.IsClosed);
        Assert.Equal(30, board.GetBounds().Width, 2);
        Assert.Equal(20, board.GetBounds().Height, 2);
        // Rounded corners of radius 2 mm: 600 - (4 - pi) * 4.
        Assert.Equal(600 - (4 - Math.PI) * 4, Math.Abs(Polyline.SignedArea(board.Flatten(0.005))), 0.1);

        Assert.Equal(new[] { 0.8, 0.8, 1.0 }, holes.Holes.Select(h => Math.Round(h.Diameter, 2)));
        Assert.True(holes.Holes[0].Center.IsNear(new Vec2(5, 10), 0.001));
        Assert.True(holes.Holes[2].Center.IsNear(new Vec2(15, 15), 0.001));
        var slot = Assert.Single(slots.Slots);
        Assert.Equal(24, slot.Path[0].X, 2);
        Assert.Equal(27, slot.Path[1].X, 2);
    }

    [Fact]
    public void Demo_sign_svg_has_three_layers()
    {
        var import = SvgReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "samples", "demo-sign.svg"));
        var contours = ContourBuilder.Build(import.Paths);

        Assert.Empty(import.Warnings);
        Assert.Equal(new[] { "Гравировка", "Контур", "Отверстия" }, contours.Select(c => c.Layer).Distinct().OrderBy(l => l));
        Assert.Equal(2, contours.Count(c => c.TryGetCircle(out _, out _)));
        var outline = contours.Single(c => c.Layer == "Контур");
        Assert.Equal(118, outline.GetBounds().Width, 3);
    }

    [Fact]
    public void Dome_stl_relief_generates_gcode()
    {
        var mesh = StlReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "samples", "dome.stl"));
        var project = CamProject.CreateDefault();
        var ball = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.BallNose).Create(4);
        project.Tools.Add(ball);
        project.Operations.Add(new ReliefOperation
        {
            Name = "Dome", ToolId = ball.Id, Source = ReliefSource.Mesh, Mesh = mesh, WidthMm = 40, Depth = 8,
            Resolution = 0.25, StepOverMm = 0.5,
        });

        var result = ToolpathGenerator.Generate(project);
        var gcode = GcodeWriter.Write(project.Name, result, project.Machine);

        Assert.Empty(result.Warnings);
        Assert.True(mesh.TriangleCount > 1000);
        Assert.Contains("M30", gcode);
        Assert.All(gcode.Split('\n'), l => Assert.True(l.Length <= 80));
    }
}
