using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class LaserTests
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
        var laser = ToolPresets.Cnc3018.Single(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        project.Contours.AddRange(contours);
        return (project, laser);
    }

    private static string[] Lines(string gcode) => gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static GrayImage Image(int width, int height, params byte[] pixels) =>
        new() { Width = width, Height = height, Pixels = pixels };

    [Fact]
    public void Laser_cut_uses_m4_power_and_never_moves_z()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10));
        project.Operations.Add(new LaserVectorOperation { Name = "Cut", ToolId = laser.Id, PowerPercent = 80, Speed = 300, Passes = 2, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);
        var lines = Lines(GcodeWriter.Write("p", result, project.Machine));

        Assert.Empty(result.Warnings);
        Assert.Contains("M4 S0", lines);
        Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("S800") && l.Contains("F300"));
        Assert.DoesNotContain(lines, l => l.StartsWith("G4"));
        Assert.DoesNotContain(lines, l => l.Contains('Z') && !l.StartsWith('('));
        Assert.Equal("M30", lines[^1]);
        Assert.Contains("M5", lines);
        // Two passes around the 60 mm outline.
        var stats = ToolpathStatistics.Compute(result.Toolpaths, project.Machine, new Vec3(0, 0, 0));
        Assert.Equal(120, stats.CutLength, 3);
    }

    [Fact]
    public void Z_step_lowers_the_focus_on_each_pass()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 10));
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, Passes = 3, ZStepPerPass = 0.5, ContourIds = { 1 } });

        var zs = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut)
            .Select(m => Math.Round(m.Target.Z, 6)).Distinct().OrderByDescending(z => z).ToList();

        Assert.Equal(new[] { 0.0, -0.5, -1.0 }, zs);
    }

    [Fact]
    public void Hatch_fill_covers_the_area_in_zigzag_and_skips_holes()
    {
        var outer = Rectangle(1, 0, 0, 10, 10);
        var hole = Rectangle(2, 4, 4, 6, 6);
        var lines = ToolpathGenerator.HatchLines(new List<Contour> { outer, hole }, 1, 0, new List<string>(), "t");

        // Rows at y = 0.5 … 9.5; rows 4.5 and 5.5 are split by the hole.
        Assert.Equal(12, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.InRange(l.From.X, -1e-6, 10 + 1e-6);
            Assert.Equal(l.From.Y, l.To.Y, 6);
            var midX = (l.From.X + l.To.X) / 2;
            var insideHole = l.From.Y > 4 && l.From.Y < 6 && midX > 4 && midX < 6;
            Assert.False(insideHole);
        });
        Assert.True(lines[0].From.X < lines[0].To.X);
        Assert.True(lines[1].From.X > lines[1].To.X);
    }

    [Fact]
    public void Hatch_angle_rotates_the_lines()
    {
        var lines = ToolpathGenerator.HatchLines(new List<Contour> { Rectangle(1, 0, 0, 10, 10) }, 1, 90, new List<string>(), "t");

        Assert.All(lines, l => Assert.Equal(l.From.X, l.To.X, 6));
    }

    [Fact]
    public void Fill_and_line_burns_hatch_then_outline()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 10, 10));
        project.Operations.Add(new LaserVectorOperation
        {
            ToolId = laser.Id, Mode = LaserVectorMode.FillAndLine, FillSpacing = 0.5, PowerPercent = 30, ContourIds = { 1 },
        });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        var burns = moves.Where(m => m.Kind == MoveKind.Cut && m.Power > 0).ToList();
        Assert.Equal(20 + 4, burns.Count);
        Assert.All(burns, m => Assert.Equal(0.3, m.Power, 9));
    }

    [Fact]
    public void Raster_grayscale_maps_darkness_to_power_with_overscan()
    {
        var (project, laser) = LaserProject();
        // 4×1 picture: black, grey, white, black.
        project.Operations.Add(new LaserRasterOperation
        {
            ToolId = laser.Id, Image = Image(4, 1, 0, 128, 255, 0), WidthMm = 4, LineInterval = 1,
            PowerMinPercent = 0, PowerMaxPercent = 100, Overscan = 2, Mode = RasterMode.Grayscale,
        });

        var result = ToolpathGenerator.Generate(project);
        var moves = result.Toolpaths.Single().Moves;

        Assert.Empty(result.Warnings);
        var burns = moves.Where(m => m.Kind == MoveKind.Cut).ToList();
        // Overscan in, black, grey, white (off), black, overscan out.
        Assert.Equal(new[] { 0, 1, 0.498, 0, 1, 0 }, burns.Select(m => Math.Round(m.Power, 3)).ToArray());
        // The line starts 2 mm before the picture (rapid) and ends 2 mm after it.
        Assert.Equal(-2, moves.First(m => m.Kind == MoveKind.Rapid).Target.X, 6);
        Assert.Equal(6, burns.Max(m => m.Target.X), 6);
        Assert.All(burns, m => Assert.Equal(0.5, m.Target.Y, 6));
    }

    [Fact]
    public void Raster_runs_bidirectionally_and_skips_white_rows()
    {
        var (project, laser) = LaserProject();
        // 2×3 picture: black row, white row, black row.
        project.Operations.Add(new LaserRasterOperation
        {
            ToolId = laser.Id, Image = Image(2, 3, 0, 0, 255, 255, 0, 0), WidthMm = 2, LineInterval = 1,
            Mode = RasterMode.Threshold, Overscan = 0,
        });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;
        var burns = moves.Where(m => m.Kind == MoveKind.Cut && m.Power > 0).ToList();

        Assert.Equal(2, burns.Count);
        Assert.Equal(2.5, burns[0].Target.Y, 6); // top row first
        Assert.Equal(2, burns[0].Target.X, 6); // left to right
        Assert.Equal(0.5, burns[1].Target.Y, 6);
        Assert.Equal(0, burns[1].Target.X, 6); // back right to left
    }

    [Fact]
    public void Dither_keeps_the_average_darkness()
    {
        var pixels = Enumerable.Repeat((byte)191, 40 * 40).ToArray(); // 25 % dark
        var operation = new LaserRasterOperation { Image = Image(40, 40, pixels), WidthMm = 40, LineInterval = 1, Mode = RasterMode.Dither, PowerMaxPercent = 100 };

        var powers = ToolpathGenerator.RasterPowers(operation, 40, 40);
        var on = powers.SelectMany(r => r).Count(p => p > 0) / 1600.0;

        Assert.InRange(on, 0.2, 0.3);
        Assert.All(powers.SelectMany(r => r), p => Assert.True(p == 0 || p == 1));
    }

    [Fact]
    public void Raster_gcode_has_power_changes_inline_and_no_arcs()
    {
        var (project, laser) = LaserProject();
        project.Operations.Add(new LaserRasterOperation
        {
            ToolId = laser.Id, Image = Image(4, 1, 0, 128, 255, 0), WidthMm = 4, LineInterval = 1, PowerMaxPercent = 50,
        });

        var lines = Lines(GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine));

        Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("S500"));
        Assert.DoesNotContain(lines, l => l.StartsWith("G2 ") || l.StartsWith("G3 "));
    }

    [Fact]
    public void Laser_and_milling_mismatches_are_reported()
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 10, 10));
        var mill = new Tool();
        project.Tools.Add(mill);
        project.Operations.Add(new ProfileOperation { Name = "Mill", ToolId = mill.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { Name = "Wrong", ToolId = laser.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new LaserVectorOperation { Name = "No laser", ToolId = mill.Id, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("фрезерные операции"));
        Assert.Contains(result.Warnings, w => w.StartsWith("Wrong"));
        Assert.Contains(result.Warnings, w => w.StartsWith("No laser"));

        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Operations.RemoveAll(o => o.Name != "No laser");
        project.Operations[0].ToolId = laser.Id;
        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("профиль лазера"));
    }

    [Fact]
    public void Raster_operation_with_picture_survives_saving()
    {
        var project = CamProject.CreateDefault();
        project.Operations.Add(new LaserRasterOperation { Image = Image(2, 1, 10, 20), WidthMm = 30, Mode = RasterMode.Dither, X = 5 });
        project.Operations.Add(new LaserVectorOperation { Mode = LaserVectorMode.Fill, FillAngle = 45 });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var raster = Assert.IsType<LaserRasterOperation>(copy.Operations[0]);
        Assert.Equal(new byte[] { 10, 20 }, raster.Image.Pixels);
        Assert.Equal(RasterMode.Dither, raster.Mode);
        Assert.Equal(15, raster.HeightMm, 9);
        Assert.Equal(45, Assert.IsType<LaserVectorOperation>(copy.Operations[1]).FillAngle);
    }
}
