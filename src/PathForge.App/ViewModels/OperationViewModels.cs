using PathForge.Core.Import;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Common settings of all operations.</summary>
public abstract class OperationViewModel : ModelWrapper
{
    protected OperationViewModel(Operation model, Action changed)
        : base(changed)
    {
        Model = model;
    }

    public Operation Model { get; }

    /// <summary>Short operation type shown in the list.</summary>
    public abstract string KindLabel { get; }

    public virtual string Summary => Loc.T($"{KindLabel} · {Model.Depth:0.##} мм · контуров: {Model.ContourIds.Count}", $"{KindLabel} · {Model.Depth:0.##} mm · contours: {Model.ContourIds.Count}");

    public string Name
    {
        get => Model.Name;
        set => Set(Model.Name, value, v => Model.Name = v);
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Set(Model.Enabled, value, v => Model.Enabled = v);
    }

    public string ToolId
    {
        get => Model.ToolId;
        set => Set(Model.ToolId, value ?? "", v => Model.ToolId = v);
    }

    public double Depth
    {
        get => Model.Depth;
        set => Set(Model.Depth, Math.Max(0.01, value), v => Model.Depth = v);
    }

    public double StartZ
    {
        get => Model.StartZ;
        set => Set(Model.StartZ, value, v => Model.StartZ = v);
    }

    public EntryMode Entry
    {
        get => Model.Entry;
        set => Set(Model.Entry, value, v => Model.Entry = v);
    }

    public double RampAngle
    {
        get => Model.RampAngle;
        set => Set(Model.RampAngle, Math.Clamp(value, 0.5, 45), v => Model.RampAngle = v);
    }

    public int ContourCount => Model.ContourIds.Count;

    public void SetContours(IEnumerable<int> ids)
    {
        var list = ids.Distinct().OrderBy(i => i).ToList();
        Set(Model.ContourIds, list, v => Model.ContourIds = v, nameof(ContourCount));
    }

    protected override void OnModelChanged() => OnPropertyChanged(nameof(Summary));

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ContourCount));
    }

    public static OperationViewModel Create(Operation model, Action changed) => model switch
    {
        ProfileOperation p => new ProfileOperationViewModel(p, changed),
        PocketOperation p => new PocketOperationViewModel(p, changed),
        DrillOperation d => new DrillOperationViewModel(d, changed),
        IsolationOperation i => new IsolationOperationViewModel(i, changed),
        LaserVectorOperation l => new LaserVectorOperationViewModel(l, changed),
        LaserRasterOperation r => new LaserRasterOperationViewModel(r, changed),
        ReliefOperation r => new ReliefOperationViewModel(r, changed),
        VCarveOperation v => new VCarveOperationViewModel(v, changed),
        _ => throw new NotSupportedException(model.GetType().Name),
    };
}

public sealed class ProfileOperationViewModel : OperationViewModel
{
    private readonly ProfileOperation _model;

    public ProfileOperationViewModel(ProfileOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("Контур", "Profile");

    public ProfileSide Side
    {
        get => _model.Side;
        set => Set(_model.Side, value, v => _model.Side = v);
    }

    public CutDirection Direction
    {
        get => _model.Direction;
        set => Set(_model.Direction, value, v => _model.Direction = v);
    }

    public double Allowance
    {
        get => _model.Allowance;
        set => Set(_model.Allowance, value, v => _model.Allowance = v);
    }

    public int TabCount
    {
        get => _model.TabCount;
        set => Set(_model.TabCount, Math.Clamp(value, 0, 50), v => _model.TabCount = v);
    }

    public double TabWidth
    {
        get => _model.TabWidth;
        set => Set(_model.TabWidth, Math.Max(0, value), v => _model.TabWidth = v);
    }

    public double TabHeight
    {
        get => _model.TabHeight;
        set => Set(_model.TabHeight, Math.Max(0, value), v => _model.TabHeight = v);
    }

    public LeadMode Lead
    {
        get => _model.Lead;
        set => Set(_model.Lead, value, v => _model.Lead = v);
    }

    public double LeadRadius
    {
        get => _model.LeadRadius;
        set => Set(_model.LeadRadius, Math.Clamp(value, 0.1, 50), v => _model.LeadRadius = v);
    }
}

public sealed class VCarveOperationViewModel : OperationViewModel
{
    private readonly VCarveOperation _model;

    public VCarveOperationViewModel(VCarveOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("V-карвинг", "V-carving");

    public override string Summary => Loc.T($"{KindLabel} · до {Model.Depth:0.##} мм · контуров: {Model.ContourIds.Count}", $"{KindLabel} · up to {Model.Depth:0.##} mm · contours: {Model.ContourIds.Count}");

    public double StepMm
    {
        get => _model.StepMm;
        set => Set(_model.StepMm, Math.Clamp(value, 0.01, 2), v => _model.StepMm = v);
    }

    public double FlatStepMm
    {
        get => _model.FlatStepMm;
        set => Set(_model.FlatStepMm, Math.Clamp(value, 0, 5), v => _model.FlatStepMm = v);
    }

    public CutDirection Direction
    {
        get => _model.Direction;
        set => Set(_model.Direction, value, v => _model.Direction = v);
    }
}

public sealed class PocketOperationViewModel : OperationViewModel
{
    private readonly PocketOperation _model;

    public PocketOperationViewModel(PocketOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("Карман", "Pocket");

    public CutDirection Direction
    {
        get => _model.Direction;
        set => Set(_model.Direction, value, v => _model.Direction = v);
    }

    public double Allowance
    {
        get => _model.Allowance;
        set => Set(_model.Allowance, Math.Max(0, value), v => _model.Allowance = v);
    }
}

public sealed class DrillOperationViewModel : OperationViewModel
{
    private readonly DrillOperation _model;

    public DrillOperationViewModel(DrillOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("Сверление", "Drilling");

    public double PeckDepth
    {
        get => _model.PeckDepth;
        set => Set(_model.PeckDepth, Math.Max(0, value), v => _model.PeckDepth = v);
    }
}

public sealed class IsolationOperationViewModel : OperationViewModel
{
    private readonly IsolationOperation _model;

    public IsolationOperationViewModel(IsolationOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("Изоляция", "Isolation");

    public int Passes
    {
        get => _model.Passes;
        set => Set(_model.Passes, Math.Clamp(value, 1, 20), v => _model.Passes = v);
    }

    public double OverlapPercent
    {
        get => _model.OverlapPercent;
        set => Set(_model.OverlapPercent, Math.Clamp(value, 0, 90), v => _model.OverlapPercent = v);
    }

    public CutDirection Direction
    {
        get => _model.Direction;
        set => Set(_model.Direction, value, v => _model.Direction = v);
    }
}

public sealed class LaserVectorOperationViewModel : OperationViewModel
{
    private readonly LaserVectorOperation _model;

    public LaserVectorOperationViewModel(LaserVectorOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public override string KindLabel => Loc.T("Лазер", "Laser");

    public override string Summary => Loc.T($"Лазер · {_model.PowerPercent:0}% · {_model.Speed:0} мм/мин · контуров: {_model.ContourIds.Count}", $"Laser · {_model.PowerPercent:0}% · {_model.Speed:0} mm/min · contours: {_model.ContourIds.Count}");

    public LaserVectorMode Mode
    {
        get => _model.Mode;
        set => Set(_model.Mode, value, v => _model.Mode = v);
    }

    public double PowerPercent
    {
        get => _model.PowerPercent;
        set => Set(_model.PowerPercent, Math.Clamp(value, 0, 100), v => _model.PowerPercent = v);
    }

    public double Speed
    {
        get => _model.Speed;
        set => Set(_model.Speed, Math.Max(1, value), v => _model.Speed = v);
    }

    public int Passes
    {
        get => _model.Passes;
        set => Set(_model.Passes, Math.Clamp(value, 1, 100), v => _model.Passes = v);
    }

    public double ZStepPerPass
    {
        get => _model.ZStepPerPass;
        set => Set(_model.ZStepPerPass, Math.Max(0, value), v => _model.ZStepPerPass = v);
    }

    public double FillSpacing
    {
        get => _model.FillSpacing;
        set => Set(_model.FillSpacing, Math.Max(0.01, value), v => _model.FillSpacing = v);
    }

    public double FillAngle
    {
        get => _model.FillAngle;
        set => Set(_model.FillAngle, value, v => _model.FillAngle = v);
    }
}

public sealed class LaserRasterOperationViewModel : OperationViewModel
{
    private readonly LaserRasterOperation _model;

    public LaserRasterOperationViewModel(LaserRasterOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public LaserRasterOperation RasterModel => _model;

    public override string KindLabel => Loc.T("Картинка", "Picture");

    public override string Summary => Loc.T($"Картинка · {_model.WidthMm:0.#}×{_model.HeightMm:0.#} мм · {_model.PowerMaxPercent:0}%", $"Picture · {_model.WidthMm:0.#}×{_model.HeightMm:0.#} mm · {_model.PowerMaxPercent:0}%");

    public string ImageInfo => _model.Image.Width == 0
        ? Loc.T("картинка не загружена", "no picture loaded")
        : Loc.T($"{_model.Image.SourceName}, {_model.Image.Width}×{_model.Image.Height} пикс.", $"{_model.Image.SourceName}, {_model.Image.Width}×{_model.Image.Height} px");

    public double X
    {
        get => _model.X;
        set => Set(_model.X, value, v => _model.X = v);
    }

    public double Y
    {
        get => _model.Y;
        set => Set(_model.Y, value, v => _model.Y = v);
    }

    public double WidthMm
    {
        get => _model.WidthMm;
        set => Set(_model.WidthMm, Math.Max(1, value), v => _model.WidthMm = v);
    }

    public double HeightMm => _model.HeightMm;

    public double LineInterval
    {
        get => _model.LineInterval;
        set => Set(_model.LineInterval, Math.Clamp(value, 0.02, 2), v => _model.LineInterval = v);
    }

    public RasterMode Mode
    {
        get => _model.Mode;
        set => Set(_model.Mode, value, v => _model.Mode = v);
    }

    public double PowerMinPercent
    {
        get => _model.PowerMinPercent;
        set => Set(_model.PowerMinPercent, Math.Clamp(value, 0, 100), v => _model.PowerMinPercent = v);
    }

    public double PowerMaxPercent
    {
        get => _model.PowerMaxPercent;
        set => Set(_model.PowerMaxPercent, Math.Clamp(value, 0, 100), v => _model.PowerMaxPercent = v);
    }

    public double Speed
    {
        get => _model.Speed;
        set => Set(_model.Speed, Math.Max(1, value), v => _model.Speed = v);
    }

    public bool Invert
    {
        get => _model.Invert;
        set => Set(_model.Invert, value, v => _model.Invert = v);
    }

    public bool Bidirectional
    {
        get => _model.Bidirectional;
        set => Set(_model.Bidirectional, value, v => _model.Bidirectional = v);
    }

    public double Overscan
    {
        get => _model.Overscan;
        set => Set(_model.Overscan, Math.Max(0, value), v => _model.Overscan = v);
    }

    public void ReplaceImage(GrayImage image)
    {
        Set(_model.Image, image, v => _model.Image = v, nameof(ImageInfo));
        OnPropertyChanged(nameof(HeightMm));
    }

    protected override void OnModelChanged()
    {
        base.OnModelChanged();
        OnPropertyChanged(nameof(HeightMm));
    }
}

public sealed class ReliefOperationViewModel : OperationViewModel
{
    private readonly ReliefOperation _model;

    public ReliefOperationViewModel(ReliefOperation model, Action changed)
        : base(model, changed)
    {
        _model = model;
    }

    public ReliefOperation ReliefModel => _model;

    public override string KindLabel => Loc.T("Рельеф 3D", "3D relief");

    public override string Summary => Loc.T($"Рельеф · {_model.WidthMm:0.#}×{_model.HeightMm:0.#} мм · глубина {_model.Depth:0.##}", $"Relief · {_model.WidthMm:0.#}×{_model.HeightMm:0.#} mm · depth {_model.Depth:0.##}");

    public bool IsImage => _model.Source == ReliefSource.Image;

    public string SourceInfo => _model.Source == ReliefSource.Image
        ? (_model.Image.Width == 0 ? Loc.T("картинка не загружена", "no picture loaded") : Loc.T($"{_model.Image.SourceName}, {_model.Image.Width}×{_model.Image.Height} пикс.", $"{_model.Image.SourceName}, {_model.Image.Width}×{_model.Image.Height} px"))
        : (_model.Mesh.TriangleCount == 0 ? Loc.T("модель не загружена", "no model loaded") : Loc.T($"{_model.Mesh.SourceName}, треугольников: {_model.Mesh.TriangleCount}", $"{_model.Mesh.SourceName}, triangles: {_model.Mesh.TriangleCount}"));

    public double X
    {
        get => _model.X;
        set => Set(_model.X, value, v => _model.X = v);
    }

    public double Y
    {
        get => _model.Y;
        set => Set(_model.Y, value, v => _model.Y = v);
    }

    public double WidthMm
    {
        get => _model.WidthMm;
        set => Set(_model.WidthMm, Math.Max(1, value), v => _model.WidthMm = v);
    }

    public double HeightMm => _model.HeightMm;

    public bool Invert
    {
        get => _model.Invert;
        set => Set(_model.Invert, value, v => _model.Invert = v);
    }

    public double Resolution
    {
        get => _model.Resolution;
        set => Set(_model.Resolution, Math.Clamp(value, 0.05, 2), v => _model.Resolution = v);
    }

    public double StepOverMm
    {
        get => _model.StepOverMm;
        set => Set(_model.StepOverMm, Math.Clamp(value, 0.05, 10), v => _model.StepOverMm = v);
    }

    public RasterAxis Axis
    {
        get => _model.Axis;
        set => Set(_model.Axis, value, v => _model.Axis = v);
    }

    public bool Roughing
    {
        get => _model.Roughing;
        set => Set(_model.Roughing, value, v => _model.Roughing = v);
    }

    public double RoughAllowance
    {
        get => _model.RoughAllowance;
        set => Set(_model.RoughAllowance, Math.Max(0, value), v => _model.RoughAllowance = v);
    }

    public ReliefFinishing Finishing
    {
        get => _model.Finishing;
        set => Set(_model.Finishing, value, v => _model.Finishing = v, nameof(Finishing));
    }

    public double WaterlineStepZ
    {
        get => _model.WaterlineStepZ;
        set => Set(_model.WaterlineStepZ, Math.Clamp(value, 0.02, 10), v => _model.WaterlineStepZ = v);
    }

    public CutDirection Direction
    {
        get => _model.Direction;
        set => Set(_model.Direction, value, v => _model.Direction = v);
    }

    public void ReplaceImage(GrayImage image)
    {
        Set(_model.Image, image, v => _model.Image = v, nameof(SourceInfo));
        OnPropertyChanged(nameof(HeightMm));
    }

    public void ReplaceMesh(StlMesh mesh)
    {
        Set(_model.Mesh, mesh, v => _model.Mesh = v, nameof(SourceInfo));
        OnPropertyChanged(nameof(HeightMm));
    }

    protected override void OnModelChanged()
    {
        base.OnModelChanged();
        OnPropertyChanged(nameof(HeightMm));
    }
}
