using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>PCB by laser: paint burned off around the copper before etching.</summary>
public class LaserPcbTests
{
    private const double Spot = 0.1;

    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    /// <summary>Two 2×2 mm pads 1 mm apart, a 10 W laser profile and a laser with a 0.1 mm spot.</summary>
    private static (CamProject Project, LaserPcbOperation Operation) PcbProject(params Contour[] extra)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        var laser = new Tool { Kind = ToolKind.Laser, Name = "Laser", Diameter = Spot, FeedRate = 1000, PlungeRate = 1000, StepDown = 1 };
        project.Tools.Add(laser);
        project.Contours.Add(Rectangle(1, 0, 0, 2, 2));
        project.Contours.Add(Rectangle(2, 3, 0, 5, 2));
        project.Contours.AddRange(extra);
        var operation = new LaserPcbOperation { Name = "PCB", ToolId = laser.Id, ContourIds = { 1, 2 } };
        project.Operations.Add(operation);
        return (project, operation);
    }

    /// <summary>Burned segments (beam on) of the only toolpath.</summary>
    private static List<(Vec2 From, Vec2 To)> Burns(GenerationResult result)
    {
        var burns = new List<(Vec2, Vec2)>();
        var position = new Vec2(0, 0);
        foreach (var move in result.Toolpaths.Single().Moves)
        {
            if (move.Kind == MoveKind.Cut && move.Power > 0)
            {
                burns.Add((position, move.Target.XY));
            }

            position = move.Target.XY;
        }

        return burns;
    }

    /// <summary>Distance from a point to the copper of <see cref="PcbProject"/> (0 inside a pad).</summary>
    private static double DistanceToCopper(Vec2 p)
    {
        static double ToBox(Vec2 p, double x0, double x1)
        {
            var dx = Math.Max(Math.Max(x0 - p.X, p.X - x1), 0);
            var dy = Math.Max(Math.Max(0 - p.Y, p.Y - 2), 0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        return Math.Min(ToBox(p, 0, 2), ToBox(p, 3, 5));
    }

    /// <summary>A point counts as burned when a burned segment passes within half the spot.</summary>
    private static bool IsBurned(List<(Vec2 From, Vec2 To)> burns, Vec2 p) =>
        burns.Any(b => Polyline.DistanceToSegment(p, b.From, b.To) <= Spot / 2 + 1e-3);

    [Fact]
    public void Isolation_strip_keeps_half_a_spot_off_the_copper_and_ends_at_the_strip_width()
    {
        var (project, operation) = PcbProject();
        operation.IsolationWidth = 0.4;
        operation.Passes = 1;

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var points = Burns(result).SelectMany(b => new[] { b.From, b.To }).ToList();
        Assert.All(points, p => Assert.InRange(DistanceToCopper(p), Spot / 2 - 0.01, 0.4 - Spot / 2 + 0.01));
        Assert.Equal(-(0.4 - Spot / 2), points.Min(p => p.X), 2);
        Assert.Equal(5 + 0.4 - Spot / 2, points.Max(p => p.X), 2);
    }

    [Fact]
    public void Isolation_strip_is_burned_without_gaps()
    {
        var (project, operation) = PcbProject();
        operation.IsolationWidth = 0.4;
        operation.Passes = 1;

        var burns = Burns(ToolpathGenerator.Generate(project));

        // Across the strip on the left of the first pad: every point from the copper edge to the strip width.
        for (var x = -0.39; x <= -0.01; x += 0.02)
        {
            Assert.True(IsBurned(burns, new Vec2(x, 1)), $"x = {x:0.00}");
        }

        Assert.False(IsBurned(burns, new Vec2(1, 1)));
        Assert.False(IsBurned(burns, new Vec2(-0.6, 1)));
    }

    [Fact]
    public void Passes_repeat_the_whole_pattern()
    {
        var (project, operation) = PcbProject();
        operation.Passes = 1;
        var once = Burns(ToolpathGenerator.Generate(project)).Sum(b => b.From.DistanceTo(b.To));

        operation.Passes = 3;
        var thrice = Burns(ToolpathGenerator.Generate(project)).Sum(b => b.From.DistanceTo(b.To));

        Assert.Equal(3 * once, thrice, 3);
    }

    [Fact]
    public void Clearing_all_burns_everything_but_the_copper_inside_the_margin()
    {
        var (project, operation) = PcbProject();
        operation.Clearing = LaserPcbClearing.All;
        operation.Margin = 1;
        operation.Passes = 1;

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var burns = Burns(result);
        Assert.All(burns.SelectMany(b => new[] { b.From, b.To }), p => Assert.True(DistanceToCopper(p) >= Spot / 2 - 0.01, $"{p.X:0.###}, {p.Y:0.###}"));
        // Between the pads, far from them and in the corner of the margin.
        Assert.True(IsBurned(burns, new Vec2(2.5, 1)));
        Assert.True(IsBurned(burns, new Vec2(2.5, 2.8)));
        Assert.True(IsBurned(burns, new Vec2(-0.9, -0.9)));
        Assert.True(IsBurned(burns, new Vec2(2.02, 1.3)));
        Assert.False(IsBurned(burns, new Vec2(1, 1)));
        Assert.False(IsBurned(burns, new Vec2(4, 1.97)));
        Assert.False(IsBurned(burns, new Vec2(-1.2, 1)));
    }

    [Fact]
    public void Clearing_all_stays_inside_the_board_outline_even_when_it_is_selected_as_copper()
    {
        var (project, operation) = PcbProject(Rectangle(3, -2, -2, 7, 4));
        operation.Clearing = LaserPcbClearing.All;
        operation.Margin = 0;
        operation.Passes = 1;
        operation.ContourIds.Add(3);
        operation.BoardContourIds.Add(3);

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var burns = Burns(result);
        var points = burns.SelectMany(b => new[] { b.From, b.To }).ToList();
        Assert.Equal(-2 + Spot / 2, points.Min(p => p.X), 2);
        Assert.Equal(4 - Spot / 2, points.Max(p => p.Y), 2);
        Assert.True(IsBurned(burns, new Vec2(6.5, 3.5)));
        Assert.False(IsBurned(burns, new Vec2(1, 1)));
    }

    [Fact]
    public void Cross_hatch_turns_every_second_pass()
    {
        var (project, operation) = PcbProject();
        operation.Clearing = LaserPcbClearing.All;
        operation.Passes = 2;

        var burns = Burns(ToolpathGenerator.Generate(project));

        Assert.Contains(burns, b => Math.Abs(b.From.Y - b.To.Y) < 1e-9 && b.From.DistanceTo(b.To) > 1);
        Assert.Contains(burns, b => Math.Abs(b.From.X - b.To.X) < 1e-9 && b.From.DistanceTo(b.To) > 1);
    }

    [Fact]
    public void Growing_the_copper_moves_the_burn_away()
    {
        var (project, operation) = PcbProject();
        operation.CopperOffset = 0.05;
        operation.Passes = 1;

        var points = Burns(ToolpathGenerator.Generate(project)).SelectMany(b => new[] { b.From, b.To }).ToList();

        Assert.Equal(Spot / 2 + 0.05, points.Min(DistanceToCopper), 2);
    }

    [Fact]
    public void Warns_when_tracks_are_closer_than_the_spot()
    {
        var (project, _) = PcbProject();
        project.Contours[1] = Rectangle(2, 2.05, 0, 4, 2);

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("пятно") || w.Contains("spot"));
    }

    [Fact]
    public void Warns_when_lines_are_further_apart_than_the_spot()
    {
        var (project, operation) = PcbProject();
        operation.LineSpacing = 0.15;

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("0.15") || w.Contains("0,15"));
    }

    [Fact]
    public void Needs_a_laser_tool()
    {
        var (project, operation) = PcbProject();
        var mill = new Tool { Kind = ToolKind.EndMill, Diameter = 1 };
        project.Tools.Add(mill);
        operation.ToolId = mill.Id;

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Toolpaths);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Gcode_burns_with_m4_at_the_operation_speed_and_never_moves_z()
    {
        var (project, operation) = PcbProject();
        operation.PowerPercent = 70;
        operation.Speed = 1500;

        var result = ToolpathGenerator.Generate(project);
        var lines = GcodeWriter.Write("pcb", result.Toolpaths, project.Machine).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("M4 S0", lines);
        Assert.Contains(lines, l => l.Contains("S700") && l.Contains("F1500"));
        Assert.DoesNotContain(lines, l => l.Contains('Z') && !l.StartsWith('('));
    }

    [Fact]
    public void Operation_survives_saving()
    {
        var project = CamProject.CreateDefault();
        project.Operations.Add(new LaserPcbOperation
        {
            Clearing = LaserPcbClearing.All, BoardContourIds = { 7 }, Margin = 2, LineSpacing = 0.06, CrossHatch = false, CopperOffset = 0.03,
        });

        var copy = Assert.IsType<LaserPcbOperation>(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Operations.Single());

        Assert.Equal(LaserPcbClearing.All, copy.Clearing);
        Assert.Equal(new[] { 7 }, copy.BoardContourIds);
        Assert.Equal(2, copy.Margin);
        Assert.Equal(0.06, copy.LineSpacing);
        Assert.False(copy.CrossHatch);
        Assert.Equal(0.03, copy.CopperOffset);
    }
}
