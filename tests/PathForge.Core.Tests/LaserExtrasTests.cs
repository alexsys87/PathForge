using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Kerf compensation, grey by speed and the power × speed test card.</summary>
public class LaserExtrasTests
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
        project.Stock.Origin = OriginAnchor.Drawing;
        var laser = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        project.Contours.AddRange(contours);
        return (project, laser);
    }

    /// <summary>Bounds of the burned loops, largest first.</summary>
    private static List<Bounds2> BurnedLoops(CamProject project)
    {
        var loops = new List<List<Vec2>>();
        List<Vec2>? current = null;
        foreach (var move in ToolpathGenerator.Generate(project).Toolpaths.Single().Moves)
        {
            if (move.Kind == MoveKind.Cut && move.Power > 0)
            {
                current ??= new List<Vec2>();
                current.Add(move.Target.XY);
            }
            else if (current is not null)
            {
                loops.Add(current);
                current = null;
            }
        }

        if (current is not null)
        {
            loops.Add(current);
        }

        return loops.Select(Bounds2.Of).OrderByDescending(b => b.Width).ToList();
    }

    [Theory]
    [InlineData(KerfCompensation.Parts, 21, 9)]
    [InlineData(KerfCompensation.Openings, 19, 11)]
    [InlineData(KerfCompensation.None, 20, 10)]
    public void Kerf_moves_outer_contours_and_holes_apart(KerfCompensation kerf, double outer, double hole)
    {
        var (project, laser) = LaserProject(Rectangle(1, 0, 0, 20, 20), Rectangle(2, 5, 5, 15, 15));
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, Kerf = kerf, KerfWidth = 1, ContourIds = { 1, 2 } });

        var loops = BurnedLoops(project);

        Assert.Equal(2, loops.Count);
        Assert.Equal(outer, loops[0].Width, 2);
        Assert.Equal(hole, loops[1].Width, 2);
    }

    [Fact]
    public void Grey_by_speed_keeps_the_power_and_slows_down_on_dark_pixels()
    {
        var (project, laser) = LaserProject();
        // One row: black, mid grey, white.
        project.Operations.Add(new LaserRasterOperation
        {
            ToolId = laser.Id, Image = new GrayImage { Width = 3, Height = 1, Pixels = new byte[] { 0, 128, 255 } },
            WidthMm = 3, LineInterval = 1, Speed = 3000, SpeedMin = 300, PowerMaxPercent = 80,
            Modulation = RasterModulation.Speed, Overscan = 0,
        });

        var result = ToolpathGenerator.Generate(project);
        var burns = result.Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut && m.Power > 0).ToList();

        Assert.Equal(2, burns.Count);
        Assert.All(burns, m => Assert.Equal(0.8, m.Power, 6));
        Assert.Equal(300, burns[0].Feed);
        // Half the darkness → half way between 1/3000 and 1/300 in burn time.
        Assert.InRange(burns[1].Feed, 500, 600);
        var lines = GcodeWriter.Write("p", result, project.Machine).Split('\n');
        Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("F300"));
        Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("S800"));
    }

    [Fact]
    public void Test_card_has_a_square_per_combination_and_labels()
    {
        var settings = new LaserTestGridSettings { PowerMinPercent = 20, PowerMaxPercent = 100, PowerSteps = 3, SpeedMin = 500, SpeedMax = 1500, SpeedSteps = 2, X = 100, Y = 50 };

        var grid = LaserTestGrid.Build(settings, "laser", 10);

        var squares = grid.Operations.Where(o => o.Mode == LaserVectorMode.Fill).ToList();
        Assert.Equal(6, squares.Count);
        Assert.Equal(new[] { 20.0, 60.0, 100.0 }, squares.Select(o => o.PowerPercent).Distinct().Order());
        Assert.Equal(new[] { 500.0, 1500.0 }, squares.Select(o => o.Speed).Distinct().Order());
        Assert.All(grid.Operations, o => Assert.Equal("laser", o.ToolId));
        Assert.Equal(grid.Contours.Count, grid.Contours.Select(c => c.Id).Distinct().Count());
        Assert.Equal(10, grid.Contours.Min(c => c.Id));
        // Labels: one line operation over all the digit strokes, everything to the right of and above (X, Y).
        var labels = Assert.Single(grid.Operations, o => o.Mode == LaserVectorMode.Line);
        Assert.True(labels.ContourIds.Count > 10);
        Assert.All(grid.Contours, c => Assert.True(c.GetBounds().MinX >= 100 - 1e-9 && c.GetBounds().MinY >= 50 - 1e-9));

        // The card generates without warnings.
        var project = new CamProject();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        var laser = ToolPresets.Cnc3018.First(p => p.Template.Kind == ToolKind.Laser).Create(1);
        project.Tools.Add(laser);
        var card = LaserTestGrid.Build(settings, laser.Id, 1);
        project.Contours.AddRange(card.Contours);
        project.Operations.AddRange(card.Operations);
        var result = ToolpathGenerator.Generate(project);
        Assert.Empty(result.Warnings);
        Assert.Equal(7, result.Toolpaths.Count);
    }
}
