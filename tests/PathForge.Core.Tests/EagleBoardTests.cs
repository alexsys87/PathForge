using PathForge.Core.Geometry;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Machining;

namespace PathForge.Core.Tests;

/// <summary>Eagle board (*.brd, XML) import: copper with computed pours, outline, drills, mask, paste, silk screen.</summary>
public class EagleBoardTests
{
    private const double Mil = 0.0254;

    /// <summary>A minimal Eagle board document around the given parts of &lt;board&gt;.</summary>
    private static string Board(string plain = "", string packages = "", string elements = "", string signals = "", string rules = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE eagle SYSTEM "eagle.dtd">
        <eagle version="9.6.2"><drawing><board>
        <plain>{plain}</plain>
        <libraries><library name="lib"><packages>{packages}</packages></library></libraries>
        <designrules name="default">{rules}</designrules>
        <elements>{elements}</elements>
        <signals>{signals}</signals>
        </board></drawing></eagle>
        """;

    private const string Outline20x10 = """
        <wire x1="0" y1="0" x2="20" y2="0" width="0" layer="20"/>
        <wire x1="20" y1="0" x2="20" y2="10" width="0" layer="20"/>
        <wire x1="20" y1="10" x2="0" y2="10" width="0" layer="20"/>
        <wire x1="0" y1="10" x2="0" y2="0" width="0" layer="20"/>
        """;

    private static EagleLayer Layer(EagleBoardResult result, string suffix) =>
        result.Layers.Single(l => l.Name.EndsWith(": " + suffix, StringComparison.Ordinal));

    private static Bounds2 BoundsOf(IEnumerable<Contour> contours)
    {
        var bounds = Bounds2.Empty;
        foreach (var contour in contours)
        {
            foreach (var segment in contour.Segments)
            {
                bounds = bounds.Include(segment.Start).Include(segment.End);
            }
        }

        return bounds;
    }

    private static List<Vec2> Ring(Contour contour) => contour.Segments.Select(s => s.Start).ToList();

    /// <summary>Region of a layer (rings with the even-odd rule: holes stay holes).</summary>
    private static Clipper2Lib.Paths64 Region(EagleLayer layer) => ClipperBridge.EvenOddRegion(layer.Contours.Select(Ring));

    [Fact]
    public void Smd_IsRotatedAndMirroredLikeEagle()
    {
        // The pad sits off the origin in both axes so that R90 and MR90 put it in different places.
        const string package = """<package name="P"><smd name="1" x="1" y="0.5" dx="1" dy="0.4" layer="1"/></package>""";
        var result = EagleBoardReader.Read(Board(
            Outline20x10,
            package,
            """
            <element name="A" library="lib" package="P" x="5" y="5" rot="R90"/>
            <element name="B" library="lib" package="P" x="15" y="5" rot="MR90"/>
            """));

        // R90: (1, 0.5) → (-0.5, 1); the pad turns, 0.4 wide and 1 high.
        var top = BoundsOf(Layer(result, "Top").Contours);
        Assert.Equal(4.5, top.Center.X, 3);
        Assert.Equal(6, top.Center.Y, 3);
        Assert.Equal(0.4, top.Width, 2);
        Assert.Equal(1, top.Height, 2);

        // MR90: rotate, then flip left-right → (0.5, 1), and the pad goes to the bottom side.
        var bottom = Layer(result, "Bottom");
        Assert.True(bottom.Bottom);
        var b = BoundsOf(bottom.Contours);
        Assert.Equal(15.5, b.Center.X, 3);
        Assert.Equal(6, b.Center.Y, 3);
        Assert.Contains(result.Warnings, w => w.Contains("Зеркалить") || w.Contains("Mirror"));
    }

    [Fact]
    public void ThroughPad_SizeFollowsTheRestringRules_AndHoleIsADrill()
    {
        const string package = """<package name="P"><pad name="1" x="0" y="0" drill="0.8"/></package>""";
        var result = EagleBoardReader.Read(Board(Outline20x10, package, """<element name="A" library="lib" package="P" x="10" y="5"/>"""));

        // Restring 25 % of 0.8 = 0.2 mm, raised to the 10 mil minimum: Ø0.8 + 2 × 0.254.
        var expected = 0.8 + 2 * 10 * Mil;
        Assert.Equal(expected, BoundsOf(Layer(result, "Top").Contours).Width, 2);
        Assert.Equal(expected, BoundsOf(Layer(result, "Bottom").Contours).Width, 2);

        var drills = result.Layers.Single(l => l.Kind == PcbFileKind.Drill).Contours;
        var hole = Assert.Single(drills);
        Assert.True(hole.TryGetCircle(out var center, out var radius));
        Assert.Equal(new Vec2(10, 5), center);
        Assert.Equal(0.4, radius, 6);

        // Mask opening: the pad plus the 4 mil stop frame, on both sides.
        Assert.Equal(expected + 2 * 4 * Mil, BoundsOf(Layer(result, "tStop").Contours).Width, 2);
        Assert.Equal(expected + 2 * 4 * Mil, BoundsOf(Layer(result, "bStop").Contours).Width, 2);
    }

    [Fact]
    public void LongPad_IsElongatedAndDesignRulesAreRead()
    {
        const string package = """<package name="P"><pad name="1" x="0" y="0" drill="1" diameter="1.6" shape="long"/></package>""";
        var result = EagleBoardReader.Read(Board(
            Outline20x10,
            package,
            """<element name="A" library="lib" package="P" x="10" y="5"/>""",
            rules: """<param name="psElongationLong" value="50"/><param name="mlMinStopFrame" value="0.1mm"/><param name="mlMaxStopFrame" value="0.1mm"/>"""));

        var pad = BoundsOf(Layer(result, "Top").Contours);
        // Round ends are polygons: allow for the chords.
        Assert.Equal(2.4, pad.Width, 0.02);
        Assert.Equal(1.6, pad.Height, 0.02);
        Assert.Equal(2.6, BoundsOf(Layer(result, "tStop").Contours).Width, 0.02);
    }

    [Fact]
    public void Pour_KeepsClearance_RemovesOrphans_AndConnectsPadsWithThermals()
    {
        const string package = """<package name="P"><smd name="1" x="0" y="0" dx="1" dy="1" layer="1"/></package>""";
        // GND pour over the board, split in two by a track of another signal running across the board:
        // the lower part touches the GND pad, the upper part touches nothing of GND and is dropped.
        var result = EagleBoardReader.Read(Board(
            Outline20x10,
            package,
            """<element name="A" library="lib" package="P" x="10" y="3"/>""",
            """
            <signal name="GND">
              <contactref element="A" pad="1"/>
              <polygon width="0.2" layer="1" isolate="0.5">
                <vertex x="0" y="0"/><vertex x="20" y="0"/><vertex x="20" y="10"/><vertex x="0" y="10"/>
              </polygon>
            </signal>
            <signal name="SIG">
              <wire x1="-1" y1="6" x2="21" y2="6" width="0.4" layer="1"/>
            </signal>
            """));

        var top = Region(Layer(result, "Top"));
        // The track itself is copper; 0.5 mm isolation on each side of it is clear.
        Assert.True(ClipperBridge.Contains(top, new Vec2(10, 6)));
        Assert.False(ClipperBridge.Contains(top, new Vec2(10, 6.6)));
        Assert.False(ClipperBridge.Contains(top, new Vec2(10, 5.4)));
        // The pour below the track is there; the orphan above it is not.
        Assert.True(ClipperBridge.Contains(top, new Vec2(5, 4)));
        Assert.False(ClipperBridge.Contains(top, new Vec2(5, 8)));
        // 40 mil from the board edge stay free.
        Assert.False(ClipperBridge.Contains(top, new Vec2(5, 0.9)));
        Assert.True(ClipperBridge.Contains(top, new Vec2(5, 1.2)));
        // Thermal: a gap around the pad corner, a spoke across the gap on the pad axis.
        Assert.False(ClipperBridge.Contains(top, new Vec2(10.6, 3.6)));
        Assert.True(ClipperBridge.Contains(top, new Vec2(10.6, 3)));
        // The track crosses the whole board: it and the pour below are separate pieces of copper.
        Assert.True(Layer(result, "Top").Contours.Count >= 2);
    }

    [Fact]
    public void Pour_WithoutThermals_AndOrphansAllowed_IsSolid()
    {
        const string package = """<package name="P"><smd name="1" x="0" y="0" dx="1" dy="1" layer="1"/></package>""";
        var result = EagleBoardReader.Read(Board(
            Outline20x10,
            package,
            """<element name="A" library="lib" package="P" x="10" y="3"/>""",
            """
            <signal name="GND">
              <contactref element="A" pad="1"/>
              <polygon width="0.2" layer="1" thermals="no" orphans="yes">
                <vertex x="0" y="0"/><vertex x="20" y="0"/><vertex x="20" y="10"/><vertex x="0" y="10"/>
              </polygon>
            </signal>
            <signal name="SIG">
              <wire x1="-1" y1="6" x2="21" y2="6" width="0.4" layer="1"/>
            </signal>
            """));

        var top = Region(Layer(result, "Top"));
        Assert.True(ClipperBridge.Contains(top, new Vec2(10.6, 3.6)));
        Assert.True(ClipperBridge.Contains(top, new Vec2(5, 8)));
        // Without "isolate" the clearance is the design rule's 8 mil.
        Assert.False(ClipperBridge.Contains(top, new Vec2(10, 6.2 + 4 * Mil)));
        Assert.True(ClipperBridge.Contains(top, new Vec2(10, 6.2 + 12 * Mil)));
    }

    [Fact]
    public void OneSidedBoard_RoutedOnTheBottom_IsTold()
    {
        const string package = """<package name="P"><pad name="1" x="-1.27" y="0" drill="0.8"/><pad name="2" x="1.27" y="0" drill="0.8"/></package>""";
        var result = EagleBoardReader.Read(Board(
            Outline20x10,
            package,
            """<element name="A" library="lib" package="P" x="5" y="5"/><element name="B" library="lib" package="P" x="15" y="5"/>""",
            """
            <signal name="S">
              <contactref element="A" pad="2"/><contactref element="B" pad="1"/>
              <wire x1="6.27" y1="5" x2="13.73" y2="5" width="0.4" layer="16"/>
            </signal>
            """));

        // Both sides have the through-hole pads; only the bottom has the track.
        Assert.False(Layer(result, "Top").Routed);
        Assert.True(Layer(result, "Bottom").Routed);
        Assert.Contains(result.Warnings, w => w.Contains("Зеркалить") || w.Contains("Mirror"));
    }

    [Fact]
    public void Outline_WithArc_IsOneClosedContour()
    {
        var result = EagleBoardReader.Read(Board("""
            <wire x1="0" y1="0" x2="10" y2="0" width="0" layer="20"/>
            <wire x1="10" y1="0" x2="10" y2="7" width="0" layer="20"/>
            <wire x1="10" y1="7" x2="7" y2="10" width="0" layer="20" curve="90"/>
            <wire x1="7" y1="10" x2="0" y2="10" width="0" layer="20"/>
            <wire x1="0" y1="10" x2="0" y2="0" width="0" layer="20"/>
            <hole x="5" y="5" drill="3"/>
            """));

        var outline = Layer(result, EagleBoardReader.Dimension);
        Assert.Equal(PcbFileKind.Outline, outline.Kind);
        var contour = Assert.Single(outline.Contours);
        Assert.True(contour.IsClosed);
        var arc = Assert.Single(contour.Segments.OfType<ArcSegment>());
        Assert.Equal(new Vec2(7, 7), arc.Center);
        Assert.Equal(3, arc.Radius, 6);
        Assert.Equal(Math.PI / 2, Math.Abs(arc.Sweep), 6);

        var hole = Assert.Single(result.Layers.Single(l => l.Kind == PcbFileKind.Drill).Contours);
        Assert.True(hole.TryGetCircle(out _, out var radius));
        Assert.Equal(1.5, radius, 6);
    }

    [Fact]
    public void Smd_GetsPasteAndMask_UnlessSwitchedOff()
    {
        const string package = """
            <package name="P">
              <smd name="1" x="-2" y="0" dx="1" dy="1" layer="1"/>
              <smd name="2" x="2" y="0" dx="1" dy="1" layer="1" cream="no" stop="no"/>
              <wire x1="-3" y1="1" x2="3" y2="1" width="0.2" layer="21"/>
            </package>
            """;
        var result = EagleBoardReader.Read(Board(Outline20x10, package, """<element name="A" library="lib" package="P" x="10" y="5"/>"""));

        var paste = Layer(result, "tCream");
        Assert.Equal(PcbFileKind.Paste, paste.Kind);
        var pasteBounds = BoundsOf(paste.Contours);
        Assert.Equal(8, pasteBounds.Center.X, 3);
        Assert.Equal(1, pasteBounds.Width, 3);

        var mask = BoundsOf(Layer(result, "tStop").Contours);
        Assert.Equal(1 + 2 * 4 * Mil, mask.Width, 3);

        var silk = BoundsOf(Layer(result, "tPlace").Contours);
        Assert.Equal(6.2, silk.Width, 0.02);
        Assert.Equal(6, silk.Center.Y, 3);
    }

    [Fact]
    public void Airwires_InnerLayers_AndTexts_AreNotCopper()
    {
        const string package = """<package name="P"><smd name="1" x="0" y="0" dx="1" dy="1" layer="1"/><text x="0" y="1" size="1" layer="25">&gt;NAME</text></package>""";
        var result = EagleBoardReader.Read(Board(
            Outline20x10 + """<text x="1" y="1" size="2" layer="1">COPPER</text>""",
            package,
            """<element name="A" library="lib" package="P" x="5" y="5"/>""",
            """
            <signal name="S">
              <contactref element="A" pad="1"/>
              <wire x1="5" y1="5" x2="15" y2="5" width="0" layer="19"/>
              <wire x1="5" y1="5" x2="15" y2="5" width="0.3" layer="2"/>
            </signal>
            """));

        Assert.Single(Layer(result, "Top").Contours);
        Assert.DoesNotContain(result.Layers, l => l.Name.EndsWith(": Bottom", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("2–15"));
        Assert.Contains(result.Warnings, w => w.Contains("(2)"));
    }

    [Fact]
    public void UnsupportedFiles_AreExplained()
    {
        Assert.Null(EagleBoardReader.UnsupportedReason(Board()));
        Assert.NotNull(EagleBoardReader.UnsupportedReason("<?xml version=\"1.0\"?><eagle><drawing><schematic/></drawing></eagle>"));
        Assert.NotNull(EagleBoardReader.UnsupportedReason("PCBNEW-BOARD Version 1 date 01/01/2010"));
        Assert.NotNull(EagleBoardReader.UnsupportedReason("\u0010\u0080\u0000\u0000binary"));
        Assert.Throws<FormatException>(() => EagleBoardReader.Read("<eagle><drawing>"));
    }

    [Fact]
    public void LayerNames_AreRecognisedByThePcbCommands()
    {
        Assert.Equal(PcbFileKind.Paste, PcbFileDetector.AuxiliaryLayerKind("demo.brd: tCream"));
        Assert.Equal(PcbFileKind.SolderMask, PcbFileDetector.AuxiliaryLayerKind("demo.brd: bStop"));
        Assert.Equal(PcbFileKind.Silkscreen, PcbFileDetector.AuxiliaryLayerKind("demo.brd: tPlace"));
        Assert.Null(PcbFileDetector.AuxiliaryLayerKind("demo.brd: Top"));
        Assert.True(PcbFileDetector.IsOutlineFileName("demo.brd: Dimension"));
        Assert.False(PcbFileDetector.IsOutlineFileName("demo.brd: Bottom"));
    }

    [Fact]
    public void DemoBoard_GivesEveryLayer()
    {
        var result = EagleBoardReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "samples", "pcb-eagle", "demo.brd"));

        var kinds = result.Layers.Select(l => (l.Kind, l.Bottom)).ToList();
        Assert.Contains((PcbFileKind.Copper, false), kinds);
        Assert.Contains((PcbFileKind.Copper, true), kinds);
        Assert.Contains((PcbFileKind.Outline, false), kinds);
        Assert.Contains((PcbFileKind.Paste, false), kinds);
        Assert.Contains((PcbFileKind.Paste, true), kinds);
        Assert.Contains((PcbFileKind.SolderMask, true), kinds);
        Assert.Contains((PcbFileKind.Silkscreen, false), kinds);
        Assert.True(Assert.Single(Layer(result, EagleBoardReader.Dimension).Contours).IsClosed);

        // Pin header Ø1, LED Ø0.8, two vias Ø0.6 and two mounting holes Ø3.2.
        var holes = result.Layers.Single(l => l.Kind == PcbFileKind.Drill).Contours
            .Select(c => c.TryGetCircle(out _, out var r) ? Math.Round(2 * r, 3) : 0)
            .GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<double, int> { [0.6] = 2, [0.8] = 2, [1] = 2, [3.2] = 2 }, holes);

        // Vias with Ø0.6 are below the 100 mil via stop limit: no mask openings for them.
        var bottomMask = Region(Layer(result, "bStop"));
        Assert.False(ClipperBridge.Contains(bottomMask, new Vec2(9, 7)));
        // The GND pour on the bottom keeps its 0.4 mm isolation around the VCC pin of the header
        // (a long pad Ø1.508 × 3.016 at (4, 8.73)).
        var bottom = Region(Layer(result, "Bottom"));
        Assert.True(ClipperBridge.Contains(bottom, new Vec2(15, 15)));
        Assert.True(ClipperBridge.Contains(bottom, new Vec2(4 + 1.4, 8.73)));
        Assert.False(ClipperBridge.Contains(bottom, new Vec2(4 + 1.508 + 0.2, 8.73)));
    }
}
