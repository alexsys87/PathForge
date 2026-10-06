using PathForge.Core.Geometry;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.Core.Tests;

/// <summary>Text along an arc and text in a variable font.</summary>
public class TextArcTests
{
    private static TrueTypeFont VariableFont() => TrueTypeFont.LoadFile(Path.Combine(AppContext.BaseDirectory, "fonts", "pf-test-var.ttf"));

    [Theory]
    [InlineData(50.0)]
    [InlineData(-50.0)]
    public void Letters_stand_on_the_circle(double radius)
    {
        var item = new TextItem { Text = "HHHH", HeightMm = 10, X = 100, Y = 20, ArcRadius = radius, Alignment = TextAlignment.Center };
        var center = new Vec2(100, radius > 0 ? 20 - radius : 20 - radius);

        var outline = TextBuilder.Layout(VariableFont(), item);

        Assert.Equal(4, outline.Rings.Count);
        var distances = outline.Rings.SelectMany(r => r).Select(p => p.DistanceTo(center)).ToList();
        // Baseline on the circle, cap height away from it: outwards on top, towards the centre below.
        var (inner, outer) = radius > 0 ? (50.0, 60.0) : (40.0, 50.0);
        Assert.InRange(distances.Min(), inner - 0.02, inner + 0.02);
        Assert.InRange(distances.Max(), outer - 0.02, outer + 0.02);
        // Centred: the middle of the text (between the second and third letter) is at X.
        Assert.True(outline.Rings[1].Average(p => p.X) < 100 && outline.Rings[2].Average(p => p.X) > 100);
    }

    [Fact]
    public void Arc_text_keeps_the_letter_order_and_bends_straight_edges()
    {
        var item = new TextItem { Text = "HO", HeightMm = 10, X = 0, Y = 0, ArcRadius = 30 };

        var outline = TextBuilder.Layout(VariableFont(), item);

        // Over the top the text runs clockwise: the first letter is on the left.
        Assert.True(outline.Rings[0].Average(p => p.X) < outline.Rings[^1].Average(p => p.X));
        // The bottom edge of 'H' (straight in the font) now follows the circle: many points, all on radius 30.
        var h = outline.Rings[0];
        Assert.True(h.Count > 8);
        Assert.True(h.Count(p => Math.Abs(p.DistanceTo(new Vec2(0, -30)) - 30) < 1e-3) > 4);
    }

    [Fact]
    public void Variable_font_weight_is_applied_to_text()
    {
        var font = VariableFont();
        var light = new TextItem { Text = "H", HeightMm = 7 };
        var bold = new TextItem { Text = "H", HeightMm = 7, Variation = new Dictionary<string, double> { ["wght"] = 900 } };

        double Width(TextItem item) => TextBuilder.Layout(font, item).Rings.SelectMany(r => r).Max(p => p.X);

        // Cap height 700 units = 7 mm: 600 units → 6 mm, 1000 units → 10 mm.
        Assert.Equal(6, Width(light), 3);
        Assert.Equal(10, Width(bold), 3);
    }

    [Fact]
    public void Arc_and_variation_survive_saving()
    {
        var project = new CamProject();
        project.Texts.Add(new TextItem { ArcRadius = -25, Variation = new Dictionary<string, double> { ["wght"] = 650, ["wdth"] = 80 } });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var text = Assert.Single(copy.Texts);
        Assert.Equal(-25, text.ArcRadius);
        Assert.Equal(650, text.Variation["wght"]);
        Assert.Equal(80, text.Variation["wdth"]);
    }
}
