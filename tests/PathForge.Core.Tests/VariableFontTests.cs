using PathForge.Core.Geometry;
using PathForge.Core.Text;

namespace PathForge.Core.Tests;

/// <summary>
/// Variable fonts built by fonts/make_test_fonts.py; expected values agree with fontTools' instancer.
/// Weight axis 100…900 with the default at 100; avar maps 500 to the design value 300 (normalized 0.25).
/// </summary>
public class VariableFontTests
{
    private static TrueTypeFont Load(string name) => TrueTypeFont.LoadFile(Path.Combine(AppContext.BaseDirectory, "fonts", name));

    private static Dictionary<string, double> Weight(double value) => new() { ["wght"] = value };

    private static Bounds2 Bounds(TrueTypeFont font, char c) =>
        font.GetPaths(font.GlyphIndex(c)).Aggregate(Bounds2.Empty, (b, path) => b.Union(Bounds2.Of(path.Flatten(p => p, 0.01))));

    [Fact]
    public void Axes_and_named_instances_are_read()
    {
        var font = Load("pf-test-var.ttf");

        Assert.True(font.IsVariable);
        var axis = Assert.Single(font.Axes);
        Assert.Equal(new FontAxis("wght", "Weight", 100, 100, 900), axis);
        Assert.Equal(new[] { "Light", "Regular", "Bold" }, font.Instances.Select(i => i.Name));
        Assert.Equal(566.667, font.Instances[1].Coordinates["wght"], 3);
        Assert.Equal(100, font.Variation["wght"]);
        Assert.False(Load("pf-test-cff.otf").IsVariable);
    }

    [Theory]
    [InlineData("pf-test-var.ttf")]
    [InlineData("pf-test-var-nohvar.ttf")]
    [InlineData("pf-test-var-cff2.otf")]
    public void Glyphs_and_advances_follow_the_weight(string file)
    {
        var font = Load(file);
        var h = font.GlyphIndex('H');

        // Default, avar-mapped middle (500 → 0.25), a point between (300 → 0.125) and the maximum.
        foreach (var (weight, width, advance) in new[] { (100.0, 600.0, 700), (300.0, 650.0, 750), (500.0, 700.0, 800), (900.0, 1000.0, 1100) })
        {
            var instance = font.WithVariation(Weight(weight));
            var bounds = Bounds(instance, 'H');
            Assert.Equal(width, bounds.Width, 6);
            Assert.Equal(700, bounds.Height, 6);
            Assert.Equal(advance, instance.AdvanceWidth(h));
        }

        Assert.Equal(825, font.WithVariation(Weight(500)).AdvanceWidth(font.GlyphIndex('O')));
    }

    [Fact]
    public void Untouched_points_are_interpolated()
    {
        var font = Load("pf-test-var.ttf").WithVariation(Weight(500));

        // gvar only stores deltas for points 1 and 3 of each contour; the others are inferred (IUP).
        var outline = font.GetOutline(font.GlyphIndex('O'));

        Assert.Equal(new[] { (712.5, 712.5), (87.5, 712.5), (87.5, 87.5), (712.5, 87.5) }, outline[0].Select(p => (p.X, p.Y)));
        Assert.Equal(new[] { (312.5, 312.5), (312.5, 487.5), (487.5, 487.5), (487.5, 312.5) }, outline[1].Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void Composite_offsets_vary_too()
    {
        var font = Load("pf-test-var.ttf").WithVariation(Weight(500));

        var o = font.GetOutline(font.GlyphIndex('O'));
        var q = font.GetOutline(font.GlyphIndex('Q'));

        // 'Q' is 'O' moved by 0 units at the default and by 100 units at the maximum: 25 units at 0.25.
        Assert.Equal(o[0].Select(p => p.X + 25), q[0].Select(p => p.X));
        Assert.Equal(825, font.AdvanceWidth(font.GlyphIndex('Q')));
    }

    [Fact]
    public void Cff2_blend_moves_the_curves()
    {
        var font = Load("pf-test-var-cff2.otf");

        // Ring radius 350 at the default, 400 at the maximum: 362.5 at 0.25, around (400, 350).
        var middle = Bounds(font.WithVariation(Weight(500)), 'O');
        var bold = Bounds(font.WithVariation(Weight(900)), 'O');

        Assert.Equal(37.5, middle.MinX, 0);
        Assert.Equal(762.5, middle.MaxX, 0);
        Assert.Equal(-12.5, middle.MinY, 0);
        Assert.Equal(0, bold.MinX, 0);
        Assert.Equal(750, bold.MaxY, 0);
    }

    [Fact]
    public void Values_outside_the_axis_are_clamped_and_static_fonts_ignore_variations()
    {
        var font = Load("pf-test-var.ttf");

        Assert.Equal(1000, Bounds(font.WithVariation(Weight(5000)), 'H').Width, 6);
        Assert.Equal(900, font.WithVariation(Weight(5000)).Variation["wght"]);
        var plain = Load("pf-test-cff.otf");
        Assert.Same(plain, plain.WithVariation(Weight(700)));
    }
}
