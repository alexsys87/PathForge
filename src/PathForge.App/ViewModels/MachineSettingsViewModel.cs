using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

public sealed class MachineSettingsViewModel : ModelWrapper
{
    public MachineSettingsViewModel(MachineSettings model, Action changed)
        : base(changed)
    {
        Model = model;
    }

    public MachineSettings Model { get; }

    public IReadOnlyList<MachineProfile> Profiles => MachineProfiles.All;

    /// <summary>Name of the applied profile in the current language (projects store it in the language of the moment).</summary>
    public string ProfileName => string.IsNullOrEmpty(Model.ProfileName)
        ? Loc.T("свой", "custom")
        : MachineProfiles.Find(Model.ProfileName)?.Name ?? Model.ProfileName;

    /// <summary>Copies a ready-made profile into the settings (custom header/footer lines are kept).</summary>
    public void ApplyProfile(MachineProfile profile)
    {
        profile.ApplyTo(Model);
        // Every property may have changed.
        OnPropertyChanged(string.Empty);
        NotifyOwner();
    }

    public bool UseArcs
    {
        get => Model.UseArcs;
        set => Set(Model.UseArcs, value, v => Model.UseArcs = v);
    }

    public CoolantCommand AirAssistCommand
    {
        get => Model.AirAssistCommand;
        set => Set(Model.AirAssistCommand, value, v => Model.AirAssistCommand = v);
    }

    public GcodeDialect Dialect
    {
        get => Model.Dialect;
        set => Set(Model.Dialect, value, v => Model.Dialect = v);
    }

    public double SpindleMaxRpm
    {
        get => Model.SpindleMaxRpm;
        set => Set(Model.SpindleMaxRpm, Math.Max(1, value), v => Model.SpindleMaxRpm = v);
    }

    public double SpindleMaxS
    {
        get => Model.SpindleMaxS;
        set => Set(Model.SpindleMaxS, Math.Max(0, value), v => Model.SpindleMaxS = v);
    }

    public double MaxFeedRate
    {
        get => Model.MaxFeedRate;
        set => Set(Model.MaxFeedRate, Math.Max(0, value), v => Model.MaxFeedRate = v);
    }

    public double WorkAreaX
    {
        get => Model.WorkAreaX;
        set => Set(Model.WorkAreaX, Math.Max(0, value), v => Model.WorkAreaX = v);
    }

    public double WorkAreaY
    {
        get => Model.WorkAreaY;
        set => Set(Model.WorkAreaY, Math.Max(0, value), v => Model.WorkAreaY = v);
    }

    public double WorkAreaZ
    {
        get => Model.WorkAreaZ;
        set => Set(Model.WorkAreaZ, Math.Max(0, value), v => Model.WorkAreaZ = v);
    }

    public double SafeZ
    {
        get => Model.SafeZ;
        set => Set(Model.SafeZ, Math.Max(0.1, value), v => Model.SafeZ = v);
    }

    public double ApproachClearance
    {
        get => Model.ApproachClearance;
        set => Set(Model.ApproachClearance, Math.Max(0, value), v => Model.ApproachClearance = v);
    }

    public double RapidRate
    {
        get => Model.RapidRate;
        set => Set(Model.RapidRate, Math.Max(1, value), v => Model.RapidRate = v);
    }

    public int DecimalPlaces
    {
        get => Model.DecimalPlaces;
        set => Set(Model.DecimalPlaces, Math.Clamp(value, 0, 6), v => Model.DecimalPlaces = v);
    }

    public bool UseToolChange
    {
        get => Model.UseToolChange;
        set => Set(Model.UseToolChange, value, v => Model.UseToolChange = v);
    }

    public double SpindleDelaySeconds
    {
        get => Model.SpindleDelaySeconds;
        set => Set(Model.SpindleDelaySeconds, Math.Max(0, value), v => Model.SpindleDelaySeconds = v);
    }

    public bool ReturnToOrigin
    {
        get => Model.ReturnToOrigin;
        set => Set(Model.ReturnToOrigin, value, v => Model.ReturnToOrigin = v);
    }

    public string Header
    {
        get => Model.Header;
        set => Set(Model.Header, value ?? "", v => Model.Header = v);
    }

    public string Footer
    {
        get => Model.Footer;
        set => Set(Model.Footer, value ?? "", v => Model.Footer = v);
    }
}
