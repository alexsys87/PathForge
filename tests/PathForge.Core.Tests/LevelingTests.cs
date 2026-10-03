using System.Globalization;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;
using PathForge.Core.Leveling;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class LevelingTests
{
    /// <summary>3×2 map over (0,0)-(20,10): heights 0, 0.1, 0.2 along X and +0.05 at Y=10.</summary>
    private static LevelingMap Map() => new()
    {
        X0 = 0, Y0 = 0, StepX = 10, StepY = 10, CountX = 3, CountY = 2,
        Heights = new[] { 0, 0.1, 0.2, 0.05, 0.15, 0.25 },
    };

    private static Dictionary<char, double> Words(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Where(w => char.IsLetter(w[0]) && w.Length > 1 && (char.IsDigit(w[1]) || w[1] == '-'))
            .ToDictionary(w => w[0], w => double.Parse(w[1..], CultureInfo.InvariantCulture));

    [Fact]
    public void Map_interpolates_between_points_and_keeps_edge_values_outside()
    {
        var map = Map();

        Assert.Equal(0, map.HeightAt(0, 0), 9);
        Assert.Equal(0.05, map.HeightAt(5, 0), 9);
        Assert.Equal(0.125, map.HeightAt(10, 5), 9);
        Assert.Equal(0.25, map.HeightAt(30, 20), 9);
        Assert.Equal(0, map.HeightAt(-5, -5), 9);
        Assert.Equal(5, map.DistanceOutside(25, 5), 9);
        Assert.Equal(0, map.DistanceOutside(5, 5), 9);
    }

    [Fact]
    public void Probe_points_snake_through_the_rows_and_map_back_to_the_grid()
    {
        var area = new Bounds2(0, 0, 20, 10);
        var points = LevelingMap.PlanPoints(area, 10, out var nx, out var ny);

        Assert.Equal((3, 2), (nx, ny));
        Assert.Equal(new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(20, 0), new Vec2(20, 10), new Vec2(10, 10), new Vec2(0, 10) }, points);

        // Machine Z of the touches, the first one becomes 0.
        var map = LevelingMap.FromProbes(area, nx, ny, new[] { -5.0, -4.9, -4.8, -4.75, -4.85, -4.95 }, DateTime.Now);
        Assert.Equal(new[] { 0, 0.1, 0.2, 0.05, 0.15, 0.25 }, map.Heights.Select(h => Math.Round(h, 9)));
    }

    [Fact]
    public void Feed_moves_are_split_and_follow_the_surface()
    {
        var result = LevelingCompensator.Apply("G90 G21\nG0 Z1\nG0 X0 Y0\nG1 Z-0.1 F100\nG1 X20 F300 (cut)\nM5", Map(), maxSegment: 1);

        var lines = result.Gcode.Split('\n');
        Assert.Empty(result.Warnings);
        Assert.Equal("G90 G21", lines[0]);
        Assert.Equal("M5", lines[^1]);
        var cuts = lines.Where(l => l.StartsWith("G1", StringComparison.Ordinal)).Select(Words).ToList();
        // Plunge plus 20 one-millimetre pieces.
        Assert.Equal(21, cuts.Count);
        Assert.Equal(-0.1, cuts[0]['Z'], 9);
        Assert.Equal(100, cuts[0]['F']);
        Assert.Equal(300, cuts[1]['F']);
        Assert.False(cuts[2].ContainsKey('F'));
        Assert.All(cuts.Skip(1), w => Assert.Equal(-0.1 + w['X'] * 0.01, w['Z'], 6));
        Assert.Contains("(cut)", lines.First(l => l.Contains("F300", StringComparison.Ordinal)));
    }

    [Fact]
    public void Arcs_become_short_lines_on_the_same_circle()
    {
        var map = new LevelingMap { X0 = -10, Y0 = -10, StepX = 20, StepY = 20, CountX = 2, CountY = 2, Heights = new[] { 0.0, 0, 0, 0 } };

        var ij = LevelingCompensator.Apply("G0 X5 Y0 Z-0.1\nG3 X-5 Y0 I-5 J0 F200", map, maxSegment: 0.5).Gcode.Split('\n');
        var r = LevelingCompensator.Apply("G0 X5 Y0 Z-0.1\nG2 X-5 Y0 R5 F200", map, maxSegment: 0.5).Gcode.Split('\n');

        foreach (var (lines, upper) in new[] { (ij, true), (r, false) })
        {
            var points = lines.Skip(1).Select(Words).ToList();
            Assert.True(points.Count >= 31);
            Assert.All(points, w => Assert.Equal(5, Math.Sqrt(w['X'] * w['X'] + w['Y'] * w['Y']), 3));
            Assert.Equal(-5, points[^1]['X'], 9);
            // Counter-clockwise from (5,0) goes over the top; clockwise under.
            Assert.Equal(upper, points[points.Count / 2]['Y'] > 0);
        }
    }

    [Fact]
    public void Relative_lines_and_offsets_are_left_alone()
    {
        var result = LevelingCompensator.Apply("G90\nG0 X10 Y5\nG10 L20 P1 X0\nG91\nG1 X1 Z-1\nG90\nG1 Z-0.2\nG1 X40 Y5", Map(), maxSegment: 100);

        var lines = result.Gcode.Split('\n');
        Assert.Equal("G10 L20 P1 X0", lines[2]);
        Assert.Equal("G1 X1 Z-1", lines[4]);
        Assert.Single(result.Warnings, w => w.Contains("относительные", StringComparison.Ordinal));
        // G10 did not move the tracked position: the plunge is at X10.
        Assert.Equal(10, Words(lines[6])['X'], 9);
        Assert.Contains(result.Warnings, w => w.Contains("выходит за карту", StringComparison.Ordinal));
    }

    [Fact]
    public void Compensated_isolation_program_is_still_valid_for_grbl()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Machine.UseArcs = true;
        project.Stock.Origin = OriginAnchor.Drawing;
        var tool = new Tool { Kind = ToolKind.VBit, TipDiameter = 0.1, TipAngle = 30, StepDown = 0.1 };
        project.Tools.Add(tool);
        project.Contours.Add(new Contour(1, new Segment[] { new ArcSegment(new Vec2(10, 5), 2, 0, 2 * Math.PI) }));
        project.Operations.Add(new IsolationOperation { ToolId = tool.Id, Passes = 1, ContourIds = { 1 } });
        var gcode = GcodeWriter.Write("pcb", ToolpathGenerator.Generate(project), project.Machine);
        Assert.Contains("G2", gcode);

        var leveled = LevelingCompensator.Apply(gcode, Map());

        var prepared = GrblProgram.Prepare(leveled.Gcode);
        Assert.Empty(prepared.Problems);
        Assert.DoesNotContain(prepared.Lines, l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, "^G0?[23][XYZIJR]"));
        // At the circle (x 8…12) the board is 0.08…0.12 higher than at the origin.
        var cutZ = prepared.Lines.Where(l => l.Text.StartsWith("G1", StringComparison.Ordinal) && l.Text.Contains('Z', StringComparison.Ordinal))
            .Select(l => double.Parse(l.Text[(l.Text.IndexOf('Z', StringComparison.Ordinal) + 1)..].Split('F')[0], CultureInfo.InvariantCulture))
            .Where(z => z < 0).ToList();
        Assert.All(cutZ, z => Assert.InRange(z, -0.08 + 0.075, -0.08 + 0.13));
    }

    [Fact]
    public void Probing_on_the_machine_builds_the_map()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        board.Send("Grbl 1.1f ['$' for help]");
        board.Status("Idle");
        // A board tilted along X: the surface is 0.01 mm higher per mm of X (machine Z -5 at X0).
        double x = 0;
        board.Responder = line =>
        {
            if (line.Contains("X", StringComparison.Ordinal) && line.StartsWith("G", StringComparison.Ordinal) && !line.Contains("G10", StringComparison.Ordinal))
            {
                var at = line.IndexOf('X', StringComparison.Ordinal) + 1;
                var end = line.IndexOf('Y', StringComparison.Ordinal);
                x = double.Parse(line[at..end], CultureInfo.InvariantCulture);
            }

            return line.Contains("G38.2", StringComparison.Ordinal)
                ? FormattableString.Invariant($"[PRB:{x:0.000},0.000,{-5 + 0.01 * x:0.000}:1]\nok")
                : "ok";
        };

        var probe = new LevelingProbe(new Bounds2(0, 0, 20, 10), step: 10);
        controller.ProbeTouched += p => probe.AddTouch(p);
        foreach (var command in probe.Commands())
        {
            controller.SendCommand(command);
        }

        while (board.Pending > 0)
        {
            board.ProcessAll();
        }

        Assert.False(controller.IsBusy);
        Assert.True(probe.IsComplete);
        var map = probe.ToMap(DateTime.Now);
        Assert.Equal(0.2, map.HeightAt(20, 0), 6);
        Assert.Equal(0.1, map.HeightAt(10, 10), 6);
        Assert.Contains("G10L20P1Z0", board.Lines);
        Assert.Equal(6, board.Lines.Count(l => l.Contains("G38.2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Map_is_saved_with_the_project()
    {
        var project = new CamProject { LevelingMap = Map() };

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.NotNull(loaded.LevelingMap);
        Assert.Equal(Map().Heights, loaded.LevelingMap!.Heights);
        Assert.Null(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new CamProject())).LevelingMap);
    }
}
