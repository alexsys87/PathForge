using System.Globalization;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class ArcTests
{
    private static (CamProject Project, Tool Tool) Project(Contour contour, bool arcs)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Machine.UseArcs = arcs;
        var tool = new Tool { Diameter = 3.175, StepDown = 1, FeedRate = 400, PlungeRate = 100, SpindleRpm = 10000 };
        project.Tools.Add(tool);
        project.Contours.Add(contour);
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 1, ContourIds = { contour.Id } });
        return (project, tool);
    }

    private static Contour Circle(double r) => new(1, new Segment[] { new ArcSegment(new Vec2(50, 50), r, 0, 2 * Math.PI) });

    private static Contour Square() => new(1, new Segment[]
    {
        new LineSegment(new Vec2(0, 0), new Vec2(20, 0)),
        new LineSegment(new Vec2(20, 0), new Vec2(20, 20)),
        new LineSegment(new Vec2(20, 20), new Vec2(0, 20)),
        new LineSegment(new Vec2(0, 20), new Vec2(0, 0)),
    });

    private static Dictionary<char, double> Words(string line)
    {
        var words = new Dictionary<char, double>();
        foreach (var token in line.Split(' ').Skip(1))
        {
            words[token[0]] = double.Parse(token[1..], CultureInfo.InvariantCulture);
        }

        return words;
    }

    [Fact]
    public void Circular_profile_is_written_with_arcs_that_grbl_accepts()
    {
        var (project, tool) = Project(Circle(10), arcs: true);
        project.Stock.Origin = OriginAnchor.Drawing;

        var lines = GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine).Split('\n');

        var arcs = lines.Where(l => l.StartsWith("G2 ", StringComparison.Ordinal) || l.StartsWith("G3 ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(arcs);
        // Climb milling outside: clockwise.
        Assert.All(arcs, a => Assert.StartsWith("G2", a));
        Assert.True(lines.Count(l => l.StartsWith("G1", StringComparison.Ordinal)) < 20);

        // Replay the program and check the radius rule GRBL applies to every arc (|r_start - r_end| <= 0.005).
        double x = 0, y = 0;
        var expectedRadius = 10 + tool.Diameter / 2;
        foreach (var line in lines.Where(l => l.StartsWith('G')))
        {
            var w = Words(line);
            var nx = w.GetValueOrDefault('X', x);
            var ny = w.GetValueOrDefault('Y', y);
            if (line.StartsWith("G2 ", StringComparison.Ordinal) || line.StartsWith("G3 ", StringComparison.Ordinal))
            {
                var cx = x + w['I'];
                var cy = y + w['J'];
                var r0 = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                var r1 = Math.Sqrt((nx - cx) * (nx - cx) + (ny - cy) * (ny - cy));
                Assert.True(Math.Abs(r0 - r1) <= 0.005, $"Radius mismatch in {line}");
                Assert.Equal(expectedRadius, r0, 2);
            }

            x = nx;
            y = ny;
        }
    }

    [Fact]
    public void Straight_contours_never_become_arcs()
    {
        var (project, _) = Project(Square(), arcs: true);
        project.Operations[0] = new ProfileOperation { ToolId = project.Tools[0].Id, Side = ProfileSide.OnLine, Depth = 1, ContourIds = { 1 } };

        var gcode = GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine);

        Assert.DoesNotContain("G2 ", gcode);
        Assert.DoesNotContain("G3 ", gcode);
    }

    [Fact]
    public void Arcs_can_be_switched_off()
    {
        var (project, _) = Project(Circle(10), arcs: false);

        var gcode = GcodeWriter.Write("p", ToolpathGenerator.Generate(project), project.Machine);

        Assert.DoesNotContain("G2 ", gcode);
        Assert.DoesNotContain("G3 ", gcode);
    }

    [Fact]
    public void Arc_fitter_reproduces_a_polyline_on_a_circle()
    {
        var points = Enumerable.Range(0, 40).Select(i => new Vec2(5, 5) + Vec2.FromPolar(3, i * Math.PI / 30)).ToList();

        var fitted = ArcFitter.Fit(points);

        var arc = Assert.Single(fitted);
        Assert.Equal(39, arc.End);
        Assert.False(arc.Clockwise);
        Assert.True(arc.Center!.Value.IsNear(new Vec2(5, 5), 1e-6));
    }
}
