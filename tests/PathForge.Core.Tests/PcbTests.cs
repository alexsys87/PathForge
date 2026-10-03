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
    public void Excellon_altium_file_format_comment_sets_the_digits()
    {
        // Altium Designer writes the format as a comment; inch 2:5 differs from the usual 2:4 by a factor of ten.
        const string drill = "M48\n;Layer_Color=9474304\n;FILE_FORMAT=2:5\nINCH,TZ\n;TYPE=PLATED\nT1F00S00C0.03150\n%\nT01\nX12500Y-5000\nM30\n";

        var result = ExcellonReader.Read(drill);

        Assert.Empty(result.Warnings);
        var hole = Assert.Single(result.Holes);
        Assert.Equal(3.175, hole.Center.X, 6);
        Assert.Equal(-1.27, hole.Center.Y, 6);
        Assert.Equal(0.8001, hole.Diameter, 4);
    }

    [Fact]
    public void Excellon_drilled_slot_becomes_an_oblong_contour()
    {
        const string drill = "M48\nMETRIC\nT1C1.000\n%\nT1\nX10.0Y5.0G85X14.0Y5.0\nX20.0Y5.0\nM30\n";

        var result = ExcellonReader.Read(drill);

        Assert.Empty(result.Warnings);
        var slot = Assert.Single(result.Slots);
        Assert.Equal(new[] { new Vec2(10, 5), new Vec2(14, 5) }, slot.Path);
        Assert.Equal(new Vec2(20, 5), Assert.Single(result.Holes).Center);

        var contours = result.ToContours();
        var outline = Assert.Single(contours, c => !c.TryGetCircle(out _, out _));
        Assert.True(outline.IsClosed);
        var bounds = outline.GetBounds();
        Assert.Equal(9.5, bounds.MinX, 6);
        Assert.Equal(14.5, bounds.MaxX, 6);
        Assert.Equal(1, bounds.Height, 6);
        // 4 x 1 mm rectangle plus two half circles of radius 0.5.
        Assert.Equal(4 + Math.PI / 4, Math.Abs(Polyline.SignedArea(outline.Flatten(0.001))), 0.01);
    }

    [Fact]
    public void Excellon_routed_slots_follow_the_tool_path()
    {
        // KiCad oval holes and Altium routed slots: G00 to the start, M15 tool down, G01 cuts, M16 tool up.
        const string drill = """
            M48
            METRIC
            T1C1.000
            %
            G90
            G05
            T1
            G00X10.0Y5.0
            M15
            G01X14.0Y5.0
            M16
            G05
            G00X0.0Y0.0
            M15
            G01X5.0Y0.0
            G01X5.0Y5.0
            M16
            G05
            M30
            """;

        var result = ExcellonReader.Read(drill);

        Assert.Empty(result.Warnings);
        Assert.Empty(result.Holes);
        Assert.Equal(2, result.Slots.Count);
        Assert.Equal(3, result.Slots[1].Path.Count);
        var contours = result.ToContours();
        Assert.Equal(2, contours.Count);
        Assert.All(contours, c => Assert.True(c.IsClosed));
        var corner = contours[1].GetBounds();
        Assert.Equal(-0.5, corner.MinX, 0.01);
        Assert.Equal(5.5, corner.MaxX, 0.01);
        Assert.Equal(5.5, corner.MaxY, 0.01);
    }

    [Theory]
    [InlineData("Copper,L2,Bot", GerberMode.Copper, "Нижний слой меди")]
    [InlineData("Profile,NP", GerberMode.Copper, "контур платы")]
    [InlineData("Copper,L1,Top", GerberMode.Outline, "слой меди")]
    public void Gerber_x2_file_function_warns_about_the_wrong_import(string function, GerberMode mode, string expected)
    {
        var gerber = $"%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,{function}*%\n%ADD10C,1*%\nD10*\nX0Y0D02*\nX1000000Y0D01*\nM02*\n";

        var warning = Assert.Single(GerberReader.Read(gerber, mode).Warnings);

        Assert.Contains(expected, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Gerber_x2_top_copper_needs_no_warning()
    {
        var gerber = "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,1*%\nD10*\nX0Y0D03*\nM02*\n";

        Assert.Empty(GerberReader.Read(gerber, GerberMode.Copper).Warnings);
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
