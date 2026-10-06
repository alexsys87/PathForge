using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.Core.Tests;

public class TextTests
{
    private static TrueTypeFont Font() => TrueTypeFont.Load(TestFontBuilder.Build());

    [Fact]
    public void Font_metrics_names_and_character_map_are_read()
    {
        var font = Font();

        Assert.Equal(TestFontBuilder.UnitsPerEm, font.UnitsPerEm);
        Assert.Equal(TestFontBuilder.CapHeight, font.CapHeight);
        Assert.Equal(900, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal(TestFontBuilder.Family, font.FamilyName);
        Assert.Equal(TestFontBuilder.Family, font.DisplayName);
        Assert.Equal(1, font.GlyphIndex(' '));
        Assert.Equal(2, font.GlyphIndex('H'));
        Assert.Equal(3, font.GlyphIndex('O'));
        Assert.Equal(4, font.GlyphIndex('A'));
        Assert.Equal(0, font.GlyphIndex('Z'));
        Assert.Equal(0, font.GlyphIndex(0x1F600));
        Assert.Equal(700, font.AdvanceWidth(2));
    }

    [Fact]
    public void Simple_glyph_points_are_decoded()
    {
        var outline = Font().GetOutline(2);

        var contour = Assert.Single(outline);
        Assert.Equal(new[] { (0.0, 0.0), (600.0, 0.0), (600.0, 700.0), (0.0, 700.0) }, contour.Select(p => (p.X, p.Y)));
        Assert.All(contour, p => Assert.True(p.OnCurve));
    }

    [Fact]
    public void Composite_glyph_combines_moved_and_scaled_components()
    {
        var outline = Font().GetOutline(4);

        Assert.Equal(2, outline.Count);
        Assert.Equal((100.0, 0.0), (outline[0][0].X, outline[0][0].Y));
        Assert.Equal((700.0, 700.0), (outline[0][2].X, outline[0][2].Y));
        // Second copy: half size, moved up by 100.
        Assert.Equal((300.0, 450.0), (outline[1][2].X, outline[1][2].Y));
    }

    [Fact]
    public void Quadratic_contour_with_only_off_curve_points_becomes_a_smooth_ring()
    {
        var outline = Font().GetOutline(3);
        var ring = TextBuilder.FlattenGlyphContour(outline[0], p => new Vec2(p.X, p.Y), 0.5);

        Assert.True(ring.Count > 16);
        foreach (var p in ring)
        {
            var r = p.DistanceTo(new Vec2(400, 400));
            Assert.InRange(r, 299.9, 300 * 1.07);
        }
    }

    [Fact]
    public void Collection_fonts_are_found_by_index()
    {
        var data = TestFontBuilder.BuildCollection();

        Assert.Equal(2, TrueTypeFont.FontCount(data));
        Assert.Equal(TestFontBuilder.Family, TrueTypeFont.Load(data, 1).FamilyName);
        Assert.Equal(2, TrueTypeFont.Load(data, 1).GlyphIndex('H'));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrueTypeFont.Load(data, 2));
    }

    [Fact]
    public void Cff_fonts_are_rejected_with_a_clear_message()
    {
        var data = TestFontBuilder.Build();
        data[0] = (byte)'O';
        data[1] = (byte)'T';
        data[2] = (byte)'T';
        data[3] = (byte)'O';

        var error = Assert.Throws<NotSupportedException>(() => TrueTypeFont.Load(data));
        Assert.Contains("CFF", error.Message);
    }

    [Fact]
    public void Text_is_scaled_to_the_cap_height_and_placed_on_the_baseline()
    {
        var item = new TextItem { Text = "H H", HeightMm = 7, X = 10, Y = 20 };

        var outline = TextBuilder.Layout(Font(), item);

        Assert.Empty(outline.MissingCharacters);
        Assert.Equal(2, outline.Rings.Count);
        var first = Bounds2.Of(outline.Rings[0]);
        var second = Bounds2.Of(outline.Rings[1]);
        Assert.Equal(10, first.MinX, 6);
        Assert.Equal(20, first.MinY, 6);
        Assert.Equal(16, first.MaxX, 6);
        Assert.Equal(27, first.MaxY, 6);
        // H advance 7 mm + space 5 mm.
        Assert.Equal(22, second.MinX, 6);
    }

    [Fact]
    public void Alignment_spacing_lines_and_mirroring_move_the_text()
    {
        var font = Font();
        Bounds2 BoundsOf(TextItem item) => TextBuilder.Layout(font, item).Rings.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));

        var centered = BoundsOf(new TextItem { Text = "HH", HeightMm = 7, Alignment = TextAlignment.Center });
        Assert.Equal(-7, centered.MinX, 6);

        var right = BoundsOf(new TextItem { Text = "H", HeightMm = 7, Alignment = TextAlignment.Right });
        Assert.Equal(-7, right.MinX, 6);

        var spaced = BoundsOf(new TextItem { Text = "HH", HeightMm = 7, LetterSpacing = 3 });
        Assert.Equal(13 + 3, spaced.MaxX, 6);

        // Line height: (900 + 200 + 100) units = 12 mm at 7 mm cap height.
        var twoLines = BoundsOf(new TextItem { Text = "H\nH", HeightMm = 7 });
        Assert.Equal(-12, twoLines.MinY, 6);

        var mirrored = BoundsOf(new TextItem { Text = "H", HeightMm = 7, X = 10, Mirrored = true });
        Assert.Equal(4, mirrored.MinX, 6);
        Assert.Equal(10, mirrored.MaxX, 6);
    }

    [Fact]
    public void Letter_with_a_hole_gives_outer_ring_and_hole_and_overlaps_are_merged()
    {
        var font = Font();

        var o = TextBuilder.Layout(font, new TextItem { Text = "O", HeightMm = 7 });
        Assert.Equal(2, o.Rings.Count);
        Assert.Single(o.Rings, r => Polyline.IsCounterClockwise(r));

        // Negative spacing makes the letters overlap: one merged outline.
        var overlapping = TextBuilder.Layout(font, new TextItem { Text = "HH", HeightMm = 7, LetterSpacing = -2 });
        Assert.Single(overlapping.Rings);
    }

    [Fact]
    public void Missing_characters_are_reported()
    {
        var outline = TextBuilder.Layout(Font(), new TextItem { Text = "HZ Ж", HeightMm = 7 });

        Assert.Equal(new[] { "Z", "Ж" }, outline.MissingCharacters);
        Assert.Single(outline.Rings);
    }

    [Fact]
    public void Editing_a_text_keeps_its_operations_pointing_at_the_new_letters()
    {
        var font = Font();
        var project = new CamProject();
        project.Contours.Add(new Contour(1, new Segment[] { new ArcSegment(new Vec2(-50, 0), 5, 0, 2 * Math.PI) }));
        var item = new TextItem { Text = "HH", HeightMm = 7 };
        project.Texts.Add(item);
        Assert.Empty(TextBuilder.Apply(project, item, font));
        var operation = new PocketOperation();
        operation.ContourIds.AddRange(project.Contours.Select(c => c.Id));
        project.Operations.Add(operation);
        Assert.Equal(3, project.Contours.Count);
        Assert.All(project.Contours.Skip(1), c => Assert.Equal(item.Id, c.TextId));

        item.Text = "HHH";
        TextBuilder.Apply(project, item, font);

        Assert.Equal(4, project.Contours.Count);
        Assert.Equal(project.Contours.Select(c => c.Id).Order(), operation.ContourIds.Order());
        Assert.Contains(1, operation.ContourIds);

        TextBuilder.Remove(project, item);
        Assert.Empty(project.Texts);
        Assert.Equal(new[] { 1 }, operation.ContourIds);
        Assert.Single(project.Contours);
    }

    [Fact]
    public void Texts_are_saved_with_the_project()
    {
        var project = new CamProject();
        var item = new TextItem { Text = "OH", HeightMm = 12.5, FontPath = "C:/Fonts/test.ttf", Alignment = TextAlignment.Center, Mirrored = true };
        project.Texts.Add(item);
        TextBuilder.Apply(project, item, Font());

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var text = Assert.Single(loaded.Texts);
        Assert.Equal("OH", text.Text);
        Assert.Equal(12.5, text.HeightMm);
        Assert.Equal(TextAlignment.Center, text.Alignment);
        Assert.True(text.Mirrored);
        Assert.Equal(TestFontBuilder.Family, text.FontName);
        Assert.Equal(project.Contours.Count, loaded.Contours.Count(c => c.TextId == item.Id));
    }

    [Fact]
    public void Font_catalog_lists_fonts_in_a_folder()
    {
        // Scratch folder in the test output directory: the system temp folder may deny writes
        // under restricted tokens (folder creation in %TEMP% fails with access denied).
        var folder = Path.Combine(AppContext.BaseDirectory, "pathforge-fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "test.ttf"), TestFontBuilder.Build());
            File.WriteAllBytes(Path.Combine(folder, "broken.ttf"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(folder, "readme.txt"), "not a font");

            var fonts = FontCatalog.Scan(new[] { folder, Path.Combine(folder, "missing") });

            var entry = Assert.Single(fonts);
            Assert.Equal(TestFontBuilder.Family, entry.Name);
            Assert.Same(FontCatalog.Load(entry.Path, entry.Index), FontCatalog.Load(entry.Path, entry.Index));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Smoke test with a real system font when one is installed (DejaVu on Linux, Arial on Windows).</summary>
    [Fact]
    public void Real_system_font_lays_out_latin_and_cyrillic_text()
    {
        var candidates = new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return;
        }

        var font = TrueTypeFont.LoadFile(path);
        var outline = TextBuilder.Layout(font, new TextItem { Text = "Ab8 Жук", HeightMm = 10 });

        Assert.Empty(outline.MissingCharacters);
        Assert.True(outline.Rings.Count >= 7);
        var bounds = outline.Rings.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));
        Assert.InRange(bounds.MaxY, 9.5, 11.5);
        Assert.InRange(bounds.MinY, -3.5, 0.5);
    }
}
