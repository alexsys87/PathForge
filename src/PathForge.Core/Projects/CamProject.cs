using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.Core.Projects;

/// <summary>Everything that is saved in a project file.</summary>
public sealed class CamProject
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string Name { get; set; } = Loc.T("Новый проект", "New project");

    /// <summary>Drawing the contours were imported from (informational).</summary>
    public string? SourceFile { get; set; }

    public List<Contour> Contours { get; set; } = new();

    /// <summary>Texts whose outlines are part of <see cref="Contours"/>.</summary>
    public List<TextItem> Texts { get; set; } = new();

    public List<Tool> Tools { get; set; } = new();

    public List<Operation> Operations { get; set; } = new();

    public MachineSettings Machine { get; set; } = new();

    public StockSettings Stock { get; set; } = new();

    /// <summary>Measured board surface for auto-levelling (null when not measured).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Leveling.LevelingMap? LevelingMap { get; set; }

    /// <summary>Layers hidden in the view (their contours cannot be selected).</summary>
    public List<string> HiddenLayers { get; set; } = new();

    /// <summary>Id for a new contour.</summary>
    public int NextContourId() => Contours.Count == 0 ? 1 : Contours.Max(c => c.Id) + 1;

    /// <summary>
    /// Bounding box of all contours in drawing coordinates. Without contours (e.g. a project that only faces the
    /// spoil board) the facing areas take their place, so that the work zero is their corner.
    /// </summary>
    public Bounds2 DrawingBounds()
    {
        if (Contours.Count > 0)
        {
            return Contours.Aggregate(Bounds2.Empty, (bounds, contour) => bounds.Union(contour.GetBounds()));
        }

        var none = new Dictionary<int, Contour>();
        return Operations.OfType<FacingOperation>().Where(f => f.Enabled)
            .Aggregate(Bounds2.Empty, (bounds, facing) => bounds.Union(facing.Area(none)));
    }

    /// <summary>New project set up for a CNC 3018 with the stock spindle and a few typical tools.</summary>
    public static CamProject CreateDefault()
    {
        var project = new CamProject { Name = Loc.T("Новый проект", "New project") };
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.LowerLeft;

        var presets = ToolPresets.Cnc3018;
        project.Tools.Add(presets.First(p => p.NameRu.StartsWith("Фреза 1-заходная Ø3,175", StringComparison.Ordinal)).Create(1));
        project.Tools.Add(presets.First(p => p.NameRu.StartsWith("Кукуруза Ø1,0", StringComparison.Ordinal)).Create(2));
        project.Tools.Add(presets.First(p => p.NameRu == "Сверло Ø3").Create(3));
        return project;
    }
}
