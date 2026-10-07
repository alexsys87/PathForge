using System.Globalization;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.Core.Tests;

/// <summary>Which G-code line is being simulated or machined, for the highlight in the G-code panel.</summary>
public class GcodeLineTests
{
    private static (CamProject Project, GenerationResult Result) Project(bool arcs)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Machine.UseArcs = arcs;
        project.Stock.Origin = OriginAnchor.Drawing;
        var tool = new Tool { Diameter = 3.175, StepDown = 1, StepOverPercent = 40, FeedRate = 400, PlungeRate = 100, SpindleRpm = 10000 };
        project.Tools.Add(tool);
        project.Contours.Add(new Contour(1, new Segment[] { new ArcSegment(new Vec2(50, 50), 10, 0, 2 * Math.PI) }));
        project.Contours.Add(new Contour(2, new Segment[]
        {
            new LineSegment(new Vec2(0, 0), new Vec2(20, 0)), new LineSegment(new Vec2(20, 0), new Vec2(20, 20)),
            new LineSegment(new Vec2(20, 20), new Vec2(0, 20)), new LineSegment(new Vec2(0, 20), new Vec2(0, 0)),
        }));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 } });
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 2 } });
        return (project, ToolpathGenerator.Generate(project));
    }

    /// <summary>Position after every line of the program (index 0 = before the first line).</summary>
    private static List<Vec3> Replay(string gcode)
    {
        var positions = new List<Vec3> { new(double.NaN, double.NaN, double.NaN) };
        var p = positions[0];
        foreach (var line in gcode.Split('\n'))
        {
            var code = line.Split('(')[0];
            double Word(char name, double current)
            {
                var token = code.Split(' ').FirstOrDefault(t => t.Length > 1 && t[0] == name);
                return token is null ? current : double.Parse(token[1..], CultureInfo.InvariantCulture);
            }

            p = new Vec3(Word('X', p.X), Word('Y', p.Y), Word('Z', p.Z));
            positions.Add(p);
        }

        return positions;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_move_maps_to_the_line_that_brings_the_machine_to_its_end(bool arcs)
    {
        var (project, result) = Project(arcs);

        var output = GcodeWriter.WriteWithLines("p", result, project.Machine);

        Assert.Equal(GcodeWriter.Write("p", result, project.Machine), output.Text);
        if (arcs)
        {
            Assert.Contains("G2 ", output.Text);
        }

        var positions = Replay(output.Text);
        var moves = result.Toolpaths.SelectMany(t => t.Moves).ToList();
        var lines = Enumerable.Range(0, moves.Count).Select(output.LineOfMove).ToList();
        var index = moves.Count;
        for (var k = 0; k < moves.Count; k++)
        {
            Assert.InRange(lines[k], 1, positions.Count - 1);
            // An arc line covers several moves: the machine is at the end of the last of them after the line.
            if (k + 1 < moves.Count && lines[k + 1] == lines[k])
            {
                continue;
            }

            var at = positions[lines[k]];
            Assert.True(at.DistanceTo(moves[k].Target) < 0.002, $"move {k} → line {lines[k]}: {at} instead of {moves[k].Target}");
        }

        if (!arcs)
        {
            // Without arcs a line is one move (or a move that changes nothing: the line before it).
            Assert.True(lines.Distinct().Count() > moves.Count * 0.9);
        }

        // Lines only go forward.
        Assert.Equal(lines.OrderBy(l => l), lines);
        Assert.Equal(0, output.LineOfMove(index));
        Assert.Equal(0, output.LineOfMove(-1));
    }

    [Fact]
    public void Simulation_reports_the_move_it_is_making()
    {
        var (project, result) = Project(arcs: false);
        var simulation = new StockSimulation(project, result, 0.5);

        Assert.Equal(0, simulation.CurrentMoveIndex);
        simulation.AdvanceTo(simulation.Moves[10].StartTime + simulation.Moves[10].Duration / 2, null);
        Assert.Equal(10, simulation.CurrentMoveIndex);
        simulation.RunToEnd();
        Assert.Equal(simulation.Moves.Count - 1, simulation.CurrentMoveIndex);
    }

    [Fact]
    public void Executing_line_is_as_many_motion_lines_back_as_blocks_wait_in_the_planner()
    {
        var accepted = new[] { 10, 11, 13, 14, 16 };

        // 15 blocks, 13 free: two in the planner, the older of them (line 14) is moving.
        Assert.Equal(14, GrblController.ExecutingLineFor(accepted, 17, 15, 13));
        Assert.Equal(16, GrblController.ExecutingLineFor(accepted, 17, 15, 14));
        // The planner is empty: the last accepted line (e.g. a dwell or spindle command) is being executed.
        Assert.Equal(17, GrblController.ExecutingLineFor(accepted, 17, 15, 15));
        // More blocks than lines remembered: the oldest one.
        Assert.Equal(10, GrblController.ExecutingLineFor(accepted, 17, 15, 0));
        // No buffer state in the status report ($10 without +2): the last accepted line.
        Assert.Equal(17, GrblController.ExecutingLineFor(accepted, 17, 15, null));
        Assert.Equal(17, GrblController.ExecutingLineFor(accepted, 17, 0, 13));
    }

    [Theory]
    [InlineData("G1X10F300", true)]
    [InlineData("X-1.5", true)]
    [InlineData("G2X1Y2I3J4", true)]
    [InlineData("Z.5", true)]
    [InlineData("M3S10000", false)]
    [InlineData("G4P1", false)]
    [InlineData("F300", false)]
    [InlineData("G21", false)]
    public void Motion_lines_are_those_with_axis_words(string text, bool motion) =>
        Assert.Equal(motion, GrblController.IsMotion(text));
}
