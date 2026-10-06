using PathForge.Core.Geometry;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.Core.Tests;

/// <summary>CFF outlines (.otf) and kerning, with the fonts built by fonts/make_test_fonts.py.</summary>
public class OpenTypeTests
{
    private static string FontPath(string name) => Path.Combine(AppContext.BaseDirectory, "fonts", name);

    private static TrueTypeFont Cff() => TrueTypeFont.LoadFile(FontPath("pf-test-cff.otf"));

    private static TrueTypeFont Kern() => TrueTypeFont.LoadFile(FontPath("pf-test-kern.ttf"));

    private static int G(TrueTypeFont font, char c) => font.GlyphIndex(c);

    [Fact]
    public void Cff_font_names_metrics_and_simple_outline_are_read()
    {
        var font = Cff();

        Assert.True(font.HasCffOutlines);
        Assert.Equal("PF Test Cff", font.FamilyName);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(700, font.CapHeight);
        Assert.Equal(700, font.AdvanceWidth(G(font, 'H')));

        var path = Assert.Single(font.GetPaths(G(font, 'H')));
        Assert.Equal(new Vec2(0, 0), path.Start);
        Assert.Equal(new[] { new Vec2(600, 0), new Vec2(600, 700), new Vec2(0, 700), new Vec2(0, 0) }, path.Segments.Select(s => s.End));
        Assert.All(path.Segments, s => Assert.Equal(GlyphSegmentKind.Line, s.Kind));
    }

    [Fact]
    public void Cff_curves_and_holes_become_rings()
    {
        var font = Cff();

        var paths = font.GetPaths(G(font, 'O'));

        Assert.Equal(2, paths.Count);
        Assert.Contains(paths[0].Segments, s => s.Kind == GlyphSegmentKind.Cubic);
        foreach (var p in paths[0].Flatten(p => p, 0.5))
        {
            Assert.InRange(p.DistanceTo(new Vec2(400, 350)), 349, 351);
        }

        var outline = TextBuilder.Layout(font, new TextItem { Text = "O", HeightMm = 7 });
        Assert.Equal(2, outline.Rings.Count);
    }

    [Fact]
    public void Cff_subroutines_hint_masks_and_flex_are_followed()
    {
        var font = Cff();

        var path = Assert.Single(font.GetPaths(G(font, 'A')));

        // rmoveto 0 0; local subr: line to (350, 700); global subr: line to (700, 0); flex back to (100, 0); close.
        Assert.Equal(new Vec2(0, 0), path.Start);
        Assert.Equal(new Vec2(350, 700), path.Segments[0].End);
        Assert.Equal(new Vec2(700, 0), path.Segments[1].End);
        Assert.Equal(GlyphSegmentKind.Cubic, path.Segments[2].Kind);
        Assert.Equal(new Vec2(400, 0), path.Segments[2].End);
        Assert.Equal(new Vec2(100, 0), path.Segments[3].End);
        Assert.Equal(new Vec2(0, 0), path.Segments[^1].End);
    }

    [Fact]
    public void Gpos_kerning_reads_glyph_pairs_and_class_pairs()
    {
        var font = Cff();

        Assert.Equal(-80, font.Kerning(G(font, 'A'), G(font, 'V')));
        Assert.Equal(-60, font.Kerning(G(font, 'T'), G(font, 'o')));
        Assert.Equal(-60, font.Kerning(G(font, 'T'), G(font, 'O')));
        Assert.Equal(0, font.Kerning(G(font, 'V'), G(font, 'A')));
        Assert.Equal(0, font.Kerning(G(font, 'H'), G(font, 'H')));
        Assert.Equal(0, font.Kerning(G(font, 'A'), 0));
    }

    [Fact]
    public void Old_kern_table_is_used_by_truetype_fonts()
    {
        var font = Kern();

        Assert.False(font.HasCffOutlines);
        Assert.Equal(-100, font.Kerning(G(font, 'A'), G(font, 'V')));
        Assert.Equal(-40, font.Kerning(G(font, 'T'), G(font, 'A')));
        Assert.Equal(0, font.Kerning(G(font, 'V'), G(font, 'A')));
    }

    [Fact]
    public void Layout_moves_kerned_letters_closer_unless_turned_off()
    {
        var font = Cff();
        Bounds2 Second(bool kerning) =>
            Bounds2.Of(TextBuilder.Layout(font, new TextItem { Text = "AV", HeightMm = 7, Kerning = kerning }).Rings
                .OrderBy(r => r.Min(p => p.X)).Last());

        // A advances 700 units, kerning -80; 7 mm cap height = 700 units.
        Assert.Equal(7.0, Second(kerning: false).MinX, 6);
        Assert.Equal(6.2, Second(kerning: true).MinX, 6);

        var centered = TextBuilder.Layout(font, new TextItem { Text = "AV", HeightMm = 7, Alignment = TextAlignment.Center }).Rings
            .Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));
        Assert.Equal(-6.6, centered.MinX, 6);
    }

    [Fact]
    public void Catalog_lists_otf_fonts_too()
    {
        // Scratch folder in the test output directory: the system temp folder may deny writes
        // under restricted tokens (folder creation in %TEMP% fails with access denied).
        var folder = Path.Combine(AppContext.BaseDirectory, "pathforge-otf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(FontPath("pf-test-cff.otf"), Path.Combine(folder, "a.otf"));
            File.Copy(FontPath("pf-test-kern.ttf"), Path.Combine(folder, "b.ttf"));

            var fonts = FontCatalog.Scan(new[] { folder });

            Assert.Equal(new[] { "PF Test Cff", "PF Test Kern" }, fonts.Select(f => f.Name));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Variable_cff2_fonts_are_refused_clearly()
    {
        var data = File.ReadAllBytes(FontPath("pf-test-cff.otf"));
        var directory = System.Text.Encoding.ASCII.GetString(data, 0, 300);
        var tag = directory.IndexOf("CFF ", StringComparison.Ordinal);
        data[tag + 3] = (byte)'2';

        var error = Assert.Throws<NotSupportedException>(() => TrueTypeFont.Load(data));
        Assert.Contains("CFF2", error.Message);
    }

    /// <summary>Real fonts with CFF outlines when installed (Linux test machines): name-keyed and CID-keyed.</summary>
    [Fact]
    public void Real_otf_fonts_lay_out_text()
    {
        var loma = "/usr/share/fonts/opentype/tlwg/Loma.otf";
        if (File.Exists(loma))
        {
            var font = TrueTypeFont.LoadFile(loma);
            Assert.True(font.Kerning(G(font, 'A'), G(font, 'V')) < 0);
            var outline = TextBuilder.Layout(font, new TextItem { Text = "AVaB8", HeightMm = 10 });
            Assert.Empty(outline.MissingCharacters);
            Assert.True(outline.Rings.Count >= 7);
        }

        var unifont = "/usr/share/fonts/opentype/unifont/unifont.otf";
        if (File.Exists(unifont))
        {
            var font = TrueTypeFont.LoadFile(unifont);
            var outline = TextBuilder.Layout(font, new TextItem { Text = "Жук", HeightMm = 10 });
            Assert.Empty(outline.MissingCharacters);
            Assert.NotEmpty(outline.Rings);
        }
    }
}
