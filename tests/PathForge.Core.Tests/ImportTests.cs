using PathForge.Core.Geometry;
using PathForge.Core.Import;

namespace PathForge.Core.Tests;

public class ImportTests
{
    private static List<Contour> Import(string dxf, out DxfImportResult result)
    {
        result = DxfReader.Read(new StringReader(dxf));
        return ContourBuilder.Build(result.Paths);
    }

    private static List<Contour> Import(string dxf) => Import(dxf, out _);

    [Fact]
    public void Lines_in_any_order_and_direction_are_joined_into_one_closed_contour()
    {
        var dxf = new DxfBuilder()
            .Line(0, 0, 10, 0)
            .Line(10, 10, 0, 10)
            .Line(10, 0, 10, 10)
            .Line(0, 0, 0, 10) // reversed relative to the chain
            .Build();

        var contours = Import(dxf);

        var contour = Assert.Single(contours);
        Assert.True(contour.IsClosed);
        Assert.Equal(4, contour.Segments.Count);
        Assert.Equal(40, contour.Length, 6);
    }

    [Fact]
    public void Small_gaps_within_tolerance_are_closed()
    {
        var dxf = new DxfBuilder()
            .Line(0, 0, 10, 0)
            .Line(10.005, 0, 10, 10)
            .Line(10, 10, 0.004, 0)
            .Build();

        var contour = Assert.Single(Import(dxf));
        Assert.True(contour.IsClosed);
    }

    [Fact]
    public void Lines_on_different_layers_are_not_joined()
    {
        var dxf = new DxfBuilder()
            .Line(0, 0, 10, 0, "A")
            .Line(10, 0, 10, 10, "B")
            .Build();

        Assert.Equal(2, Import(dxf).Count);
    }

    [Fact]
    public void Circle_is_recognised_as_circle()
    {
        var contour = Assert.Single(Import(new DxfBuilder().Circle(5, 6, 2.5).Build()));

        Assert.True(contour.IsClosed);
        Assert.True(contour.TryGetCircle(out var center, out var radius));
        Assert.Equal(5, center.X, 9);
        Assert.Equal(6, center.Y, 9);
        Assert.Equal(2.5, radius, 9);
    }

    [Fact]
    public void Arc_angles_are_read_counter_clockwise()
    {
        var contour = Assert.Single(Import(new DxfBuilder().Arc(0, 0, 10, 0, 90).Build()));
        var arc = Assert.IsType<ArcSegment>(Assert.Single(contour.Segments));

        Assert.Equal(Math.PI / 2, arc.Sweep, 9);
        Assert.True(arc.Start.IsNear(new Vec2(10, 0), 1e-9));
        Assert.True(arc.End.IsNear(new Vec2(0, 10), 1e-9));
    }

    [Fact]
    public void Arc_crossing_zero_degrees_has_positive_sweep()
    {
        var contour = Assert.Single(Import(new DxfBuilder().Arc(0, 0, 1, 350, 10).Build()));
        var arc = Assert.IsType<ArcSegment>(contour.Segments[0]);
        Assert.Equal(20 * Math.PI / 180, arc.Sweep, 9);
    }

    [Fact]
    public void Polyline_bulge_creates_arc()
    {
        // Half circle from (0,0) to (10,0) bulging downwards (counter-clockwise), then back with a line.
        var dxf = new DxfBuilder().LwPolyline(true, (0, 0, 1), (10, 0, 0)).Build();

        var contour = Assert.Single(Import(dxf));
        Assert.True(contour.IsClosed);
        var arc = Assert.IsType<ArcSegment>(contour.Segments[0]);
        Assert.Equal(5, arc.Radius, 9);
        Assert.True(arc.Center.IsNear(new Vec2(5, 0), 1e-9));
        Assert.Equal(Math.PI, arc.Sweep, 9);
        // Counter-clockwise from (0,0) to (10,0) passes below the chord.
        Assert.True(arc.PointAt(arc.StartAngle + arc.Sweep / 2).Y < 0);
    }

    [Fact]
    public void Quarter_bulge_produces_quarter_circle()
    {
        var segment = ArcSegment.FromBulge(new Vec2(10, 0), new Vec2(0, 10), Math.Tan(Math.PI / 8));
        var arc = Assert.IsType<ArcSegment>(segment);
        Assert.True(arc.Center.IsNear(Vec2.Zero, 1e-9));
        Assert.Equal(10, arc.Radius, 9);
        Assert.True(arc.End.IsNear(new Vec2(0, 10), 1e-9));
    }

    [Fact]
    public void Inch_drawings_are_converted_to_millimetres()
    {
        var contour = Assert.Single(Import(new DxfBuilder().Units(1).Line(0, 0, 1, 0).Build()));
        Assert.Equal(25.4, contour.Length, 9);
    }

    [Fact]
    public void Block_insert_is_scaled_rotated_and_moved()
    {
        var dxf = new DxfBuilder()
            .BeginBlock("B").Line(0, 0, 1, 0).Circle(0, 0, 1).EndBlock()
            .Insert("B", 100, 50, scale: 2, rotationDeg: 90)
            .Build();

        var contours = Import(dxf);

        Assert.Equal(2, contours.Count);
        var line = contours.Single(c => !c.IsClosed);
        Assert.True(line.Start.IsNear(new Vec2(100, 50), 1e-9));
        Assert.True(line.End.IsNear(new Vec2(100, 52), 1e-9));
        var circle = contours.Single(c => c.IsClosed);
        Assert.True(circle.TryGetCircle(out var center, out var radius));
        Assert.True(center.IsNear(new Vec2(100, 50), 1e-9));
        Assert.Equal(2, radius, 9);
    }

    [Fact]
    public void Unsupported_entities_are_reported()
    {
        var dxf = new DxfBuilder().Line(0, 0, 1, 0).Raw((0, "TEXT"), (8, "0"), (1, "Hello")).Build();

        Import(dxf, out var result);

        Assert.Contains(result.Warnings, w => w.Contains("TEXT"));
    }

    [Fact]
    public void Full_ellipse_becomes_closed_contour()
    {
        var dxf = new DxfBuilder()
            .Raw((0, "ELLIPSE"), (8, "0"), (10, 0), (20, 0), (11, 10), (21, 0), (40, 0.5), (41, 0), (42, 2 * Math.PI))
            .Build();

        var contour = Assert.Single(Import(dxf));
        Assert.True(contour.IsClosed);
        var bounds = contour.GetBounds();
        Assert.Equal(20, bounds.Width, 2);
        Assert.Equal(10, bounds.Height, 2);
    }

    [Fact]
    public void Linear_spline_passes_through_control_points()
    {
        var dxf = new DxfBuilder()
            .Raw((0, "SPLINE"), (8, "0"), (70, 8), (71, 1), (72, 5), (73, 3), (74, 0),
                (40, 0), (40, 0), (40, 1), (40, 2), (40, 2),
                (10, 0), (20, 0), (10, 10), (20, 0), (10, 10), (20, 10))
            .Build();

        var contour = Assert.Single(Import(dxf));
        Assert.True(contour.Start.IsNear(new Vec2(0, 0), 1e-9));
        Assert.True(contour.End.IsNear(new Vec2(10, 10), 1e-9));
        Assert.Equal(20, contour.Length, 6);
    }
}
