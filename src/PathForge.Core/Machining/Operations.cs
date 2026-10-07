using System.Text.Json.Serialization;
using PathForge.Core.Import;

namespace PathForge.Core.Machining;

/// <summary>Where the tool runs relative to a closed contour.</summary>
public enum ProfileSide
{
    Outside,
    Inside,
    OnLine,
}

public enum CutDirection
{
    /// <summary>Climb milling (for a clockwise spindle, M3).</summary>
    Climb,
    Conventional,
}

/// <summary>How the tool goes down into the material at the start of a pass.</summary>
public enum EntryMode
{
    /// <summary>Straight down with the plunge feed.</summary>
    Plunge,

    /// <summary>Zigzag ramp along the start of the path; gentle on weak spindles and small cutters.</summary>
    Ramp,

    /// <summary>
    /// Spiral: the tool goes down continuously while running around the closed path (for a round pocket
    /// this is a true helix). Open paths and passes with tabs use the zigzag ramp instead.
    /// </summary>
    Helix,
}

/// <summary>How the tool approaches and leaves a profile.</summary>
public enum LeadMode
{
    /// <summary>Straight down onto the contour.</summary>
    None,

    /// <summary>Quarter-circle arcs from and to the free side, tangent to the contour: no mark where the cut starts.</summary>
    Arc,
}

/// <summary>One machining step applied to a set of contours with one tool.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProfileOperation), "profile")]
[JsonDerivedType(typeof(PocketOperation), "pocket")]
[JsonDerivedType(typeof(DrillOperation), "drill")]
[JsonDerivedType(typeof(IsolationOperation), "isolation")]
[JsonDerivedType(typeof(CopperClearingOperation), "copperClearing")]
[JsonDerivedType(typeof(LaserVectorOperation), "laserVector")]
[JsonDerivedType(typeof(LaserRasterOperation), "laserRaster")]
[JsonDerivedType(typeof(LaserPcbOperation), "laserPcb")]
[JsonDerivedType(typeof(ReliefOperation), "relief")]
[JsonDerivedType(typeof(VCarveOperation), "vcarve")]
[JsonDerivedType(typeof(FacingOperation), "facing")]
public abstract class Operation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string ToolId { get; set; } = "";

    /// <summary>Z of the stock surface where the operation starts (usually 0).</summary>
    public double StartZ { get; set; }

    /// <summary>Total depth below <see cref="StartZ"/>, positive.</summary>
    public double Depth { get; set; } = 3;

    public List<int> ContourIds { get; set; } = new();

    /// <summary>Entry into the material (not used by drilling).</summary>
    public EntryMode Entry { get; set; } = EntryMode.Plunge;

    /// <summary>Ramp angle to the horizontal, degrees.</summary>
    public double RampAngle { get; set; } = 3;

    /// <summary>
    /// Air assist (laser) or coolant on during this operation: M8, or M7 when the machine settings say so;
    /// M9 switches it off for the operations without it.
    /// </summary>
    public bool AirAssist { get; set; }

    [JsonIgnore]
    public double BottomZ => StartZ - Depth;
}

/// <summary>Cuts along contours: outside or inside with tool radius compensation, or exactly on the line.</summary>
public sealed class ProfileOperation : Operation
{
    public ProfileSide Side { get; set; } = ProfileSide.Outside;

    public CutDirection Direction { get; set; } = CutDirection.Climb;

    /// <summary>Material left on the wall (mm), e.g. for a later finishing pass.</summary>
    public double Allowance { get; set; }

    /// <summary>Holding tabs per closed contour (0 = none).</summary>
    public int TabCount { get; set; }

    public double TabWidth { get; set; } = 4;

    public double TabHeight { get; set; } = 1;

    /// <summary>Lead-in and lead-out (outside and inside profiles only).</summary>
    public LeadMode Lead { get; set; } = LeadMode.None;

    /// <summary>Radius of the lead arcs (mm).</summary>
    public double LeadRadius { get; set; } = 2;

    /// <summary>
    /// Rest machining by simulation: the contour is cut only where the stock left by the operations above this
    /// one (3D simulation) still stands in the way of the tool, pass by pass. Lead arcs are not used then.
    /// </summary>
    public bool RestFromStock { get; set; }

    /// <summary>Material thinner than this (mm) is not machined again by rest machining by simulation.</summary>
    public double RestTolerance { get; set; } = 0.05;
}

/// <summary>How a pocket is cleared.</summary>
public enum PocketStrategy
{
    /// <summary>Rings from the centre to the wall at the tool's step-over.</summary>
    Offset,

    /// <summary>
    /// Constant load: a trochoidal channel along the middle of the pocket, then rings growing outwards from it by a
    /// small step. The cutter never goes in with its full width, not even in corners.
    /// </summary>
    Adaptive,
}

/// <summary>Clears the whole area inside closed contours; nested contours become islands.</summary>
public sealed class PocketOperation : Operation
{
    public CutDirection Direction { get; set; } = CutDirection.Climb;

    public double Allowance { get; set; }

    public PocketStrategy Strategy { get; set; } = PocketStrategy.Offset;

    /// <summary>Adaptive clearing: width of the material taken by each pass, percent of the tool diameter.</summary>
    public double AdaptiveStepOverPercent { get; set; } = 15;

    /// <summary>
    /// Rest machining: diameter of the larger tool that already cleared this pocket (0 = off). Only what that
    /// tool could not reach (corners, narrow places) is machined.
    /// </summary>
    public double RestFromDiameter { get; set; }

    /// <summary>Allowance the larger tool left on the walls (mm).</summary>
    public double RestFromAllowance { get; set; }

    /// <summary>
    /// Rest machining by simulation: only where the stock left by the operations above this one (3D simulation)
    /// still stands above the pass. Takes precedence over <see cref="RestFromDiameter"/>.
    /// </summary>
    public bool RestFromStock { get; set; }

    /// <summary>Material thinner than this (mm) is not machined again by rest machining by simulation.</summary>
    public double RestTolerance { get; set; } = 0.05;
}

/// <summary>
/// Face milling: a rectangle is machined flat in zigzag lines (spoil board, top of the stock). The area is the
/// bounding box of the selected contours plus <see cref="Margin"/>, or X/Y/Width/Height without contours.
/// </summary>
public sealed class FacingOperation : Operation
{
    public FacingOperation()
    {
        Depth = 0.3;
    }

    /// <summary>Lower-left corner of the area when no contours are selected (drawing coordinates, mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 100;

    public double Height { get; set; } = 100;

    /// <summary>Added on every side of the selected contours' bounding box (mm).</summary>
    public double Margin { get; set; } = 2;

    /// <summary>Lines run along X or along Y.</summary>
    public RasterAxis Axis { get; set; } = RasterAxis.X;

    /// <summary>How far the cutter edge goes past the area edge, percent of the diameter (50 = the centre reaches the edge).</summary>
    public double OverhangPercent { get; set; } = 50;

    /// <summary>Area actually faced (drawing coordinates).</summary>
    public Geometry.Bounds2 Area(IReadOnlyDictionary<int, Geometry.Contour> contours)
    {
        var selected = ContourIds.Where(contours.ContainsKey).Select(id => contours[id]).ToList();
        if (selected.Count == 0)
        {
            return new Geometry.Bounds2(X, Y, X + Math.Max(0, Width), Y + Math.Max(0, Height));
        }

        var b = selected.Aggregate(Geometry.Bounds2.Empty, (bounds, c) => bounds.Union(c.GetBounds()));
        var m = Math.Max(0, Margin);
        return new Geometry.Bounds2(b.MinX - m, b.MinY - m, b.MaxX + m, b.MaxY + m);
    }
}

/// <summary>
/// V-carving with a V-bit: the tool goes the deeper the wider the shape is, so the walls stay sharp at the top
/// and the bottom becomes a V (like hand-carved letters). <see cref="Operation.Depth"/> limits the depth;
/// wider areas get a flat bottom.
/// </summary>
public sealed class VCarveOperation : Operation
{
    public VCarveOperation()
    {
        Depth = 3;
    }

    /// <summary>Distance between the offset passes (mm); smaller is smoother along the centre line.</summary>
    public double StepMm { get; set; } = 0.1;

    /// <summary>Distance between passes on a flat bottom (mm, 0 = 80 % of the tip width, at least the step).</summary>
    public double FlatStepMm { get; set; }

    public CutDirection Direction { get; set; } = CutDirection.Climb;
}

/// <summary>Drills at the centre of circles.</summary>
public sealed class DrillOperation : Operation
{
    /// <summary>Depth per peck (0 = drill in one go).</summary>
    public double PeckDepth { get; set; }

    /// <summary>Slots (oblong holes, Excellon G85): distance between the holes drilled along them, percent of the drill diameter.</summary>
    public double SlotPitchPercent { get; set; } = 40;
}

/// <summary>
/// PCB isolation milling: the engraver runs around the copper area so that tracks and pads
/// are separated from the rest of the copper.
/// </summary>
public sealed class IsolationOperation : Operation
{
    public IsolationOperation()
    {
        Depth = 0.08;
    }

    /// <summary>Number of passes around the copper; more passes give a wider gap.</summary>
    public int Passes { get; set; } = 2;

    /// <summary>Overlap of neighbouring passes, percent of the cut width.</summary>
    public double OverlapPercent { get; set; } = 40;

    public CutDirection Direction { get; set; } = CutDirection.Climb;
}

/// <summary>
/// Removes the excess copper of a PCB with a larger end mill: the whole board (outline plus margin, or the copper
/// bounds) is cleared except a band of <see cref="KeepDistance"/> around the selected copper, which the engraver's
/// isolation passes take care of. Places narrower than the mill stay as floating copper islands.
/// </summary>
public sealed class CopperClearingOperation : Operation
{
    public CopperClearingOperation()
    {
        Depth = 0.1;
    }

    /// <summary>Closed board outline contours that limit the cleared area; empty = the copper bounds.</summary>
    public List<int> BoardContourIds { get; set; } = new();

    /// <summary>Extra cleared margin beyond the board outline or the copper bounds (mm).</summary>
    public double Margin { get; set; } = 1;

    /// <summary>The mill's edge stays this far from the copper (mm): the width the isolation passes cut.</summary>
    public double KeepDistance { get; set; } = 0.4;

    public CutDirection Direction { get; set; } = CutDirection.Climb;
}

public enum LaserVectorMode
{
    /// <summary>Burn along the contour lines (cutting, line engraving).</summary>
    Line,

    /// <summary>Hatch the area inside closed contours (filled engraving).</summary>
    Fill,

    /// <summary>Fill first, then trace the outline.</summary>
    FillAndLine,
}

/// <summary>Which size a laser cut keeps when the burnt-away width (kerf) is compensated.</summary>
public enum KerfCompensation
{
    /// <summary>The beam runs exactly on the lines.</summary>
    None,

    /// <summary>Parts come out to size: outer contours move out by half the kerf, holes move in.</summary>
    Parts,

    /// <summary>Openings come out to size (inlays, sockets): the other way round.</summary>
    Openings,
}

/// <summary>How a raster picture shows grey.</summary>
public enum RasterModulation
{
    /// <summary>Darker pixels get more power at constant speed.</summary>
    Power,

    /// <summary>
    /// Constant power, darker pixels go slower: for diode lasers whose power does not follow S linearly
    /// (weak spots stay unburnt, then the beam suddenly gets too strong).
    /// </summary>
    Speed,
}

/// <summary>Laser cutting or engraving along / inside contours. Z stays at the focus height.</summary>
public sealed class LaserVectorOperation : Operation
{
    public LaserVectorOperation()
    {
        Depth = 0;
    }

    public LaserVectorMode Mode { get; set; } = LaserVectorMode.Line;

    /// <summary>Laser power, percent of the maximum.</summary>
    public double PowerPercent { get; set; } = 80;

    /// <summary>Travel speed while burning (mm/min).</summary>
    public double Speed { get; set; } = 300;

    public int Passes { get; set; } = 1;

    /// <summary>Lowering of the focus after each pass for thick material (mm, 0 = none).</summary>
    public double ZStepPerPass { get; set; }

    /// <summary>Distance between hatch lines (mm).</summary>
    public double FillSpacing { get; set; } = 0.1;

    /// <summary>Direction of the hatch lines (degrees from X).</summary>
    public double FillAngle { get; set; }

    /// <summary>Compensation of the burnt-away width for closed contours burned as lines.</summary>
    public KerfCompensation Kerf { get; set; }

    /// <summary>Width of the cut (mm): measure a cut square and subtract its size from the nominal one.</summary>
    public double KerfWidth { get; set; } = 0.15;

    /// <summary>
    /// Micro-tabs per closed outer contour burned as a line (0 = none): short gaps where the beam is off, so the
    /// part stays in the sheet and cannot drop or tilt up under the nozzle. Holes are cut without tabs.
    /// </summary>
    public int TabCount { get; set; }

    /// <summary>Material left in each micro-tab (mm); the beam spot is added to the gap.</summary>
    public double TabWidth { get; set; } = 0.5;
}

/// <summary>Which part of the paint the laser removes from a painted PCB blank.</summary>
public enum LaserPcbClearing
{
    /// <summary>A strip of the given width around the copper (like milled isolation); the rest of the copper stays.</summary>
    Isolation,

    /// <summary>All the paint except over the copper: the board is etched clean, only tracks and pads stay.</summary>
    All,

    /// <summary>
    /// Inside the selected contours: solder mask openings over the pads (the cured mask is burned away) or the
    /// silk screen markings burned into the mask. The burned edge lands on the contour.
    /// </summary>
    Inside,
}

/// <summary>
/// PCB made with a laser and etching: the copper blank is painted (black matte paint), the laser burns the
/// paint away where the copper must go, the board is washed and etched. The paint stays over the selected copper
/// contours. The beam spot is the laser tool's diameter: the beam centre stays half a spot away from the copper.
/// </summary>
public sealed class LaserPcbOperation : Operation
{
    public LaserPcbOperation()
    {
        Depth = 0;
    }

    public LaserPcbClearing Clearing { get; set; } = LaserPcbClearing.Isolation;

    /// <summary>Width of the burned strip around the copper (mm), for <see cref="LaserPcbClearing.Isolation"/>.</summary>
    public double IsolationWidth { get; set; } = 0.8;

    /// <summary>
    /// Closed board outline contours that limit <see cref="LaserPcbClearing.All"/>; empty = the copper bounds.
    /// </summary>
    public List<int> BoardContourIds { get; set; } = new();

    /// <summary>Extra cleared margin beyond the board outline or the copper bounds (mm).</summary>
    public double Margin { get; set; } = 1;

    /// <summary>Laser power, percent of the maximum.</summary>
    public double PowerPercent { get; set; } = 100;

    /// <summary>Travel speed while burning (mm/min).</summary>
    public double Speed { get; set; } = 1200;

    /// <summary>The whole pattern is burned this many times (the second pass removes what the first one left).</summary>
    public int Passes { get; set; } = 2;

    /// <summary>Distance between neighbouring burned lines (mm); must not exceed the beam spot.</summary>
    public double LineSpacing { get; set; } = 0.08;

    /// <summary>Direction of the hatch lines when all the paint is removed (degrees from X).</summary>
    public double FillAngle { get; set; }

    /// <summary>Every second pass hatches at 90° to the first one: no stripes of paint left between lines.</summary>
    public bool CrossHatch { get; set; } = true;

    /// <summary>
    /// Grows (positive) or shrinks (negative) the copper before burning (mm): positive makes up for the
    /// etchant eating the track edges and for a beam wider than set.
    /// </summary>
    public double CopperOffset { get; set; }
}

public enum RasterMode
{
    /// <summary>Power follows the brightness of each pixel.</summary>
    Grayscale,

    /// <summary>Black and white: dark pixels at full power.</summary>
    Threshold,

    /// <summary>Black and white dots that keep the impression of grey (Floyd–Steinberg).</summary>
    Dither,
}

/// <summary>Grey-scale picture: 0 = black, 255 = white, rows from top to bottom.</summary>
public sealed class GrayImage
{
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Width × Height bytes (serialized as base64).</summary>
    public byte[] Pixels { get; set; } = Array.Empty<byte>();

    public string SourceName { get; set; } = "";

    public byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>Brightness 0…1 sampled bilinearly at a point given in pixel units.</summary>
    public double Sample(double x, double y)
    {
        x = Math.Clamp(x - 0.5, 0, Width - 1);
        y = Math.Clamp(y - 0.5, 0, Height - 1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var top = this[x0, y0] * (1 - fx) + this[x1, y0] * fx;
        var bottom = this[x0, y1] * (1 - fx) + this[x1, y1] * fx;
        return (top * (1 - fy) + bottom * fy) / 255.0;
    }
}

/// <summary>Engraves a picture line by line. The picture's lower-left corner is placed at (X, Y).</summary>
public sealed class LaserRasterOperation : Operation
{
    public LaserRasterOperation()
    {
        Depth = 0;
    }

    public GrayImage Image { get; set; } = new();

    /// <summary>Position of the lower-left corner in drawing coordinates (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Engraved width (mm); the height follows the picture's aspect ratio.</summary>
    public double WidthMm { get; set; } = 50;

    [JsonIgnore]
    public double HeightMm => Image.Width == 0 ? 0 : WidthMm * Image.Height / Image.Width;

    /// <summary>Distance between lines and pixel size (mm). 0.1 mm ≈ 254 dpi.</summary>
    public double LineInterval { get; set; } = 0.1;

    public RasterMode Mode { get; set; } = RasterMode.Grayscale;

    public double PowerMinPercent { get; set; }

    public double PowerMaxPercent { get; set; } = 60;

    public double Speed { get; set; } = 1500;

    /// <summary>Grey by power (default) or by speed.</summary>
    public RasterModulation Modulation { get; set; }

    /// <summary>Speed for the darkest pixels when grey is made by speed (mm/min); <see cref="Speed"/> is the speed for the lightest burnt pixels.</summary>
    public double SpeedMin { get; set; } = 300;

    /// <summary>Burn the light parts instead of the dark ones.</summary>
    public bool Invert { get; set; }

    /// <summary>Lines run in both directions (faster) instead of always left to right.</summary>
    public bool Bidirectional { get; set; } = true;

    /// <summary>Extra travel before and after each line so that the laser burns at constant speed (mm).</summary>
    public double Overscan { get; set; } = 2;
}

public enum ReliefSource
{
    /// <summary>Grey-scale picture: brightness becomes height.</summary>
    Image,

    /// <summary>3D model from an STL file.</summary>
    Mesh,
}

/// <summary>Finishing strategy of a relief.</summary>
public enum ReliefFinishing
{
    /// <summary>Parallel lines (good on flat and gently sloped areas).</summary>
    Parallel,

    /// <summary>Contours at constant Z levels (good on steep walls).</summary>
    Waterline,

    /// <summary>Parallel lines, then waterlines.</summary>
    ParallelAndWaterline,
}

public enum RasterAxis
{
    /// <summary>Lines run along X.</summary>
    X,

    /// <summary>Lines run along Y.</summary>
    Y,
}

/// <summary>
/// 3D relief milled line by line (parallel finishing, optional roughing in levels).
/// The relief is placed with its lower-left corner at (X, Y); its top is at StartZ and the deepest point at StartZ - Depth.
/// </summary>
public sealed class ReliefOperation : Operation
{
    public ReliefSource Source { get; set; } = ReliefSource.Image;

    public GrayImage Image { get; set; } = new();

    public StlMesh Mesh { get; set; } = new();

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Relief width (mm); the length follows the picture or model proportions.</summary>
    public double WidthMm { get; set; } = 50;

    /// <summary>Picture: light parts deep instead of dark parts deep.</summary>
    public bool Invert { get; set; }

    /// <summary>Grid size of the height map (mm).</summary>
    public double Resolution { get; set; } = 0.2;

    /// <summary>Distance between finishing lines (mm).</summary>
    public double StepOverMm { get; set; } = 0.4;

    public RasterAxis Axis { get; set; } = RasterAxis.X;

    /// <summary>Remove the bulk first in levels of the tool's step-down.</summary>
    public bool Roughing { get; set; } = true;

    /// <summary>Material left by roughing for the finishing pass (mm).</summary>
    public double RoughAllowance { get; set; } = 0.3;

    public ReliefFinishing Finishing { get; set; } = ReliefFinishing.Parallel;

    /// <summary>Distance between waterline levels (mm).</summary>
    public double WaterlineStepZ { get; set; } = 0.2;

    /// <summary>Cutting direction of the waterline contours.</summary>
    public CutDirection Direction { get; set; } = CutDirection.Climb;

    /// <summary>
    /// Machine only inside the selected closed contours (the tool centre stays inside them); the rest of the
    /// relief is left untouched. Off: the whole relief rectangle.
    /// </summary>
    public bool LimitToContours { get; set; }

    /// <summary>
    /// Waterline only where the surface is steeper than this angle (degrees from the horizontal); the gentle
    /// parts are left to the parallel lines. 0 = waterline everywhere.
    /// </summary>
    public double SteepAngle { get; set; }

    /// <summary>
    /// Rest machining: the operations before this one are simulated, and this (usually smaller) tool machines only
    /// where material is left above the model — corners, narrow valleys and steps the larger tool could not reach.
    /// </summary>
    public bool RestMachining { get; set; }

    /// <summary>Material thinner than this above the model is not machined again (mm).</summary>
    public double RestTolerance { get; set; } = 0.05;

    [JsonIgnore]
    public double HeightMm
    {
        get
        {
            if (Source == ReliefSource.Image)
            {
                return Image.Width == 0 ? 0 : WidthMm * Image.Height / Image.Width;
            }

            var b = Mesh.Bounds();
            return b.MaxX - b.MinX < 1e-9 ? 0 : WidthMm * (b.MaxY - b.MinY) / (b.MaxX - b.MinX);
        }
    }
}
