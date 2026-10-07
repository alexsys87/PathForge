using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PathForge.App.ViewModels;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.App.Controls;

/// <summary>
/// 2D view of contours and toolpaths (top view, Y up). Wheel zooms around the cursor,
/// dragging with any mouse button pans, a left click selects contours (Ctrl adds), a right click without dragging
/// opens the context menu. Dragging with Shift selects by a rectangle: to the right — the contours lying wholly
/// inside it, to the left — every contour it touches (Ctrl+Shift adds to the selection).
/// </summary>
public sealed class CamViewport : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(ViewScene), typeof(CamViewport),
        new FrameworkPropertyMetadata(ViewScene.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CamViewport)d).RebuildGeometry()));

    public static readonly DependencyProperty ContourClickCommandProperty = DependencyProperty.Register(
        nameof(ContourClickCommand), typeof(ICommand), typeof(CamViewport));

    public static readonly DependencyProperty BoxSelectCommandProperty = DependencyProperty.Register(
        nameof(BoxSelectCommand), typeof(ICommand), typeof(CamViewport));

    public static readonly DependencyProperty CursorTextProperty = DependencyProperty.Register(
        nameof(CursorText), typeof(string), typeof(CamViewport),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MachinePositionProperty = DependencyProperty.Register(
        nameof(MachinePosition), typeof(Vec2?), typeof(CamViewport),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double ClickTolerancePixels = 6;
    private const double DragThresholdPixels = 4;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)));
    private static readonly Color GridColor = Color.FromRgb(0x2B, 0x31, 0x39);
    private static readonly Color ContourColor = Color.FromRgb(0xD5, 0xD9, 0xDE);
    private static readonly Color OperationColor = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color SelectedColor = Color.FromRgb(0xFF, 0xB7, 0x4D);
    private static readonly Color CutColor = Color.FromRgb(0x66, 0xBB, 0x6A);
    private static readonly Color CutHighlightColor = Color.FromRgb(0xB9, 0xF6, 0xCA);
    private static readonly Color RapidColor = Color.FromArgb(0xB0, 0xEF, 0x53, 0x50);

    private readonly List<(Geometry Geometry, ContourState State)> _contourGeometry = new();
    private readonly List<(Geometry Cuts, Geometry Rapids, bool Highlighted)> _toolpathGeometry = new();
    private readonly List<(SceneContour Contour, Bounds2 Bounds)> _hitTargets = new();

    private double _scale = 4; // pixels per millimetre
    private Point _origin = new(60, 400); // screen position of world (0, 0)
    private bool _fitPending = true;
    private Point? _pressPoint;
    private Point _lastMouse;
    private bool _dragging;

    /// <summary>A Shift-drag draws the selection rectangle instead of panning.</summary>
    private bool _boxSelecting;

    public CamViewport()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public ViewScene Scene
    {
        get => (ViewScene)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Executed with a <see cref="ContourClick"/> argument.</summary>
    public ICommand? ContourClickCommand
    {
        get => (ICommand?)GetValue(ContourClickCommandProperty);
        set => SetValue(ContourClickCommandProperty, value);
    }

    /// <summary>Executed with a <see cref="ContourBoxSelection"/> argument after a Shift-drag.</summary>
    public ICommand? BoxSelectCommand
    {
        get => (ICommand?)GetValue(BoxSelectCommandProperty);
        set => SetValue(BoxSelectCommandProperty, value);
    }

    /// <summary>World coordinates under the mouse, formatted for the status bar.</summary>
    public string CursorText
    {
        get => (string)GetValue(CursorTextProperty);
        set => SetValue(CursorTextProperty, value);
    }

    /// <summary>Current tool position reported by the machine (program coordinates), or null.</summary>
    public Vec2? MachinePosition
    {
        get => (Vec2?)GetValue(MachinePositionProperty);
        set => SetValue(MachinePositionProperty, value);
    }

    public void ZoomToFit()
    {
        var bounds = Scene.Bounds;
        if (bounds.IsEmpty || ActualWidth < 1 || ActualHeight < 1)
        {
            _fitPending = true;
            return;
        }

        const double margin = 30;
        var width = Math.Max(bounds.Width, 1);
        var height = Math.Max(bounds.Height, 1);
        _scale = Math.Min((ActualWidth - 2 * margin) / width, (ActualHeight - 2 * margin) / height);
        _scale = Math.Clamp(_scale, 0.01, 10000);
        var center = bounds.Center;
        _origin = new Point(ActualWidth / 2 - center.X * _scale, ActualHeight / 2 + center.Y * _scale);
        _fitPending = false;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending)
        {
            ZoomToFit();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(BackgroundBrush, null, area);
        DrawGrid(dc);

        var world = new MatrixTransform(_scale, 0, 0, -_scale, _origin.X, _origin.Y);
        dc.PushTransform(world);

        // Pen widths are given in pixels and converted to world units.
        double Px(double pixels) => pixels / _scale;

        foreach (var (cuts, rapids, highlighted) in _toolpathGeometry)
        {
            var rapidPen = new Pen(new SolidColorBrush(RapidColor), Px(1)) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) };
            dc.DrawGeometry(null, rapidPen, rapids);
            var cutPen = new Pen(new SolidColorBrush(highlighted ? CutHighlightColor : CutColor), Px(highlighted ? 2 : 1.3))
            {
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, cutPen, cuts);
        }

        foreach (var (geometry, state) in _contourGeometry)
        {
            var (color, width) = state switch
            {
                ContourState.Selected => (SelectedColor, 2.5),
                ContourState.InOperation => (OperationColor, 2.0),
                _ => (ContourColor, 1.2),
            };
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), Px(width)) { LineJoin = PenLineJoin.Round }, geometry);
        }

        dc.Pop();
        DrawOriginMarker(dc);
        DrawMachinePosition(dc);
        DrawSelectionBox(dc);
    }

    /// <summary>The rectangle of a Shift-drag: solid when it takes only what lies wholly inside, dashed when it takes all it touches.</summary>
    private void DrawSelectionBox(DrawingContext dc)
    {
        if (!_boxSelecting || !_dragging || _pressPoint is not { } press)
        {
            return;
        }

        var crossing = _lastMouse.X < press.X;
        var pen = new Pen(new SolidColorBrush(SelectedColor), 1) { DashStyle = crossing ? new DashStyle(new[] { 4.0, 3.0 }, 0) : null };
        var fill = new SolidColorBrush(Color.FromArgb(0x30, SelectedColor.R, SelectedColor.G, SelectedColor.B));
        dc.DrawRectangle(fill, pen, new Rect(press, _lastMouse));
    }

    /// <summary>Crosshair where the spindle is now.</summary>
    private void DrawMachinePosition(DrawingContext dc)
    {
        if (MachinePosition is not { } position)
        {
            return;
        }

        var center = new Point(_origin.X + position.X * _scale, _origin.Y - position.Y * _scale);
        var pen = new Pen(Brushes.OrangeRed, 2);
        dc.DrawEllipse(null, pen, center, 7, 7);
        dc.DrawLine(pen, new Point(center.X - 12, center.Y), new Point(center.X + 12, center.Y));
        dc.DrawLine(pen, new Point(center.X, center.Y - 12), new Point(center.X, center.Y + 12));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var mouse = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        var newScale = Math.Clamp(_scale * factor, 0.01, 10000);
        factor = newScale / _scale;
        // Keep the world point under the cursor fixed.
        _origin = new Point(mouse.X - (mouse.X - _origin.X) * factor, mouse.Y - (mouse.Y - _origin.Y) * factor);
        _scale = newScale;
        _fitPending = false;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2)
        {
            ZoomToFit();
            return;
        }

        _pressPoint = e.GetPosition(this);
        _lastMouse = _pressPoint.Value;
        _dragging = false;
        _boxSelecting = e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var mouse = e.GetPosition(this);
        var world = ToWorld(mouse);
        CursorText = string.Format(CultureInfo.CurrentCulture, Loc.T("X {0:0.00}  Y {1:0.00} мм", "X {0:0.00}  Y {1:0.00} mm"), world.X, world.Y);

        if (_pressPoint is { } press && IsMouseCaptured)
        {
            if (!_dragging && (mouse - press).Length > DragThresholdPixels)
            {
                _dragging = true;
                Cursor = _boxSelecting ? Cursors.Cross : Cursors.SizeAll;
            }

            if (_dragging && _boxSelecting)
            {
                _lastMouse = mouse;
                InvalidateVisual();
                return;
            }

            if (_dragging)
            {
                _origin += mouse - _lastMouse;
                _fitPending = false;
                InvalidateVisual();
            }
        }

        _lastMouse = mouse;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_pressPoint is null)
        {
            return;
        }

        var wasDragging = _dragging;
        var press = _pressPoint.Value;
        var boxSelecting = _boxSelecting;
        _pressPoint = null;
        _dragging = false;
        _boxSelecting = false;
        Cursor = null;
        ReleaseMouseCapture();

        if (wasDragging && boxSelecting)
        {
            var end = e.GetPosition(this);
            var a = ToWorld(press);
            var b = ToWorld(end);
            var box = new Bounds2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            var crossing = end.X < press.X;
            var ids = _hitTargets.Where(h => crossing ? Touches(h.Contour, h.Bounds, box) : box.Contains(h.Bounds)).Select(h => h.Contour.Id).ToList();
            var selection = new ContourBoxSelection(ids, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            if (BoxSelectCommand?.CanExecute(selection) == true)
            {
                BoxSelectCommand.Execute(selection);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!wasDragging && e.ChangedButton == MouseButton.Right)
        {
            // Left unhandled so that the context menu (select all, clear selection…) opens.
            return;
        }

        if (!wasDragging && e.ChangedButton == MouseButton.Left)
        {
            var hit = HitTest(e.GetPosition(this));
            var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            var click = new ContourClick(hit, additive);
            if (ContourClickCommand?.CanExecute(click) == true)
            {
                ContourClickCommand.Execute(click);
            }
        }

        e.Handled = true;
    }

    private void RebuildGeometry()
    {
        _contourGeometry.Clear();
        _toolpathGeometry.Clear();
        _hitTargets.Clear();

        foreach (var contour in Scene.Contours)
        {
            if (contour.Points.Count < 2)
            {
                continue;
            }

            _contourGeometry.Add((BuildPolyline(contour.Points, contour.Closed), contour.State));
            _hitTargets.Add((contour, Bounds2.Of(contour.Points)));
        }

        // Selected contours are drawn last so they stay on top.
        _contourGeometry.Sort((a, b) => a.State.CompareTo(b.State));

        foreach (var toolpath in Scene.Toolpaths)
        {
            var cuts = new StreamGeometry();
            var rapids = new StreamGeometry();
            using (var cutContext = cuts.Open())
            using (var rapidContext = rapids.Open())
            {
                var position = toolpath.Start;
                foreach (var move in toolpath.Moves)
                {
                    var target = move.Target;
                    if (!target.XY.IsNear(position.XY, 1e-9))
                    {
                        // Laser moves with the beam off are shown like rapids.
                        var ctx = move.Kind == MoveKind.Rapid || move.Power == 0 ? rapidContext : cutContext;
                        ctx.BeginFigure(ToPoint(position.XY), false, false);
                        ctx.LineTo(ToPoint(target.XY), true, false);
                    }

                    position = target;
                }
            }

            cuts.Freeze();
            rapids.Freeze();
            _toolpathGeometry.Add((cuts, rapids, toolpath.Highlighted));
        }

        if (_fitPending)
        {
            ZoomToFit();
        }
    }

    private static Geometry BuildPolyline(IReadOnlyList<Vec2> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPoint(points[0]), false, closed);
            context.PolyLineTo(points.Skip(1).Select(ToPoint).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Whether a contour has a point inside the box or crosses its border.</summary>
    private static bool Touches(SceneContour contour, Bounds2 bounds, Bounds2 box)
    {
        if (bounds.MaxX < box.MinX || bounds.MinX > box.MaxX || bounds.MaxY < box.MinY || bounds.MinY > box.MaxY)
        {
            return false;
        }

        var points = contour.Points;
        bool Inside(Vec2 p) => p.X >= box.MinX && p.X <= box.MaxX && p.Y >= box.MinY && p.Y <= box.MaxY;
        if (points.Any(Inside))
        {
            return true;
        }

        // A segment crossing the box without a point inside it (Liang–Barsky clipping).
        var count = contour.Closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            var d = points[(i + 1) % points.Count] - p;
            double t0 = 0, t1 = 1;
            var hit = true;
            foreach (var (q, r) in new[] { (-d.X, p.X - box.MinX), (d.X, box.MaxX - p.X), (-d.Y, p.Y - box.MinY), (d.Y, box.MaxY - p.Y) })
            {
                if (Math.Abs(q) < 1e-12)
                {
                    if (r < 0)
                    {
                        hit = false;
                        break;
                    }

                    continue;
                }

                var t = r / q;
                if (q < 0)
                {
                    t0 = Math.Max(t0, t);
                }
                else
                {
                    t1 = Math.Min(t1, t);
                }

                if (t0 > t1)
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    private int? HitTest(Point screen)
    {
        var world = ToWorld(screen);
        var tolerance = ClickTolerancePixels / _scale;
        int? best = null;
        var bestDistance = tolerance;
        foreach (var (contour, bounds) in _hitTargets)
        {
            if (world.X < bounds.MinX - tolerance || world.X > bounds.MaxX + tolerance ||
                world.Y < bounds.MinY - tolerance || world.Y > bounds.MaxY + tolerance)
            {
                continue;
            }

            var distance = Polyline.DistanceTo(contour.Points, world, contour.Closed);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = contour.Id;
            }
        }

        return best;
    }

    private void DrawGrid(DrawingContext dc)
    {
        // Pick a 1-2-5 step that is at least 40 px on screen.
        var step = Math.Pow(10, Math.Floor(Math.Log10(40 / _scale)));
        foreach (var multiplier in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (step * multiplier * _scale >= 40)
            {
                step *= multiplier;
                break;
            }
        }

        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(ActualWidth, ActualHeight));
        if ((bottomRight.X - topLeft.X) / step > 400 || (topLeft.Y - bottomRight.Y) / step > 400)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(GridColor), 1);
        pen.Freeze();
        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var sx = Math.Round(_origin.X + x * _scale) + 0.5;
            dc.DrawLine(pen, new Point(sx, 0), new Point(sx, ActualHeight));
        }

        for (var y = Math.Floor(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
        {
            var sy = Math.Round(_origin.Y - y * _scale) + 0.5;
            dc.DrawLine(pen, new Point(0, sy), new Point(ActualWidth, sy));
        }
    }

    private void DrawOriginMarker(DrawingContext dc)
    {
        const double length = 30;
        var xPen = new Pen(Brushes.IndianRed, 2);
        var yPen = new Pen(Brushes.MediumSeaGreen, 2);
        dc.DrawLine(xPen, _origin, new Point(_origin.X + length, _origin.Y));
        dc.DrawLine(yPen, _origin, new Point(_origin.X, _origin.Y - length));
    }

    private Vec2 ToWorld(Point screen) => new((screen.X - _origin.X) / _scale, (_origin.Y - screen.Y) / _scale);

    private static Point ToPoint(Vec2 p) => new(p.X, p.Y);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
