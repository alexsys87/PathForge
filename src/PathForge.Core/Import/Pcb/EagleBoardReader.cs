using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.Core.Import.Pcb;

/// <summary>One layer of an Eagle board turned into contours (the contours carry their own layer names).</summary>
public sealed class EagleLayer
{
    public EagleLayer(string name, PcbFileKind kind, bool bottom, List<Contour> contours, bool routed = true)
    {
        Name = name;
        Kind = kind;
        Bottom = bottom;
        Contours = contours;
        Routed = routed;
    }

    /// <summary>Layer name in the drawing, e.g. "board.brd: Top" (drills: one layer per diameter, see the contours).</summary>
    public string Name { get; }

    public PcbFileKind Kind { get; }

    /// <summary>The layer is on the bottom side (milled mirrored).</summary>
    public bool Bottom { get; }

    /// <summary>
    /// Copper only: the side has tracks, SMD pads or pours. A one-sided board routed on the bottom has only the
    /// through-hole pads and vias on top, which are of no use for milling.
    /// </summary>
    public bool Routed { get; }

    public List<Contour> Contours { get; }
}

public sealed class EagleBoardResult
{
    /// <summary>Non-empty layers: copper, board outline, drills, then paste, solder mask and silk screen.</summary>
    public List<EagleLayer> Layers { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reader for Eagle board files (*.brd, the XML format of Eagle 6 and newer, also written by Fusion Electronics).
/// The board is turned into the same layers as a Gerber + Excellon output: top and bottom copper (tracks, pads,
/// vias and the copper pours computed from the polygons), the board outline (Dimension and Milling layers),
/// drills by diameter, paste (tCream/bCream), solder mask openings (tStop/bStop) and silk screen (tPlace/bPlace).
/// Pad sizes, mask and paste frames and clearances follow the design rules stored in the board.
/// Written from the published Eagle DTD (eagle.dtd).
/// </summary>
public static partial class EagleBoardReader
{
    /// <summary>Suffixes of the layer names this reader creates (after "file.brd: ").</summary>
    public const string TopCopper = "Top";
    public const string BottomCopper = "Bottom";
    public const string Dimension = "Dimension";

    private const int Top = 1;
    private const int Bottom = 16;
    private const int DimensionLayer = 20;
    private const int TopPlace = 21;
    private const int BottomPlace = 22;
    private const int TopStop = 29;
    private const int BottomStop = 30;
    private const int TopCream = 31;
    private const int BottomCream = 32;
    private const int TopRestrict = 41;
    private const int BottomRestrict = 42;
    private const int Milling = 46;

    /// <summary>Chord tolerance for round shapes (mm).</summary>
    private const double Tolerance = GerberApertures.Tolerance;

    /// <summary>The text is an Eagle XML document (a board or another Eagle file).</summary>
    public static bool IsEagleXml(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return head.Contains("<eagle", StringComparison.Ordinal);
    }

    /// <summary>
    /// Why a *.brd file cannot be read (binary Eagle before 6.0, a legacy KiCad board, an Eagle file that is not a
    /// board), or null when it is an Eagle XML board.
    /// </summary>
    public static string? UnsupportedReason(string content)
    {
        if (IsEagleXml(content))
        {
            return content.Contains("<board", StringComparison.Ordinal)
                ? null
                : Loc.T("файл Eagle без платы (схема или библиотека)", "an Eagle file without a board (a schematic or a library)");
        }

        if (content.StartsWith("PCBNEW-BOARD", StringComparison.Ordinal))
        {
            return Loc.T(
                "старый формат KiCad — выведите из KiCad Gerber и сверловку и добавьте их",
                "an old KiCad board — plot Gerber and drill files from KiCad and add them");
        }

        return Loc.T(
            "двоичный формат Eagle до версии 6 — откройте плату в Eagle 6 или новее и сохраните (станет XML) либо выведите Gerber",
            "the binary format of Eagle before 6.0 — open and save the board in Eagle 6 or newer (it becomes XML) or plot Gerber files");
    }

    public static EagleBoardResult ReadFile(string path) =>
        Read(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path));

    public static EagleBoardResult Read(string xml, string fileName = "board.brd")
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FormatException(Loc.T($"Повреждённый XML платы Eagle: {ex.Message}", $"Corrupt Eagle board XML: {ex.Message}"), ex);
        }

        var board = document.Root?.Element("drawing")?.Element("board")
            ?? throw new FormatException(Loc.T("В файле Eagle нет платы (<board>).", "The Eagle file has no board (<board>)."));
        var reader = new Reader(fileName);
        reader.Read(board);
        return reader.Result;
    }

    private enum ItemKind
    {
        Wire,
        Pad,
        Via,
        Other,
    }

    /// <summary>Thermal connection of a pad to a pour of its own signal: a "+" of spokes across the gap.</summary>
    private sealed record Thermal(Vec2 Center, double Angle, double HalfExtent, double SpokeWidth);

    /// <summary>Copper object on one layer. <paramref name="Net"/> tells which objects a pour may touch.</summary>
    private sealed record CopperItem(string Net, ItemKind Kind, Paths64 Region, Thermal? Thermal);

    private sealed record PolygonDef(
        string Net, int Layer, Paths64 Outline, double Width, double Isolate, int Rank, bool Orphans, bool Thermals);

    /// <summary>Package placed on the board: its transform and whether it sits on the bottom side.</summary>
    private sealed record Placement(string Element, Affine2 Transform, double Angle, bool Mirrored);

    private sealed class Reader
    {
        private readonly string _file;
        private readonly DesignRules _rules = new();
        private readonly Dictionary<string, XElement> _packages = new();
        private readonly Dictionary<string, string> _padNets = new();
        private readonly Dictionary<int, List<CopperItem>> _copper = new() { [Top] = new(), [Bottom] = new() };
        private readonly Dictionary<int, Paths64> _graphics = new();
        private readonly List<PolygonDef> _polygons = new();
        private readonly Dictionary<int, Paths64> _cutouts = new() { [Top] = new(), [Bottom] = new() };
        private readonly List<ImportedPath> _outline = new();
        private readonly Paths64 _edgeKeepout = new();
        private readonly ExcellonResult _drills = new();
        private readonly HashSet<string> _reported = new();
        private int _plainCount;
        private int _texts;
        private int _innerObjects;
        private readonly HashSet<int> _routed = new();
        private int _hatched;

        public Reader(string file)
        {
            _file = file;
        }

        public EagleBoardResult Result { get; } = new();

        public void Read(XElement board)
        {
            if (board.Element("designrules") is { } rules)
            {
                _rules.Parse(rules);
            }

            foreach (var library in board.Elements("libraries").Elements("library"))
            {
                foreach (var package in library.Elements("packages").Elements("package"))
                {
                    var name = Attr(package, "name");
                    _packages[PackageKey(Attr(library, "name"), Attr(library, "urn"), name)] = package;
                    _packages.TryAdd(PackageKey(Attr(library, "name"), "", name), package);
                }
            }

            foreach (var signal in board.Elements("signals").Elements("signal"))
            {
                var net = Attr(signal, "name");
                foreach (var contact in signal.Elements("contactref"))
                {
                    _padNets[Attr(contact, "element") + "\n" + Attr(contact, "pad")] = net;
                }
            }

            foreach (var plain in board.Elements("plain"))
            {
                ReadGraphics(plain, null, () => "\0plain" + _plainCount++);
            }

            foreach (var element in board.Elements("elements").Elements("element"))
            {
                ReadElement(element);
            }

            foreach (var signal in board.Elements("signals").Elements("signal"))
            {
                ReadSignal(signal);
            }

            Build();
        }

        private static string PackageKey(string library, string urn, string package) => library + "\n" + urn + "\n" + package;

        // ---- Elements -------------------------------------------------------------------------------

        private void ReadElement(XElement element)
        {
            var name = Attr(element, "name");
            var library = Attr(element, "library");
            var packageName = Attr(element, "package");
            if (!_packages.TryGetValue(PackageKey(library, Attr(element, "library_urn"), packageName), out var package) &&
                !_packages.TryGetValue(PackageKey(library, "", packageName), out package))
            {
                Warn(Loc.T($"Корпус {packageName} ({library}) элемента {name} не найден в плате — элемент пропущен.",
                    $"Package {packageName} ({library}) of element {name} is missing from the board — the element was skipped."));
                return;
            }

            var (angle, mirrored) = Rotation(Attr(element, "rot"));
            var transform = Affine2.Rotation(angle * Math.PI / 180);
            if (mirrored)
            {
                // Eagle "MRnn": rotate first, then flip left-right around the element origin.
                transform = Affine2.Then(transform, Affine2.Scaling(-1, 1));
            }

            transform = Affine2.Then(transform, Affine2.Translation(Num(element, "x"), Num(element, "y")));
            var placement = new Placement(name, transform, angle, mirrored);
            var partCount = 0;
            ReadGraphics(package, placement, () => "\0" + name + "#" + partCount++);

            foreach (var smd in package.Elements("smd"))
            {
                ReadSmd(smd, placement);
            }

            foreach (var pad in package.Elements("pad"))
            {
                ReadPad(pad, placement);
            }

            // Smashed names and values are texts of the element itself.
            _texts += element.Elements("attribute").Count(a =>
                Attr(a, "display") != "off" && a.Attribute("layer") is not null && IsSilkOrCopper(Int(a, "layer")));
        }

        private void ReadSmd(XElement smd, Placement placement)
        {
            var layer = MapLayer(Int(smd, "layer", Top), placement);
            if (layer is not (Top or Bottom))
            {
                _innerObjects++;
                return;
            }

            var dx = Num(smd, "dx");
            var dy = Num(smd, "dy");
            var (rot, _) = Rotation(Attr(smd, "rot"));
            var local = Affine2.Then(Affine2.Rotation(rot * Math.PI / 180), Affine2.Translation(Num(smd, "x"), Num(smd, "y")));
            var toBoard = Affine2.Then(local, placement.Transform);
            var minSide = Math.Min(dx, dy);
            var radius = Math.Max(Num(smd, "roundness") / 100 * minSide / 2,
                Math.Clamp(_rules.SmdRoundness * minSide, _rules.SmdMinRoundness, Math.Max(_rules.SmdMinRoundness, _rules.SmdMaxRoundness)));
            radius = Math.Min(radius, minSide / 2);
            Paths64 Shape(double grow) => RoundedRectangle(dx + 2 * grow, dy + 2 * grow, Math.Max(0, radius + grow), toBoard);

            var region = Shape(0);
            var net = NetOf(placement.Element, Attr(smd, "name"));
            var thermal = Attr(smd, "thermals") == "no"
                ? null
                : new Thermal(toBoard.Apply(Vec2.Zero), ToBoardAngle(rot, placement), Math.Max(dx, dy) / 2, minSide / 2);
            _copper[layer].Add(new CopperItem(net, ItemKind.Pad, region, thermal));
            _routed.Add(layer);

            if (Attr(smd, "stop") != "no")
            {
                AddGraphic(layer == Top ? TopStop : BottomStop, Shape(_rules.StopFrame(minSide)));
            }

            if (Attr(smd, "cream") != "no")
            {
                var shrink = _rules.CreamFrame(minSide);
                if (shrink < minSide / 2)
                {
                    AddGraphic(layer == Top ? TopCream : BottomCream, Shape(-shrink));
                }
            }
        }

        private void ReadPad(XElement pad, Placement placement)
        {
            var drill = Num(pad, "drill");
            var (rot, _) = Rotation(Attr(pad, "rot"));
            var local = Affine2.Then(Affine2.Rotation(rot * Math.PI / 180), Affine2.Translation(Num(pad, "x"), Num(pad, "y")));
            var toBoard = Affine2.Then(local, placement.Transform);
            var center = toBoard.Apply(Vec2.Zero);
            var net = NetOf(placement.Element, Attr(pad, "name"));
            var shape = Attr(pad, "shape", "round");
            var thermals = Attr(pad, "thermals") != "no";

            foreach (var layer in new[] { Top, Bottom })
            {
                var diameter = Math.Max(Num(pad, "diameter"), drill + 2 * _rules.PadRestring(drill, layer == Bottom));
                var layerShape = _rules.PadShape(shape, layer == Bottom);
                var length = layerShape is "long" or "offset" ? diameter * (1 + _rules.Elongation(layerShape) / 100) : diameter;
                var region = PadShape(layerShape, diameter, length, toBoard);
                var thermal = thermals ? new Thermal(center, ToBoardAngle(rot, placement), length / 2, Math.Max(drill / 2, diameter / 4)) : null;
                _copper[layer].Add(new CopperItem(net, ItemKind.Pad, region, thermal));
                if (Attr(pad, "stop") != "no")
                {
                    var stop = _rules.StopFrame(diameter);
                    AddGraphic(layer == Top ? TopStop : BottomStop, ClipperBridge.Offset(region, stop));
                }
            }

            _drills.Holes.Add(new DrillHole(center, drill));
        }

        /// <summary>Pad of a through hole: square, round, octagon, long (oblong) or offset (oblong to one side).</summary>
        private static Paths64 PadShape(string shape, double diameter, double length, Affine2 toBoard)
        {
            List<Vec2> ring;
            switch (shape)
            {
                case "square":
                    ring = GerberApertures.Rectangle(Vec2.Zero, diameter, diameter, 0);
                    break;
                case "octagon":
                    ring = GerberApertures.RegularPolygon(Vec2.Zero, diameter / 2 / Math.Cos(Math.PI / 8), 8, 22.5);
                    break;
                case "long":
                    ring = GerberApertures.Obround(length, diameter);
                    break;
                case "offset":
                    ring = GerberApertures.Obround(length, diameter).Select(p => p + new Vec2((length - diameter) / 2, 0)).ToList();
                    break;
                default:
                    ring = GerberApertures.Circle(Vec2.Zero, diameter / 2);
                    break;
            }

            return Region(ring.Select(toBoard.Apply).ToList());
        }

        /// <summary>Rectangle with rounded corners centred at the local origin, placed on the board.</summary>
        private static Paths64 RoundedRectangle(double w, double h, double radius, Affine2 toBoard)
        {
            if (w <= 0 || h <= 0)
            {
                return new Paths64();
            }

            radius = Math.Min(radius, Math.Min(w, h) / 2);
            if (radius < 1e-6)
            {
                return Region(GerberApertures.Rectangle(Vec2.Zero, w, h, 0).Select(toBoard.Apply).ToList());
            }

            var coreW = w - 2 * radius;
            var coreH = h - 2 * radius;
            if (coreW > 1e-6 && coreH > 1e-6)
            {
                var core = GerberApertures.Rectangle(Vec2.Zero, coreW, coreH, 0).Select(toBoard.Apply).ToList();
                return ClipperBridge.Offset(Region(core), radius);
            }

            // Fully rounded: an oblong (or a circle) along the longer side.
            var half = coreW > coreH ? new Vec2(coreW / 2, 0) : new Vec2(0, coreH / 2);
            return ClipperBridge.Stroke(new[] { toBoard.Apply(-half), toBoard.Apply(half) }, radius);
        }

        // ---- Signals ---------------------------------------------------------------------------------

        private void ReadSignal(XElement signal)
        {
            var net = Attr(signal, "name");
            foreach (var wire in signal.Elements("wire"))
            {
                var layer = Int(wire, "layer");
                if (layer is Top or Bottom)
                {
                    var region = WireRegion(wire, null, 0);
                    if (region.Count > 0)
                    {
                        _copper[layer].Add(new CopperItem(net, ItemKind.Wire, region, null));
                        _routed.Add(layer);
                    }
                }
                else if (layer is > Top and < Bottom)
                {
                    _innerObjects++;
                }
            }

            foreach (var via in signal.Elements("via"))
            {
                ReadVia(via, net);
            }

            foreach (var polygon in signal.Elements("polygon"))
            {
                ReadPolygon(polygon, net);
            }
        }

        private void ReadVia(XElement via, string net)
        {
            var center = new Vec2(Num(via, "x"), Num(via, "y"));
            var drill = Num(via, "drill");
            var diameter = Math.Max(Num(via, "diameter"), drill + 2 * _rules.ViaRestring(drill));
            var region = Attr(via, "shape", "round") switch
            {
                "square" => Region(GerberApertures.Rectangle(center, diameter, diameter, 0)),
                "octagon" => Region(GerberApertures.RegularPolygon(center, diameter / 2 / Math.Cos(Math.PI / 8), 8, 22.5)),
                _ => Region(GerberApertures.Circle(center, diameter / 2)),
            };

            var (from, to) = Extent(Attr(via, "extent", "1-16"));
            var thermal = _rules.ThermalsForVias ? new Thermal(center, 0, diameter / 2, Math.Max(drill / 2, diameter / 4)) : null;
            foreach (var layer in new[] { Top, Bottom })
            {
                if (layer >= from && layer <= to)
                {
                    _copper[layer].Add(new CopperItem(net, ItemKind.Via, region, thermal));
                    if (drill > _rules.ViaStopLimit || Attr(via, "alwaysstop") == "yes")
                    {
                        AddGraphic(layer == Top ? TopStop : BottomStop, ClipperBridge.Offset(region, _rules.StopFrame(diameter)));
                    }
                }
            }

            _drills.Holes.Add(new DrillHole(center, drill));
        }

        private void ReadPolygon(XElement polygon, string? net)
        {
            var layer = Int(polygon, "layer");
            if (layer is > Top and < Bottom)
            {
                _innerObjects++;
                return;
            }

            var outline = Region(PolygonRing(polygon, null));
            var width = Num(polygon, "width");
            if (Attr(polygon, "pour") == "cutout")
            {
                if (layer is Top or Bottom)
                {
                    _cutouts[layer].AddRange(ClipperBridge.Offset(outline, width / 2));
                }

                return;
            }

            if (Attr(polygon, "pour") == "hatch")
            {
                _hatched++;
            }

            if (layer is Top or Bottom && net is not null)
            {
                _polygons.Add(new PolygonDef(net, layer, outline, width, Num(polygon, "isolate"), Int(polygon, "rank", 1),
                    Attr(polygon, "orphans") == "yes", Attr(polygon, "thermals") != "no"));
            }
        }

        private static (int From, int To) Extent(string extent)
        {
            var parts = extent.Split('-');
            return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b)
                ? (Math.Min(a, b), Math.Max(a, b))
                : (Top, Bottom);
        }

        // ---- Graphics (plain and package drawings) ---------------------------------------------------

        /// <param name="placement">Package placement, or null for the board's own drawing.</param>
        /// <param name="nextNet">Name of an unconnected copper object: every one is its own net.</param>
        private void ReadGraphics(XElement owner, Placement? placement, Func<string> nextNet)
        {
            var transform = placement?.Transform ?? Affine2.Identity;
            foreach (var wire in owner.Elements("wire"))
            {
                var layer = MapLayer(Int(wire, "layer"), placement);
                if (layer is DimensionLayer or Milling)
                {
                    var segment = WireSegment(wire).Transform(transform);
                    _outline.Add(new ImportedPath(Dimension, new List<Segment> { segment }));
                    _edgeKeepout.AddRange(StrokeSegment(segment, Num(wire, "width") / 2 + _rules.CopperDimension));
                }
                else
                {
                    AddShape(layer, WireRegion(wire, placement, 0), nextNet);
                }
            }

            foreach (var circle in owner.Elements("circle"))
            {
                var layer = MapLayer(Int(circle, "layer"), placement);
                var center = transform.Apply(new Vec2(Num(circle, "x"), Num(circle, "y")));
                var radius = Num(circle, "radius");
                var width = Num(circle, "width");
                if (layer is DimensionLayer or Milling)
                {
                    var arc = new ArcSegment(center, radius, 0, 2 * Math.PI);
                    _outline.Add(new ImportedPath(Dimension, new List<Segment> { arc }));
                    _edgeKeepout.AddRange(StrokeSegment(arc, width / 2 + _rules.CopperDimension));
                    continue;
                }

                var region = width <= 0
                    ? Region(GerberApertures.Circle(center, radius))
                    : ClipperBridge.Difference(Region(GerberApertures.Circle(center, radius + width / 2)),
                        radius > width / 2 ? Region(GerberApertures.Circle(center, radius - width / 2)) : new Paths64());
                AddShape(layer, region, nextNet);
            }

            foreach (var rectangle in owner.Elements("rectangle"))
            {
                var layer = MapLayer(Int(rectangle, "layer"), placement);
                var (x1, y1, x2, y2) = (Num(rectangle, "x1"), Num(rectangle, "y1"), Num(rectangle, "x2"), Num(rectangle, "y2"));
                var (rot, _) = Rotation(Attr(rectangle, "rot"));
                var center = new Vec2((x1 + x2) / 2, (y1 + y2) / 2);
                var ring = GerberApertures.Rectangle(Vec2.Zero, Math.Abs(x2 - x1), Math.Abs(y2 - y1), rot)
                    .Select(p => transform.Apply(p + center)).ToList();
                AddShape(layer, Region(ring), nextNet);
            }

            foreach (var polygon in owner.Elements("polygon"))
            {
                var layer = MapLayer(Int(polygon, "layer"), placement);
                if (Attr(polygon, "pour") == "cutout")
                {
                    // Cutout: no pour of any signal on this layer inside it.
                    ReadPolygon(polygon, null);
                    continue;
                }

                var region = ClipperBridge.Offset(Region(PolygonRing(polygon, placement)), Num(polygon, "width") / 2);
                AddShape(layer, region, nextNet);
            }

            foreach (var hole in owner.Elements("hole"))
            {
                var center = transform.Apply(new Vec2(Num(hole, "x"), Num(hole, "y")));
                var drill = Num(hole, "drill");
                _drills.Holes.Add(new DrillHole(center, drill));
                _edgeKeepout.AddRange(Region(GerberApertures.Circle(center, drill / 2 + _rules.CopperDimension)));
            }

            _texts += owner.Elements("text").Count(t => IsSilkOrCopper(MapLayer(Int(t, "layer"), placement)));
        }

        private void AddShape(int layer, Paths64 region, Func<string> nextNet)
        {
            if (region.Count == 0)
            {
                return;
            }

            if (layer is Top or Bottom)
            {
                _copper[layer].Add(new CopperItem(nextNet(), ItemKind.Other, region, null));
                _routed.Add(layer);
            }
            else if (layer is > Top and < Bottom)
            {
                _innerObjects++;
            }
            else
            {
                AddGraphic(layer, region);
            }
        }

        private void AddGraphic(int layer, Paths64 region)
        {
            if (!_graphics.TryGetValue(layer, out var paths))
            {
                _graphics[layer] = paths = new Paths64();
            }

            paths.AddRange(region);
        }

        private static bool IsSilkOrCopper(int layer) => layer is Top or Bottom or (>= TopPlace and <= 28);

        private Paths64 WireRegion(XElement wire, Placement? placement, double grow)
        {
            var width = Num(wire, "width");
            if (width + 2 * grow <= 0)
            {
                return new Paths64();
            }

            var segment = WireSegment(wire);
            if (placement is not null)
            {
                segment = segment.Transform(placement.Transform);
            }

            return StrokeSegment(segment, width / 2 + grow);
        }

        private static Paths64 StrokeSegment(Segment segment, double radius)
        {
            if (radius <= 0)
            {
                return new Paths64();
            }

            var points = new List<Vec2> { segment.Start };
            segment.AppendPoints(points, Tolerance);
            return ClipperBridge.Stroke(points, radius);
        }

        private static Segment WireSegment(XElement wire) =>
            Edge(new Vec2(Num(wire, "x1"), Num(wire, "y1")), new Vec2(Num(wire, "x2"), Num(wire, "y2")), Num(wire, "curve"));

        /// <summary>Straight edge, or an arc of the given included angle (degrees, positive counter-clockwise).</summary>
        private static Segment Edge(Vec2 from, Vec2 to, double curveDeg) =>
            Math.Abs(curveDeg) < 1e-9 ? new LineSegment(from, to) : ArcSegment.FromBulge(from, to, Math.Tan(curveDeg * Math.PI / 180 / 4));

        /// <summary>Outline of a polygon: its vertices joined by straight edges or arcs (curve of the vertex).</summary>
        private static List<Vec2> PolygonRing(XElement polygon, Placement? placement)
        {
            var vertices = polygon.Elements("vertex").ToList();
            var ring = new List<Vec2>();
            for (var i = 0; i < vertices.Count; i++)
            {
                var a = new Vec2(Num(vertices[i], "x"), Num(vertices[i], "y"));
                var next = vertices[(i + 1) % vertices.Count];
                var b = new Vec2(Num(next, "x"), Num(next, "y"));
                Segment edge = Edge(a, b, Num(vertices[i], "curve"));
                if (placement is not null)
                {
                    edge = edge.Transform(placement.Transform);
                }

                if (ring.Count == 0)
                {
                    ring.Add(edge.Start);
                }

                edge.AppendPoints(ring, Tolerance);
            }

            if (ring.Count > 1 && ring[^1].IsNear(ring[0], 1e-9))
            {
                ring.RemoveAt(ring.Count - 1);
            }

            return ring;
        }

        // ---- Copper pours -----------------------------------------------------------------------------

        /// <summary>
        /// Eagle stores only the outline of a polygon; the copper is computed like Eagle's "Ratsnest": the outline
        /// (with the width of its contour line) minus everything of other signals grown by the clearance, minus the
        /// board edge, holes, restrict areas and cutouts; parts narrower than the line width are dropped, pads of
        /// the same signal get thermal spokes, and islands not touching the signal are removed (orphans off).
        /// </summary>
        private Paths64 Pours(int layer)
        {
            var items = _copper[layer];
            var restrict = _graphics.TryGetValue(layer == Top ? TopRestrict : BottomRestrict, out var r) ? r : new Paths64();
            var computed = new List<(string Net, Paths64 Region)>();
            var all = new Paths64();
            foreach (var polygon in _polygons.Where(p => p.Layer == layer).OrderBy(p => p.Rank))
            {
                var halfWidth = polygon.Width / 2;
                var area = ClipperBridge.Offset(polygon.Outline, halfWidth);
                var clearance = new Paths64();
                foreach (var kind in new[] { ItemKind.Wire, ItemKind.Pad, ItemKind.Via, ItemKind.Other })
                {
                    var others = new Paths64();
                    foreach (var item in items.Where(i => i.Kind == kind && i.Net != polygon.Net))
                    {
                        others.AddRange(item.Region);
                    }

                    if (others.Count > 0)
                    {
                        clearance.AddRange(ClipperBridge.Offset(ClipperBridge.Union(others), Math.Max(polygon.Isolate, _rules.Clearance(kind))));
                    }
                }

                foreach (var (net, region) in computed.Where(c => c.Net != polygon.Net))
                {
                    clearance.AddRange(ClipperBridge.Offset(region, Math.Max(polygon.Isolate, _rules.WireWire)));
                }

                clearance.AddRange(_edgeKeepout);
                clearance.AddRange(restrict);
                clearance.AddRange(_cutouts[layer]);
                var allowed = ClipperBridge.Difference(area, ClipperBridge.Union(clearance));

                // Copper is laid with a line of the polygon width: anything narrower stays empty.
                var fill = halfWidth > 1e-6
                    ? ClipperBridge.Offset(ClipperBridge.Offset(allowed, -halfWidth), halfWidth)
                    : allowed;

                var own = items.Where(i => i.Net == polygon.Net).ToList();
                if (polygon.Thermals)
                {
                    fill = AddThermals(fill, allowed, own.Where(i => i.Thermal is not null).ToList(), polygon.Width);
                }

                if (!polygon.Orphans)
                {
                    fill = WithoutOrphans(fill, own);
                }

                computed.Add((polygon.Net, fill));
                all.AddRange(fill);
            }

            return all;
        }

        /// <summary>Clears a gap around the pads of the pour's own signal and bridges it with a "+" of spokes.</summary>
        private Paths64 AddThermals(Paths64 fill, Paths64 allowed, List<CopperItem> pads, double width)
        {
            if (pads.Count == 0)
            {
                return fill;
            }

            var gaps = new Paths64();
            var spokes = new Paths64();
            foreach (var pad in pads)
            {
                var thermal = pad.Thermal!;
                gaps.AddRange(ClipperBridge.Offset(pad.Region, _rules.ThermalIsolation));
                var reach = thermal.HalfExtent + _rules.ThermalIsolation + Math.Max(width, 0.1);
                var spokeWidth = Math.Max(thermal.SpokeWidth, width);
                foreach (var angle in new[] { thermal.Angle, thermal.Angle + Math.PI / 2 })
                {
                    var along = Vec2.FromPolar(reach, angle);
                    spokes.AddRange(ClipperBridge.Stroke(new[] { thermal.Center - along, thermal.Center + along }, spokeWidth / 2, squareEnds: true));
                }
            }

            fill = ClipperBridge.Difference(fill, ClipperBridge.Union(gaps));
            // Spokes only where copper may be and only where they reach the pour.
            var bridges = ClipperBridge.Intersect(ClipperBridge.Union(spokes), allowed);
            return ClipperBridge.Union(fill, bridges);
        }

        /// <summary>Keeps the islands of a pour that touch copper of its own signal.</summary>
        private static Paths64 WithoutOrphans(Paths64 fill, List<CopperItem> own)
        {
            var signal = new Paths64();
            foreach (var item in own)
            {
                signal.AddRange(item.Region);
            }

            if (signal.Count == 0)
            {
                return new Paths64();
            }

            signal = ClipperBridge.Union(signal);
            var kept = new Paths64();
            foreach (var island in Islands(fill))
            {
                if (ClipperBridge.Area(ClipperBridge.Intersect(island, signal)) > 1e-6)
                {
                    kept.AddRange(island);
                }
            }

            return kept;
        }

        /// <summary>Separate pieces of a region, each an outer boundary with its holes.</summary>
        private static List<Paths64> Islands(Paths64 region)
        {
            var clipper = new Clipper64();
            clipper.AddSubject(region);
            var tree = new PolyTree64();
            clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
            var islands = new List<Paths64>();

            void Visit(PolyPath64 outer)
            {
                var island = new Paths64 { outer.Polygon! };
                foreach (PolyPath64 hole in outer)
                {
                    island.Add(hole.Polygon!);
                    foreach (PolyPath64 inner in hole)
                    {
                        Visit(inner);
                    }
                }

                islands.Add(island);
            }

            foreach (PolyPath64 outer in tree)
            {
                Visit(outer);
            }

            return islands;
        }

        // ---- Result -----------------------------------------------------------------------------------

        private void Build()
        {
            var copperLayers = new List<EagleLayer>();
            foreach (var (layer, name) in new[] { (Top, TopCopper), (Bottom, BottomCopper) })
            {
                var region = new Paths64();
                foreach (var item in _copper[layer])
                {
                    region.AddRange(item.Region);
                }

                var pours = Pours(layer);
                if (pours.Count > 0)
                {
                    _routed.Add(layer);
                }

                region.AddRange(pours);
                if (region.Count > 0)
                {
                    copperLayers.Add(Layer(name, PcbFileKind.Copper, layer == Bottom, ClipperBridge.Union(region), _routed.Contains(layer)));
                }
            }

            Result.Layers.AddRange(copperLayers);

            var outlineName = LayerName(Dimension);
            var outline = ContourBuilder.Build(_outline, 0.02);
            foreach (var contour in outline)
            {
                contour.Layer = outlineName;
            }

            if (outline.Count > 0)
            {
                Result.Layers.Add(new EagleLayer(outlineName, PcbFileKind.Outline, false, outline));
                if (outline.Any(c => !c.IsClosed))
                {
                    Warn(Loc.T("Контур платы (Dimension/Milling) не замкнут — проверьте его перед вырезкой.",
                        "The board outline (Dimension/Milling) is not closed — check it before cutting out."));
                }
            }
            else
            {
                Warn(Loc.T("В плате нет контура (слой 20 Dimension).", "The board has no outline (layer 20 Dimension)."));
            }

            var drills = _drills.ToContours();
            if (drills.Count > 0)
            {
                Result.Layers.Add(new EagleLayer(Loc.T("Сверловка", "Drills"), PcbFileKind.Drill, false, drills));
            }

            foreach (var (layer, name, kind) in new[]
                     {
                         (TopCream, "tCream", PcbFileKind.Paste), (BottomCream, "bCream", PcbFileKind.Paste),
                         (TopStop, "tStop", PcbFileKind.SolderMask), (BottomStop, "bStop", PcbFileKind.SolderMask),
                         (TopPlace, "tPlace", PcbFileKind.Silkscreen), (BottomPlace, "bPlace", PcbFileKind.Silkscreen),
                     })
            {
                if (_graphics.TryGetValue(layer, out var region) && region.Count > 0)
                {
                    Result.Layers.Add(Layer(name, kind, layer % 2 == 0, ClipperBridge.Union(region)));
                }
            }

            if (copperLayers.Any(l => l.Bottom && l.Routed))
            {
                Warn(Loc.T(
                    "Нижний слой меди (Bottom): перед фрезерованием отзеркальте чертёж (Файл → Печатная плата → Зеркалить по X).",
                    "Bottom copper layer: mirror the drawing before milling (File → PCB → Mirror X)."));
            }

            if (_innerObjects > 0)
            {
                Warn(Loc.T($"Внутренние слои меди (2–15) пропущены: объектов {_innerObjects}.",
                    $"Inner copper layers (2–15) were skipped: {_innerObjects} objects."));
            }

            if (_hatched > 0)
            {
                Warn(Loc.T($"Полигоны с сетчатой заливкой ({_hatched}) залиты сплошь.", $"Hatched polygons ({_hatched}) are filled solid."));
            }

            if (_texts > 0)
            {
                Warn(Loc.T(
                    $"Надписи ({_texts}) не импортированы: векторный шрифт Eagle не поддерживается. Нужные надписи добавьте командой «Текст».",
                    $"Texts ({_texts}) were not imported: the Eagle vector font is not supported. Add the texts you need with the “Text” command."));
            }
        }

        private EagleLayer Layer(string suffix, PcbFileKind kind, bool bottom, Paths64 region, bool routed = true)
        {
            var name = LayerName(suffix);
            var contours = new List<Contour>();
            foreach (var ring in ClipperBridge.FromPaths(region))
            {
                var segments = new List<Segment>(ring.Count);
                for (var k = 0; k < ring.Count; k++)
                {
                    segments.Add(new LineSegment(ring[k], ring[(k + 1) % ring.Count]));
                }

                contours.Add(new Contour(0, segments, name));
            }

            return new EagleLayer(name, kind, bottom, contours, routed);
        }

        private string LayerName(string suffix) => $"{_file}: {suffix}";

        private string NetOf(string element, string pad) =>
            _padNets.TryGetValue(element + "\n" + pad, out var net) ? net : "\0pad:" + element + "." + pad;

        /// <summary>Layer of a package object on the board: top and bottom swap when the element is mirrored.</summary>
        private static int MapLayer(int layer, Placement? placement)
        {
            if (placement is not { Mirrored: true })
            {
                return layer;
            }

            return layer switch
            {
                >= Top and <= Bottom => Top + Bottom - layer,
                >= TopPlace and <= BottomRestrict => layer % 2 == 1 ? layer + 1 : layer - 1,
                51 => 52,
                52 => 51,
                _ => layer,
            };
        }

        /// <summary>Direction of a pad's local X axis on the board (for the thermal spokes).</summary>
        private static double ToBoardAngle(double padDeg, Placement placement)
        {
            var angle = (padDeg + placement.Angle) * Math.PI / 180;
            return placement.Mirrored ? Math.PI - angle : angle;
        }

        private void Warn(string message)
        {
            if (_reported.Add(message))
            {
                Result.Warnings.Add(message);
            }
        }
    }

    /// <summary>Design rules of the board (DRC parameters) that change the shape of the copper and the masks.</summary>
    private sealed partial class DesignRules
    {
        private const double Mil = 0.0254;

        public double WireWire { get; private set; } = 8 * Mil;
        public double WirePad { get; private set; } = 8 * Mil;
        public double WireVia { get; private set; } = 8 * Mil;
        public double CopperDimension { get; private set; } = 40 * Mil;
        public double PadTop { get; private set; } = 0.25;
        public double PadBottom { get; private set; } = 0.25;
        public double MinPadTop { get; private set; } = 10 * Mil;
        public double MaxPadTop { get; private set; } = 20 * Mil;
        public double MinPadBottom { get; private set; } = 10 * Mil;
        public double MaxPadBottom { get; private set; } = 20 * Mil;
        public double ViaOuter { get; private set; } = 0.25;
        public double MinViaOuter { get; private set; } = 8 * Mil;
        public double MaxViaOuter { get; private set; } = 20 * Mil;
        public int ShapeTop { get; private set; } = -1;
        public int ShapeBottom { get; private set; } = -1;
        public double ElongationLong { get; private set; } = 100;
        public double ElongationOffset { get; private set; } = 100;
        public double StopRatio { get; private set; } = 1;
        public double MinStop { get; private set; } = 4 * Mil;
        public double MaxStop { get; private set; } = 4 * Mil;
        public double CreamRatio { get; private set; }
        public double MinCream { get; private set; }
        public double MaxCream { get; private set; }
        public double ViaStopLimit { get; private set; }
        public double SmdRoundness { get; private set; }
        public double SmdMinRoundness { get; private set; }
        public double SmdMaxRoundness { get; private set; }
        public double ThermalIsolation { get; private set; } = 10 * Mil;
        public bool ThermalsForVias { get; private set; }

        public void Parse(XElement rules)
        {
            foreach (var param in rules.Elements("param"))
            {
                var value = Attr(param, "value");
                switch (Attr(param, "name"))
                {
                    case "mdWireWire": WireWire = Distance(value, WireWire); break;
                    case "mdWirePad": WirePad = Distance(value, WirePad); break;
                    case "mdWireVia": WireVia = Distance(value, WireVia); break;
                    case "mdCopperDimension": CopperDimension = Distance(value, CopperDimension); break;
                    case "rvPadTop": PadTop = Ratio(value, PadTop); break;
                    case "rvPadBottom": PadBottom = Ratio(value, PadBottom); break;
                    case "rlMinPadTop": MinPadTop = Distance(value, MinPadTop); break;
                    case "rlMaxPadTop": MaxPadTop = Distance(value, MaxPadTop); break;
                    case "rlMinPadBottom": MinPadBottom = Distance(value, MinPadBottom); break;
                    case "rlMaxPadBottom": MaxPadBottom = Distance(value, MaxPadBottom); break;
                    case "rvViaOuter": ViaOuter = Ratio(value, ViaOuter); break;
                    case "rlMinViaOuter": MinViaOuter = Distance(value, MinViaOuter); break;
                    case "rlMaxViaOuter": MaxViaOuter = Distance(value, MaxViaOuter); break;
                    case "psTop": ShapeTop = (int)Ratio(value, ShapeTop); break;
                    case "psBottom": ShapeBottom = (int)Ratio(value, ShapeBottom); break;
                    case "psElongationLong": ElongationLong = Ratio(value, ElongationLong); break;
                    case "psElongationOffset": ElongationOffset = Ratio(value, ElongationOffset); break;
                    case "mvStopFrame": StopRatio = Ratio(value, StopRatio); break;
                    case "mlMinStopFrame": MinStop = Distance(value, MinStop); break;
                    case "mlMaxStopFrame": MaxStop = Distance(value, MaxStop); break;
                    case "mvCreamFrame": CreamRatio = Ratio(value, CreamRatio); break;
                    case "mlMinCreamFrame": MinCream = Distance(value, MinCream); break;
                    case "mlMaxCreamFrame": MaxCream = Distance(value, MaxCream); break;
                    case "mlViaStopLimit": ViaStopLimit = Distance(value, ViaStopLimit); break;
                    case "srRoundness": SmdRoundness = Ratio(value, SmdRoundness); break;
                    case "srMinRoundness": SmdMinRoundness = Distance(value, SmdMinRoundness); break;
                    case "srMaxRoundness": SmdMaxRoundness = Distance(value, SmdMaxRoundness); break;
                    case "slThermalIsolation": ThermalIsolation = Distance(value, ThermalIsolation); break;
                    case "slThermalsForVias": ThermalsForVias = Ratio(value, 0) != 0; break;
                }
            }
        }

        public double Clearance(ItemKind kind) => kind switch
        {
            ItemKind.Pad => WirePad,
            ItemKind.Via => WireVia,
            _ => WireWire,
        };

        /// <summary>Width of the copper ring around a pad hole: a share of the drill between the limits.</summary>
        public double PadRestring(double drill, bool bottom) => bottom
            ? Clamp(drill * PadBottom, MinPadBottom, MaxPadBottom)
            : Clamp(drill * PadTop, MinPadTop, MaxPadTop);

        public double ViaRestring(double drill) => Clamp(drill * ViaOuter, MinViaOuter, MaxViaOuter);

        /// <summary>Pad shape on a side: the library shape unless the design rules force one (psTop/psBottom).</summary>
        public string PadShape(string shape, bool bottom)
        {
            if (shape is "long" or "offset")
            {
                return shape;
            }

            return (bottom ? ShapeBottom : ShapeTop) switch
            {
                0 => "square",
                1 => "round",
                2 => "octagon",
                _ => shape,
            };
        }

        public double Elongation(string shape) => shape == "offset" ? ElongationOffset : ElongationLong;

        /// <summary>How much a solder mask opening is larger than the pad.</summary>
        public double StopFrame(double padSize) => Clamp(StopRatio * padSize, MinStop, MaxStop);

        /// <summary>How much a paste opening is smaller than the pad.</summary>
        public double CreamFrame(double padSize) => Clamp(CreamRatio * padSize, MinCream, MaxCream);

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, Math.Max(min, max)));

        private static double Ratio(string value, double fallback) =>
            double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        /// <summary>Distance with a unit: "8mil", "0.2mm", "0.01inch", "10mic"; a plain number is millimetres.</summary>
        private static double Distance(string value, double fallback)
        {
            var m = DistanceRegex().Match(value.Trim());
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return fallback;
            }

            return m.Groups[2].Value switch
            {
                "mil" => v * Mil,
                "inch" or "in" => v * 25.4,
                "mic" or "um" => v / 1000,
                _ => v,
            };
        }

        [GeneratedRegex(@"^([+-]?[\d.]+(?:[eE][+-]?\d+)?)\s*(mil|mm|inch|in|mic|um)?$")]
        private static partial Regex DistanceRegex();
    }

    // ---- XML helpers ------------------------------------------------------------------------------------

    private static string Attr(XElement element, string name, string fallback = "") =>
        element.Attribute(name)?.Value ?? fallback;

    private static double Num(XElement element, string name) =>
        element.Attribute(name) is { } a && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(XElement element, string name, int fallback = 0) =>
        element.Attribute(name) is { } a && int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Eagle rotation "R90", "MR180", "SR45", "SMR270.5": angle in degrees and the mirror flag.</summary>
    private static (double Angle, bool Mirrored) Rotation(string rot)
    {
        if (string.IsNullOrEmpty(rot))
        {
            return (0, false);
        }

        var mirrored = false;
        var i = 0;
        while (i < rot.Length && char.IsLetter(rot[i]))
        {
            mirrored |= rot[i] == 'M';
            i++;
        }

        return (double.TryParse(rot[i..], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) ? angle : 0, mirrored);
    }

    private static Paths64 Region(List<Vec2> ring) =>
        ring.Count < 3 ? new Paths64() : ClipperBridge.Union(ClipperBridge.ToPaths(new[] { ring }));
}
