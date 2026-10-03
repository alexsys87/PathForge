using PathForge.Core.Geometry;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class PcbTests
{
    private static double Area(IEnumerable<Contour> contours) =>
        contours.Sum(c => Polyline.SignedArea(c.Flatten(0.001)));

    private static Bounds2 Bounds(IEnumerable<Contour> contours) =>
        contours.Aggregate(Bounds2.Empty, (b, c) => b.Union(c.GetBounds()));

    [Fact]
    public void Gerber_tracks_and_flashes_become_copper_areas()
    {
        const string gerber = """
            G04 test*
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,1.000000*%
            %ADD11R,2.000000X1.000000*%
            G01*
            D10*
            X0Y0D02*
            X10000000Y0D01*
            D11*
            X20000000Y0D03*
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Copper);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Contours.Count);
        Assert.Equal(10 + Math.PI * 0.25 + 2, Area(result.Contours), 1);
        var bounds = Bounds(result.Contours);
        Assert.Equal(-0.5, bounds.MinX, 2);
        Assert.Equal(21, bounds.MaxX, 2);
    }

    [Fact]
    public void Gerber_region_with_clear_polarity_leaves_a_hole()
    {
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,2.000000*%
            G36*
            X0Y0D02*
            X5000000Y0D01*
            X5000000Y5000000D01*
            X0Y5000000D01*
            X0Y0D01*
            G37*
            %LPC*%
            D10*
            X2500000Y2500000D03*
            %LPD*%
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Copper);

        Assert.Equal(2, result.Contours.Count);
        Assert.Equal(25 - Math.PI, Area(result.Contours), 1);
    }

    [Fact]
    public void Gerber_aperture_macro_with_expressions_is_evaluated()
    {
        // Rounded rectangle macro in the style written by common PCB tools.
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %AMRoundRect*
            0 Rectangle with rounded corners*
            0 $1 Rounding radius*
            4,1,4,$2,$3,$4,$5,$6,$7,$8,$9,$2,$3,0*
            1,1,$1+$1,$2,$3*
            1,1,$1+$1,$4,$5*
            1,1,$1+$1,$6,$7*
            1,1,$1+$1,$8,$9*
            20,1,$1+$1,$2,$3,$4,$5,0*
            20,1,$1+$1,$4,$5,$6,$7,0*
            20,1,$1+$1,$6,$7,$8,$9,0*
            20,1,$1+$1,$8,$9,$2,$3,0*%
            %ADD20RoundRect,0.25X-0.75X-0.5X0.75X-0.5X0.75X0.5X-0.75X0.5*%
            D20*
            X10000000Y10000000D03*
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Copper);

        var contour = Assert.Single(result.Contours);
        var bounds = contour.GetBounds();
        Assert.Equal(2.0, bounds.Width, 2);
        Assert.Equal(1.5, bounds.Height, 2);
        // Round parts are polygons with a chord error of 0.005 mm, so the area is a little smaller.
        Assert.Equal(1.5 + 1.25 + Math.PI * 0.0625, Area(result.Contours), 1);
    }

    [Fact]
    public void Gerber_outline_with_arcs_becomes_one_closed_contour()
    {
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,0.100000*%
            D10*
            G75*
            G01*
            X1000000Y0D02*
            X9000000Y0D01*
            G03*
            X10000000Y1000000I0J1000000D01*
            G01*
            X10000000Y5000000D01*
            X0Y5000000D01*
            X0Y1000000D01*
            G03*
            X1000000Y0I1000000J0D01*
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Outline);

        var contour = Assert.Single(result.Contours);
        Assert.True(contour.IsClosed);
        Assert.Equal(2, contour.Segments.OfType<ArcSegment>().Count());
        var bounds = contour.GetBounds();
        Assert.Equal(10, bounds.Width, 3);
        Assert.Equal(5, bounds.Height, 3);
    }

    [Theory]
    [InlineData("%FSLAX24Y24*%", "X10000", 25.4)]
    [InlineData("%FSTAX24Y24*%", "X25", 25 * 25.4)]
    public void Gerber_number_formats_and_inches(string format, string x, double expectedMm)
    {
        var gerber = $"{format}\n%MOIN*%\n%ADD10C,0.01*%\nD10*\nX0Y0D02*\n{x}Y0D01*\nM02*";

        var result = GerberReader.Read(gerber, GerberMode.Outline);

        Assert.Equal(expectedMm, Assert.Single(result.Contours).Length, 6);
    }

    [Fact]
    public void Excellon_decimal_metric_file()
    {
        const string drill = """
            M48
            ; FORMAT={-:-/ absolute / metric / decimal}
            FMAT,2
            METRIC
            T1C0.800
            T2C1.000
            %
            G90
            G05
            T1
            X10.0Y-5.0
            X12.54Y-5.0
            T2
            X20.0Y-5.0
            T0
            M30
            """;

        var result = ExcellonReader.Read(drill);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Holes.Count);
        Assert.Equal(new Vec2(12.54, -5), result.Holes[1].Center);
        Assert.Equal(0.8, result.Holes[1].Diameter, 9);
        Assert.Equal(1.0, result.Holes[2].Diameter, 9);
        var contours = result.ToContours();
        Assert.All(contours, c => Assert.True(c.TryGetCircle(out _, out _)));
    }

    [Theory]
    [InlineData("INCH,TZ", "T1C0.0320", "X10000Y5000", 25.4, 12.7)]
    [InlineData("METRIC,LZ", "T1C0.8", "X010Y0025", 10, 2.5)]
    public void Excellon_implicit_coordinates(string units, string toolLine, string coordinates, double x, double y)
    {
        var drill = $"M48\n{units}\n{toolLine}\n%\nT1\n{coordinates}\nM30\n";

        var hole = Assert.Single(ExcellonReader.Read(drill).Holes);

        Assert.Equal(x, hole.Center.X, 6);
        Assert.Equal(y, hole.Center.Y, 6);
    }

    [Fact]
    public void Vbit_cutting_width_grows_with_depth()
    {
        var tool = new Tool { Kind = ToolKind.VBit, TipDiameter = 0.1, TipAngle = 20 };

        Assert.Equal(0.1, tool.CuttingDiameter(0), 9);
        Assert.Equal(0.1 + 2 * 0.1 * Math.Tan(10 * Math.PI / 180), tool.CuttingDiameter(0.1), 9);
        Assert.Equal(3, new Tool { Diameter = 3 }.CuttingDiameter(1), 9);
    }

    private static (CamProject Project, Tool Tool) IsolationProject(int passes, params (double X0, double X1)[] pads)
    {
        var project = new CamProject();
        var tool = new Tool { Kind = ToolKind.VBit, TipDiameter = 0.1, TipAngle = 20, StepDown = 0.2, FeedRate = 150, PlungeRate = 50 };
        project.Tools.Add(tool);
        var id = 1;
        foreach (var (x0, x1) in pads)
        {
            project.Contours.Add(new Contour(id++, new Segment[]
            {
                new LineSegment(new Vec2(x0, 0), new Vec2(x1, 0)),
                new LineSegment(new Vec2(x1, 0), new Vec2(x1, 2)),
                new LineSegment(new Vec2(x1, 2), new Vec2(x0, 2)),
                new LineSegment(new Vec2(x0, 2), new Vec2(x0, 0)),
            }));
        }

        project.Operations.Add(new IsolationOperation
        {
            Name = "Iso", ToolId = tool.Id, Passes = passes, ContourIds = project.Contours.Select(c => c.Id).ToList(),
        });
        return (project, tool);
    }

    [Fact]
    public void Isolation_runs_around_each_pad_at_the_tool_half_width()
    {
        var (project, tool) = IsolationProject(1, (0, 2), (3, 5));

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var moves = result.Toolpaths.Single().Moves;
        Assert.Equal(2, moves.Count(m => m.Kind == MoveKind.Plunge));
        Assert.All(moves.Where(m => m.Kind != MoveKind.Rapid), m => Assert.True(m.Target.Z >= -0.08 - 1e-9));
        var halfWidth = tool.CuttingDiameter(0.08) / 2;
        var cuts = moves.Where(m => m.Kind == MoveKind.Cut && m.Target.Z < 0).Select(m => m.Target.XY).ToList();
        Assert.Equal(-halfWidth, cuts.Min(p => p.X), 3);
        Assert.Equal(5 + halfWidth, cuts.Max(p => p.X), 3);
        // Climb milling around copper: clockwise like an outside profile.
        var firstLoop = cuts.Where(p => p.X < 2.5).ToList();
        Assert.False(Polyline.IsCounterClockwise(firstLoop));
    }

    [Fact]
    public void Isolation_passes_multiply_the_loops()
    {
        var (project, _) = IsolationProject(3, (0, 2), (3, 5));

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        Assert.Equal(6, moves.Count(m => m.Kind == MoveKind.Plunge));
    }

    [Fact]
    public void Isolation_warns_when_tracks_are_too_close_for_the_engraver()
    {
        var (project, _) = IsolationProject(1, (0, 2), (2.1, 4));

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("ближе"));
    }

    [Fact]
    public void Mirroring_keeps_circles_and_closure()
    {
        var circle = new Contour(1, new Segment[] { new ArcSegment(new Vec2(5, 3), 1, 0, 2 * Math.PI) });

        var mirrored = circle.Transformed(Affine2.Scaling(-1, 1));

        Assert.True(mirrored.IsClosed);
        Assert.True(mirrored.TryGetCircle(out var center, out var radius));
        Assert.Equal(-5, center.X, 9);
        Assert.Equal(1, radius, 9);
    }
}
