using CommunityToolkit.Mvvm.Input;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.App.ViewModels;

/// <summary>Drawing edits and the generators of the "Design" tab: boxes, panel cutouts, dial scales.</summary>
public sealed partial class MainViewModel
{
    public DrawingEditViewModel DrawingEdit { get; } = new();

    public FingerBoxViewModel Box { get; } = new();

    public PanelCutoutViewModel Cutout { get; } = new();

    public DialScaleViewModel Dial { get; } = new();

    // ---- Drawing edits -----------------------------------------------------------------------

    /// <summary>The selected contours, or null after a message when nothing is selected.</summary>
    private List<int>? SelectionForEdit()
    {
        if (SelectedContourIds.Count > 0)
        {
            return SelectedContourIds.ToList();
        }

        Messages.Add(Loc.T("Сначала выделите контуры на чертеже.", "Select contours on the drawing first."));
        return null;
    }

    /// <summary>Everything that shows the drawing follows an edit.</summary>
    private void AfterDrawingEdit(IEnumerable<int>? select = null)
    {
        if (select is not null)
        {
            SelectedContourIds.Clear();
            SelectedContourIds.UnionWith(select);
        }

        SelectedContourIds.IntersectWith(_project.Contours.Select(c => c.Id));
        if (Texts.Count != _project.Texts.Count)
        {
            LoadTexts();
        }
        else
        {
            foreach (var text in Texts)
            {
                text.Refresh();
            }
        }

        foreach (var operation in Operations)
        {
            operation.RefreshSummary();
        }

        RefreshLayers();
        OnSelectionChanged();
        OnProjectChanged();
        Regenerate();
    }

    [RelayCommand]
    private void MoveSelected()
    {
        if (SelectionForEdit() is { } ids)
        {
            DrawingEdits.Move(_project, ids, DrawingEdit.MoveX, DrawingEdit.MoveY);
            AfterDrawingEdit();
        }
    }

    [RelayCommand]
    private void RotateSelected()
    {
        if (SelectionForEdit() is { } ids)
        {
            DrawingEdits.Rotate(_project, ids, DrawingEdit.Angle, DrawingEdits.Bounds(_project, ids).Center);
            AfterDrawingEdit();
        }
    }

    [RelayCommand]
    private void ScaleSelected()
    {
        if (SelectionForEdit() is not { } ids)
        {
            return;
        }

        if (DrawingEdit.ScaleFactor <= 0)
        {
            Messages.Add(Loc.T("Масштаб должен быть больше нуля.", "The scale must be greater than zero."));
            return;
        }

        DrawingEdits.Scale(_project, ids, DrawingEdit.ScaleFactor, DrawingEdits.Bounds(_project, ids).Center);
        AfterDrawingEdit();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectionForEdit() is { } ids)
        {
            var count = DrawingEdits.Delete(_project, ids);
            Messages.Add(Loc.T($"Удалено контуров: {count}.", $"Contours deleted: {count}."));
            AfterDrawingEdit();
        }
    }

    [RelayCommand]
    private void ArrayRectangular()
    {
        if (SelectionForEdit() is { } ids)
        {
            var copies = DrawingEdits.RectangularArray(_project, ids, Math.Clamp(DrawingEdit.Columns, 1, 100), Math.Clamp(DrawingEdit.Rows, 1, 100),
                DrawingEdit.StepX, DrawingEdit.StepY);
            Messages.Add(Loc.T($"Массив: добавлено контуров {copies.Count}, операции получили копии.", $"Array: {copies.Count} contours added, the operations got the copies."));
            AfterDrawingEdit(ids.Concat(copies));
        }
    }

    [RelayCommand]
    private void ArrayCircular()
    {
        if (SelectionForEdit() is { } ids)
        {
            var copies = DrawingEdits.CircularArray(_project, ids, Math.Clamp(DrawingEdit.CircularCount, 1, 360), DrawingEdit.CircularSweep,
                new Vec2(DrawingEdit.CenterX, DrawingEdit.CenterY), DrawingEdit.RotateCopies);
            Messages.Add(Loc.T($"Круговой массив: добавлено контуров {copies.Count}.", $"Circular array: {copies.Count} contours added."));
            AfterDrawingEdit(ids.Concat(copies));
        }
    }

    /// <summary>The centre of the circular array: the centre of the selection (e.g. a circle).</summary>
    [RelayCommand]
    private void ArrayCenterFromSelection()
    {
        if (SelectionForEdit() is { } ids)
        {
            var center = DrawingEdits.Bounds(_project, ids).Center;
            (DrawingEdit.CenterX, DrawingEdit.CenterY) = (Math.Round(center.X, 3), Math.Round(center.Y, 3));
        }
    }

    /// <summary>Selection by a rectangle dragged with Shift on the drawing.</summary>
    [RelayCommand]
    private void BoxSelect(ContourBoxSelection? selection)
    {
        if (selection is null)
        {
            return;
        }

        if (!selection.Additive)
        {
            SelectedContourIds.Clear();
        }

        SelectedContourIds.UnionWith(selection.ContourIds);
        OnSelectionChanged();
    }

    // ---- Box ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds the parts of a finger-joint box to the right of the drawing and the operations that cut them: a laser
    /// cut with the kerf compensated in a laser profile, else an outside profile with dog-bones in the inner corners
    /// (and pockets for the grooves of a sliding lid, milled first).
    /// </summary>
    [RelayCommand]
    private void AddFingerBox()
    {
        var drawing = _project.DrawingBounds();
        var settings = Box.Model;
        settings.X = drawing.IsEmpty ? 0 : drawing.MaxX + 10;
        settings.Y = drawing.IsEmpty ? 0 : drawing.MinY;
        settings.SheetWidth = _project.Machine.WorkAreaX > 0 ? _project.Machine.WorkAreaX : 0;
        var box = FingerBox.Build(settings, _project.NextContourId());
        foreach (var warning in box.Warnings)
        {
            Messages.Add(warning);
        }

        if (box.Outlines.Count == 0)
        {
            return;
        }

        _project.Contours.AddRange(box.Outlines);
        _project.Contours.AddRange(box.Grooves);
        var t = settings.Thickness;
        var added = new List<Operation>();
        if (_project.Machine.LaserMode)
        {
            var laser = LaserTool();
            if (box.Grooves.Count > 0)
            {
                added.Add(new LaserVectorOperation
                {
                    Name = NewName("Пазы крышки", "Lid grooves"), Mode = LaserVectorMode.Fill, PowerPercent = 100, Speed = 600, Passes = 3,
                    ToolId = laser?.Id ?? "", ContourIds = box.Grooves.Select(c => c.Id).ToList(),
                });
            }

            added.Add(new LaserVectorOperation
            {
                Name = NewName("Коробка", "Box"), PowerPercent = 100, Speed = 300, Passes = 2, AirAssist = true,
                Kerf = KerfCompensation.Parts, KerfWidth = laser?.Diameter ?? 0.15,
                ToolId = laser?.Id ?? "", ContourIds = box.Outlines.Select(c => c.Id).ToList(),
            });
        }
        else
        {
            var mill = _project.Tools.Where(tool => tool.Kind == ToolKind.EndMill && tool.Diameter <= 3.2).MaxBy(tool => tool.Diameter)
                       ?? _project.Tools.FirstOrDefault(tool => tool.Kind == ToolKind.EndMill);
            if (box.Grooves.Count > 0)
            {
                added.Add(new PocketOperation
                {
                    Name = NewName("Пазы крышки", "Lid grooves"), Depth = Math.Round(t / 2, 2), Entry = EntryMode.Ramp,
                    ToolId = mill?.Id ?? "", ContourIds = box.Grooves.Select(c => c.Id).ToList(),
                });
            }

            added.Add(new ProfileOperation
            {
                Name = NewName("Коробка", "Box"), Side = ProfileSide.Outside, Depth = Math.Round(t + 0.2, 2), Entry = EntryMode.Ramp,
                CornerRelief = CornerRelief.Dogbone, ToolId = mill?.Id ?? "", ContourIds = box.Outlines.Select(c => c.Id).ToList(),
            });
        }

        foreach (var operation in added)
        {
            _project.Operations.Add(operation);
            Operations.Add(OperationViewModel.Create(operation, OnProjectChanged));
        }

        SelectedOperation = Operations[^1];
        Messages.Add(Loc.T(
            $"Коробка {box.Outer.X:0.#} × {box.Outer.Y:0.#} × {box.Outer.Z:0.#} мм снаружи: деталей {box.Outlines.Count}" +
            (_project.Machine.LaserMode ? ", резка лазером с учётом ширины реза." : ", контур снаружи с подрезкой углов (dogbone), глубина = толщина + 0,2 мм.") +
            (box.Grooves.Count > 0 ? " Пазы крышки — на внутренней стороне: кладите детали этой стороной вверх." : "") +
            " Сначала вырежьте пробный угол и подберите «Посадку».",
            $"Box {box.Outer.X:0.#} × {box.Outer.Y:0.#} × {box.Outer.Z:0.#} mm outside: {box.Outlines.Count} parts" +
            (_project.Machine.LaserMode ? ", laser cut with the kerf compensated." : ", outside profile with dog-bone corners, depth = thickness + 0.2 mm.") +
            (box.Grooves.Count > 0 ? " The lid grooves are on the inner side: lay the parts with that side up." : "") +
            " Cut a test corner first and adjust the “Fit”."));
        AfterDrawingEdit(box.Outlines.Select(c => c.Id));
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- Panel cutouts and dial scales -------------------------------------------------------

    /// <summary>The centre of the cutout (or the dial) from the selection.</summary>
    [RelayCommand]
    private void CutoutCenterFromSelection()
    {
        if (SelectionForEdit() is { } ids)
        {
            var center = DrawingEdits.Bounds(_project, ids).Center;
            (Cutout.CenterX, Cutout.CenterY) = (Math.Round(center.X, 3), Math.Round(center.Y, 3));
        }
    }

    [RelayCommand]
    private void AddPanelCutout()
    {
        var warnings = new List<string>();
        var contours = PanelCutouts.Place(Cutout.SelectedCutout, Cutout.Values.Select(v => v.Value).ToList(),
            new Vec2(Cutout.CenterX, Cutout.CenterY), Cutout.Angle, _project.NextContourId(), warnings);
        foreach (var warning in warnings)
        {
            Messages.Add(warning);
        }

        if (contours.Count == 0)
        {
            return;
        }

        _project.Contours.AddRange(contours);
        Messages.Add(Loc.T(
            $"{Cutout.SelectedCutout.Name}: контуров {contours.Count} на слое «{PanelCutouts.Layer}», они выделены — добавьте операцию (контур внутри, отверстия по спирали, лазер).",
            $"{Cutout.SelectedCutout.Name}: {contours.Count} contours on the “{PanelCutouts.Layer}” layer, selected — add an operation (inside profile, helical holes, laser)."));
        AfterDrawingEdit(contours.Select(c => c.Id));
    }

    [RelayCommand]
    private void DialCenterFromSelection()
    {
        if (SelectionForEdit() is { } ids)
        {
            var center = DrawingEdits.Bounds(_project, ids).Center;
            (Dial.X, Dial.Y) = (Math.Round(center.X, 3), Math.Round(center.Y, 3));
        }
    }

    [RelayCommand]
    private void AddDialScale()
    {
        var contours = DialScale.Build(Dial.Model, _project.NextContourId());
        _project.Contours.AddRange(contours);
        Messages.Add(Loc.T(
            $"Шкала: контуров {contours.Count} на слое «{DialScale.Layer}», они выделены. " +
            (Dial.StrokeWidth > 0 ? "Это замкнутые контуры — для V-карвинга или кармана." : "Это линии — для гравировки «Контуром» по линии или лазером."),
            $"Scale: {contours.Count} contours on the “{DialScale.Layer}” layer, selected. " +
            (Dial.StrokeWidth > 0 ? "They are closed outlines — for V-carving or a pocket." : "They are lines — for engraving with a Profile on the line or by laser.")));
        AfterDrawingEdit(contours.Select(c => c.Id));
    }

    // ---- Operations for panels ---------------------------------------------------------------

    [RelayCommand]
    private void AddChamfer()
    {
        var vbit = _project.Tools.Where(t => t.Kind == ToolKind.VBit).MaxBy(t => t.TipAngle);
        if (vbit is null)
        {
            Messages.Add(Loc.T("Для фаски добавьте V-фрезу: «Инструменты» → пресет «V-карвинг: V-фреза 90°».", "For a chamfer add a V-bit: “Tools” → preset “V-carving: V-bit 90°”."));
        }

        AddOperation(new ChamferOperation { Name = NewName("Фаска", "Chamfer") }, vbit);
    }

    [RelayCommand]
    private void AddHelixHoles()
    {
        var mill = _project.Tools.Where(t => t.Kind == ToolKind.EndMill).MaxBy(t => t.Diameter);
        var operation = new HelixHoleOperation { Name = NewName("Отверстия по спирали", "Helical holes"), Depth = Math.Round(_project.Stock.Thickness + 0.2, 2) };
        AddOperation(operation, mill);
        operation.Entry = EntryMode.Helix;
    }
}
