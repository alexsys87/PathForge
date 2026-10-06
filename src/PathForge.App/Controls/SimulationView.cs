using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using PathForge.Core.Leveling;
using PathForge.Core.Machining;
using PathForge.Core.Simulation;

namespace PathForge.App.Controls;

/// <summary>What the 3D view shows: the (display-sized) stock surface and the tool.</summary>
/// <param name="Surface">Stock heights; a new instance (or <paramref name="Version"/>) means the mesh must be rebuilt.</param>
/// <param name="Version">Changes whenever the surface content changes.</param>
/// <param name="Tool">Tool shown at <paramref name="ToolTip"/>, or null.</param>
/// <param name="Deviation">Difference from the model per surface cell (mm, NaN outside the model); colours the surface when set.</param>
/// <param name="Tolerance">Deviation shown as "on the model" (mm).</param>
/// <param name="Path">Toolpath drawn over the stock (the operation being machined), or null.</param>
/// <param name="HeightMap">Probed board height map shown over the stock, or null.</param>
/// <param name="HeightMapScale">Vertical exaggeration of the height map.</param>
public sealed record SimulationFrame(HeightField Surface, int Version, Tool? Tool, Point3D ToolTip,
    float[]? Deviation = null, double Tolerance = 0.05, Toolpath? Path = null, LevelingMap? HeightMap = null, double HeightMapScale = 1);

/// <summary>
/// 3D view of the simulated stock. Left drag orbits, right or middle drag pans, the wheel zooms,
/// double click returns to the default view. Z is up.
/// </summary>
public sealed class SimulationView : Border
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(SimulationFrame), typeof(SimulationView),
        new PropertyMetadata(null, (d, e) => ((SimulationView)d).OnFrameChanged((SimulationFrame?)e.OldValue, (SimulationFrame?)e.NewValue)));

    private static readonly Color TopColor = Color.FromRgb(0xE4, 0xC2, 0x8E);
    private static readonly Color DeepColor = Color.FromRgb(0x8A, 0x5A, 0x2E);
    private static readonly Color TableColor = Color.FromRgb(0x4A, 0x50, 0x58);
    private static readonly Color BurnColor = Color.FromRgb(0x24, 0x18, 0x10);

    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 40, UpDirection = new Vector3D(0, 0, 1) };
    private readonly GeometryModel3D _surfaceModel = new();
    private readonly GeometryModel3D _sidesModel = new();
    private readonly GeometryModel3D _toolModel = new();
    private readonly GeometryModel3D _pathModel = new();
    private readonly GeometryModel3D _overlayModel = new();
    private Material? _comparisonMaterial;
    private double _comparisonTolerance = double.NaN;
    private Toolpath? _meshPath;
    private (LevelingMap? Map, double Scale, double Top) _meshMap;
    private readonly TranslateTransform3D _toolTransform = new();
    private readonly Material _stockMaterial;

    private Int32Collection? _indices;
    private (int Width, int Height) _indexSize;
    private Tool? _meshTool;
    private Point3D _target;
    private double _distance = 200;
    private double _yaw = -60;
    private double _pitch = 35;
    private Point _lastMouse;
    private MouseButton? _dragButton;
    private (double SizeX, double SizeY, double Top) _framedStock;

    public SimulationView()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29));
        Focusable = true;
        _stockMaterial = CreateStockMaterial();
        _surfaceModel.Material = _stockMaterial;
        _surfaceModel.BackMaterial = _stockMaterial;
        _sidesModel.Material = _stockMaterial;
        _sidesModel.BackMaterial = _stockMaterial;
        var toolMaterial = new MaterialGroup();
        toolMaterial.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5))));
        toolMaterial.Children.Add(new SpecularMaterial(Brushes.White, 40));
        _toolModel.Material = toolMaterial;
        _toolModel.Transform = _toolTransform;

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x60, 0x60)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0xC8, 0xC8, 0xC8), new Vector3D(-0.4, -0.6, -1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x58), new Vector3D(0.6, 0.5, -0.3)));
        scene.Children.Add(_surfaceModel);
        scene.Children.Add(_sidesModel);
        scene.Children.Add(_toolModel);
        scene.Children.Add(_pathModel);
        scene.Children.Add(_overlayModel);
        var pathMaterial = new MaterialGroup();
        pathMaterial.Children.Add(new DiffuseMaterial(Brushes.Black));
        pathMaterial.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x30, 0xE0, 0xFF))));
        pathMaterial.Freeze();
        _pathModel.Material = pathMaterial;
        _pathModel.BackMaterial = pathMaterial;
        _viewport.Children.Add(new ModelVisual3D { Content = scene });
        _viewport.Camera = _camera;
        Child = _viewport;
        UpdateCamera();
    }

    public SimulationFrame? Frame
    {
        get => (SimulationFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Default view: looking at the whole stock from the front left, above.</summary>
    public void ResetView()
    {
        if (Frame is not { } frame)
        {
            return;
        }

        var field = frame.Surface;
        _target = new Point3D(field.Origin.X + field.SizeX / 2, field.Origin.Y + field.SizeY / 2, (field.Top + field.Bottom) / 2);
        _distance = Math.Max(field.SizeX, field.SizeY) * 1.6 + 20;
        _yaw = -60;
        _pitch = 35;
        UpdateCamera();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ResetView();
            return;
        }

        _dragButton = e.ChangedButton;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragButton is not { } button || !IsMouseCaptured)
        {
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _lastMouse;
        _lastMouse = position;
        if (button == MouseButton.Left)
        {
            _yaw -= delta.X * 0.4;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.4, -5, 89);
        }
        else
        {
            // Pan in the view plane, scaled so that the stock follows the mouse.
            var look = _camera.LookDirection;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            var scale = _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360) * 2 / Math.Max(1, ActualHeight);
            _target -= right * delta.X * scale;
            _target += up * delta.Y * scale;
        }

        UpdateCamera();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _dragButton = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), 1, 5000);
        UpdateCamera();
        e.Handled = true;
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch)) * _distance;
        _camera.Position = _target + offset;
        _camera.LookDirection = -offset;
        _camera.NearPlaneDistance = Math.Max(0.1, _distance / 1000);
        _camera.FarPlaneDistance = _distance * 20 + 1000;
    }

    private void OnFrameChanged(SimulationFrame? old, SimulationFrame? frame)
    {
        if (frame is null)
        {
            _surfaceModel.Geometry = null;
            _sidesModel.Geometry = null;
            _toolModel.Geometry = null;
            _pathModel.Geometry = null;
            _meshPath = null;
            _overlayModel.Geometry = null;
            _meshMap = default;
            return;
        }

        if (old is null || !ReferenceEquals(old.Surface, frame.Surface) || old.Version != frame.Version ||
            !ReferenceEquals(old.Deviation, frame.Deviation) || old.Tolerance != frame.Tolerance)
        {
            BuildStock(frame.Surface, frame.Deviation, frame.Tolerance);
        }

        if (!ReferenceEquals(frame.Path, _meshPath))
        {
            _meshPath = frame.Path;
            _pathModel.Geometry = frame.Path is null ? null : PathMesh(frame.Path, frame.Surface);
        }

        var map = (frame.HeightMap, frame.HeightMapScale, frame.Surface.Top);
        if (map != _meshMap)
        {
            _meshMap = map;
            _overlayModel.Geometry = frame.HeightMap is { IsValid: true } heights ? HeightMapMesh(heights, frame.HeightMapScale, frame.Surface.Top) : null;
            if (_overlayModel.Material is null)
            {
                _overlayModel.Material = CreateHeightMapMaterial();
                _overlayModel.BackMaterial = _overlayModel.Material;
            }
        }

        var stock = (frame.Surface.SizeX, frame.Surface.SizeY, frame.Surface.Top);
        if (stock != _framedStock)
        {
            // New stock size (another project): frame it.
            _framedStock = stock;
            ResetView();
        }

        if (!ReferenceEquals(frame.Tool, _meshTool))
        {
            _meshTool = frame.Tool;
            _toolModel.Geometry = frame.Tool is null ? null : ToolMesh(frame.Tool);
        }

        _toolTransform.OffsetX = frame.ToolTip.X;
        _toolTransform.OffsetY = frame.ToolTip.Y;
        _toolTransform.OffsetZ = frame.ToolTip.Z;
    }

    /// <summary>
    /// Top surface as a grid of vertices; texture U is the depth (0 top … 1 bottom), V the laser burn.
    /// With a deviation the surface is coloured by it instead (comparison with the model).
    /// </summary>
    private void BuildStock(HeightField field, float[]? deviation, double tolerance)
    {
        var range = ComparisonRange(tolerance);
        if (deviation is not null && (_comparisonMaterial is null || _comparisonTolerance != tolerance))
        {
            _comparisonMaterial = CreateComparisonMaterial(tolerance, range);
            _comparisonTolerance = tolerance;
        }

        _surfaceModel.Material = deviation is null ? _stockMaterial : _comparisonMaterial;
        _surfaceModel.BackMaterial = _surfaceModel.Material;
        var width = field.Width;
        var height = field.Height;
        var thickness = Math.Max(1e-6, field.Top - field.Bottom);
        var positions = new Point3DCollection(width * height);
        var texture = new PointCollection(width * height);
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var center = field.CellCenter(i, j);
                var z = Math.Max(field.Bottom, (double)field.Heights[j * width + i]);
                positions.Add(new Point3D(center.X, center.Y, z));
                if (deviation is null)
                {
                    texture.Add(new Point(Math.Clamp((field.Top - z) / thickness, 0, 1), field.Burn[j * width + i] / 255.0));
                }
                else
                {
                    // Row 0 of the comparison texture: deviation from -range to +range; row 1: outside the model.
                    var d = deviation[j * width + i];
                    texture.Add(float.IsNaN(d) ? new Point(0.5, 0.9) : new Point(Math.Clamp(0.5 + d / (2 * range), 0.002, 0.998), 0.1));
                }
            }
        }

        if (_indices is null || _indexSize != (width, height))
        {
            _indices = new Int32Collection((width - 1) * (height - 1) * 6);
            for (var j = 0; j + 1 < height; j++)
            {
                for (var i = 0; i + 1 < width; i++)
                {
                    var a = j * width + i;
                    var b = a + 1;
                    var c = a + width;
                    var d = c + 1;
                    _indices.Add(a);
                    _indices.Add(b);
                    _indices.Add(d);
                    _indices.Add(a);
                    _indices.Add(d);
                    _indices.Add(c);
                }
            }

            _indices.Freeze();
            _indexSize = (width, height);
        }

        positions.Freeze();
        texture.Freeze();
        var surface = new MeshGeometry3D { Positions = positions, TextureCoordinates = texture, TriangleIndices = _indices };
        surface.Freeze();
        _surfaceModel.Geometry = surface;
        _sidesModel.Geometry = SideWalls(field, thickness);
    }

    /// <summary>Deviation shown at full colour strength (mm).</summary>
    private static double ComparisonRange(double tolerance) => Math.Max(0.25, tolerance * 6);

    /// <summary>
    /// Colours of the comparison: green within the tolerance, blue where material is left (stronger the thicker),
    /// red where the tool went too deep; the second row is the stock outside the model.
    /// </summary>
    private static Material CreateComparisonMaterial(double tolerance, double range)
    {
        const int Width = 256;
        var pixels = new byte[Width * 2 * 4];
        var within = Color.FromRgb(0x4C, 0xC0, 0x5A);
        var gouge = Color.FromRgb(0xE0, 0x30, 0x30);
        var left = Color.FromRgb(0x30, 0x70, 0xE0);
        var pale = Color.FromRgb(0xD8, 0xD8, 0xD0);
        for (var x = 0; x < Width; x++)
        {
            var d = ((x + 0.5) / Width - 0.5) * 2 * range;
            var strength = Math.Clamp((Math.Abs(d) - tolerance) / Math.Max(1e-6, range - tolerance), 0, 1);
            var color = Math.Abs(d) <= tolerance ? within : Mix(pale, d < 0 ? gouge : left, 0.35 + 0.65 * strength);
            var o = x * 4;
            pixels[o] = color.B;
            pixels[o + 1] = color.G;
            pixels[o + 2] = color.R;
            pixels[o + 3] = 255;
            var outside = Color.FromRgb(0x8A, 0x80, 0x74);
            o = (Width + x) * 4;
            pixels[o] = outside.B;
            pixels[o + 1] = outside.G;
            pixels[o + 2] = outside.R;
            pixels[o + 3] = 255;
        }

        var bitmap = BitmapSource.Create(Width, 2, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    /// <summary>
    /// The toolpath as thin glowing ribbons a little above the tool tip: flat for moves along the table,
    /// upright for plunges. Very long paths are thinned out to keep the view responsive.
    /// </summary>
    private static MeshGeometry3D? PathMesh(Toolpath toolpath, HeightField field)
    {
        const int MaxSegments = 60_000;
        var moves = toolpath.Moves;
        if (moves.Count < 2)
        {
            return null;
        }

        var width = Math.Clamp(Math.Max(field.SizeX, field.SizeY) / 500, 0.08, 1.5);
        var lift = width / 2;
        var stride = Math.Max(1, moves.Count / MaxSegments);
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var from = moves[0].Target;
        for (var k = stride; k < moves.Count; k += stride)
        {
            var to = moves[k].Target;
            var kind = moves[k].Kind;
            var segment = from;
            from = to;
            if (kind == MoveKind.Rapid)
            {
                continue;
            }

            var dx = to.X - segment.X;
            var dy = to.Y - segment.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            Vector3D side;
            if (length > 1e-6)
            {
                side = new Vector3D(-dy / length * width / 2, dx / length * width / 2, 0);
            }
            else if (Math.Abs(to.Z - segment.Z) > 1e-6)
            {
                side = new Vector3D(width / 2, 0, 0);
            }
            else
            {
                continue;
            }

            var a = new Point3D(segment.X, segment.Y, segment.Z + lift);
            var b = new Point3D(to.X, to.Y, to.Z + lift);
            var start = positions.Count;
            positions.Add(a - side);
            positions.Add(a + side);
            positions.Add(b + side);
            positions.Add(b - side);
            indices.Add(start);
            indices.Add(start + 1);
            indices.Add(start + 2);
            indices.Add(start);
            indices.Add(start + 2);
            indices.Add(start + 3);
        }

        if (positions.Count == 0)
        {
            return null;
        }

        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>
    /// The probed height map as a translucent sheet a little above the stock top: heights exaggerated by
    /// <paramref name="scale"/>, coloured from blue (lowest) to red (highest), with the probe points as the grid.
    /// </summary>
    private static MeshGeometry3D HeightMapMesh(LevelingMap map, double scale, double top)
    {
        const int Subdivide = 4;
        var nx = (map.CountX - 1) * Subdivide + 1;
        var ny = (map.CountY - 1) * Subdivide + 1;
        var range = Math.Max(1e-9, map.Max - map.Min);
        var lift = 0.5;
        var positions = new Point3DCollection(nx * ny);
        var texture = new PointCollection(nx * ny);
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var x = map.X0 + map.StepX * i / Subdivide;
                var y = map.Y0 + map.StepY * j / Subdivide;
                var h = map.HeightAt(x, y);
                positions.Add(new Point3D(x, y, top + lift + (h - map.Min) * scale));
                texture.Add(new Point(Math.Clamp((h - map.Min) / range, 0.002, 0.998), 0.5));
            }
        }

        var indices = new Int32Collection((nx - 1) * (ny - 1) * 6);
        for (var j = 0; j + 1 < ny; j++)
        {
            for (var i = 0; i + 1 < nx; i++)
            {
                var a = j * nx + i;
                indices.Add(a);
                indices.Add(a + 1);
                indices.Add(a + nx + 1);
                indices.Add(a);
                indices.Add(a + nx + 1);
                indices.Add(a + nx);
            }
        }

        var mesh = new MeshGeometry3D { Positions = positions, TextureCoordinates = texture, TriangleIndices = indices };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Blue → cyan → green → yellow → red, slightly see-through.</summary>
    private static Material CreateHeightMapMaterial()
    {
        const int Width = 256;
        var stops = new[]
        {
            Color.FromRgb(0x30, 0x50, 0xE0), Color.FromRgb(0x30, 0xC0, 0xE0), Color.FromRgb(0x40, 0xC0, 0x50),
            Color.FromRgb(0xF0, 0xD0, 0x30), Color.FromRgb(0xE0, 0x40, 0x30),
        };
        var pixels = new byte[Width * 4];
        for (var x = 0; x < Width; x++)
        {
            var t = x / (double)(Width - 1) * (stops.Length - 1);
            var k = Math.Min(stops.Length - 2, (int)t);
            var color = Mix(stops[k], stops[k + 1], t - k);
            pixels[x * 4] = color.B;
            pixels[x * 4 + 1] = color.G;
            pixels[x * 4 + 2] = color.R;
            pixels[x * 4 + 3] = 0xD8;
        }

        var bitmap = BitmapSource.Create(Width, 1, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        brush.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x30, 0x40, 0x40, 0x40))));
        material.Freeze();
        return material;
    }

    /// <summary>The four sides and the bottom of the stock, following the edge of the surface.</summary>
    private static MeshGeometry3D SideWalls(HeightField field, double thickness)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var texture = new PointCollection();
        var indices = new Int32Collection();

        void Wall(IEnumerable<(int I, int J)> cells)
        {
            var start = positions.Count;
            var count = 0;
            foreach (var (i, j) in cells)
            {
                var p = field.CellCenter(i, j);
                var z = Math.Max(field.Bottom, (double)field.Heights[j * field.Width + i]);
                positions.Add(new Point3D(p.X, p.Y, z));
                texture.Add(new Point(Math.Clamp((field.Top - z) / thickness, 0, 1), 0));
                positions.Add(new Point3D(p.X, p.Y, field.Bottom));
                texture.Add(new Point(0.999, 0));
                count++;
            }

            for (var k = 0; k + 1 < count; k++)
            {
                var a = start + 2 * k;
                indices.Add(a);
                indices.Add(a + 1);
                indices.Add(a + 3);
                indices.Add(a);
                indices.Add(a + 3);
                indices.Add(a + 2);
            }
        }

        var w = field.Width;
        var h = field.Height;
        Wall(Enumerable.Range(0, w).Select(i => (i, 0)));
        Wall(Enumerable.Range(0, h).Select(j => (w - 1, j)));
        Wall(Enumerable.Range(0, w).Select(i => (w - 1 - i, h - 1)));
        Wall(Enumerable.Range(0, h).Select(j => (0, h - 1 - j)));

        // Bottom face.
        var bottom = positions.Count;
        var c0 = field.CellCenter(0, 0);
        var c1 = field.CellCenter(w - 1, h - 1);
        positions.Add(new Point3D(c0.X, c0.Y, field.Bottom));
        positions.Add(new Point3D(c1.X, c0.Y, field.Bottom));
        positions.Add(new Point3D(c1.X, c1.Y, field.Bottom));
        positions.Add(new Point3D(c0.X, c1.Y, field.Bottom));
        for (var k = 0; k < 4; k++)
        {
            texture.Add(new Point(0.999, 0));
        }

        foreach (var index in new[] { 0, 2, 1, 0, 3, 2 })
        {
            indices.Add(bottom + index);
        }

        mesh.Positions = positions;
        mesh.TextureCoordinates = texture;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Solid of revolution for the tool: cutting part from the tip, then a shank.</summary>
    private static MeshGeometry3D ToolMesh(Tool tool)
    {
        var radius = Math.Max(0.1, tool.Diameter / 2);
        var profile = new List<(double R, double Z)>();
        switch (tool.Kind)
        {
            case ToolKind.BallNose:
                for (var k = 0; k <= 8; k++)
                {
                    var a = k * Math.PI / 16;
                    profile.Add((radius * Math.Sin(a), radius - radius * Math.Cos(a)));
                }

                break;
            case ToolKind.VBit:
                var tan = Math.Tan(Math.Clamp(tool.TipAngle, 1, 179) * Math.PI / 360);
                profile.Add((tool.TipDiameter / 2, 0));
                profile.Add((radius, Math.Max(0, radius - tool.TipDiameter / 2) / tan));
                break;
            case ToolKind.Drill:
                profile.Add((0, 0));
                profile.Add((radius, radius / Math.Tan(59 * Math.PI / 180)));
                break;
            case ToolKind.Laser:
                // A thin beam above the spot and the module body.
                profile.Add((radius, 0));
                profile.Add((radius, 15));
                profile.Add((15, 15));
                profile.Add((15, 45));
                return Revolve(profile);
            default:
                profile.Add((radius, 0));
                break;
        }

        var cutTop = Math.Max(profile[^1].Z, 0) + Math.Max(8, radius * 4);
        profile.Add((radius, cutTop));
        profile.Add((Math.Max(radius, 1.6), cutTop + 2));
        profile.Add((Math.Max(radius, 1.6), cutTop + 25));
        return Revolve(profile);
    }

    private static MeshGeometry3D Revolve(List<(double R, double Z)> profile)
    {
        const int Segments = 24;
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        positions.Add(new Point3D(0, 0, profile[0].Z));
        foreach (var (r, z) in profile)
        {
            for (var s = 0; s < Segments; s++)
            {
                var a = 2 * Math.PI * s / Segments;
                positions.Add(new Point3D(r * Math.Cos(a), r * Math.Sin(a), z));
            }
        }

        positions.Add(new Point3D(0, 0, profile[^1].Z));
        for (var s = 0; s < Segments; s++)
        {
            var next = (s + 1) % Segments;
            indices.Add(0);
            indices.Add(1 + next);
            indices.Add(1 + s);
        }

        for (var ring = 0; ring + 1 < profile.Count; ring++)
        {
            var a0 = 1 + ring * Segments;
            var b0 = a0 + Segments;
            for (var s = 0; s < Segments; s++)
            {
                var next = (s + 1) % Segments;
                indices.Add(a0 + s);
                indices.Add(a0 + next);
                indices.Add(b0 + next);
                indices.Add(a0 + s);
                indices.Add(b0 + next);
                indices.Add(b0 + s);
            }
        }

        var top = positions.Count - 1;
        var last = 1 + (profile.Count - 1) * Segments;
        for (var s = 0; s < Segments; s++)
        {
            indices.Add(top);
            indices.Add(last + s);
            indices.Add(last + (s + 1) % Segments);
        }

        mesh.Positions = positions;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }

    /// <summary>
    /// Colour table addressed by the texture coordinates: across = depth (light top, darker deeper,
    /// grey where the table shows), along = laser burn.
    /// </summary>
    private static Material CreateStockMaterial()
    {
        const int Width = 64;
        const int Height = 16;
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            var burn = y / (double)(Height - 1);
            for (var x = 0; x < Width; x++)
            {
                var depth = x / (double)(Width - 1);
                var color = depth >= 0.995 ? TableColor : Mix(TopColor, DeepColor, Math.Pow(depth, 0.7));
                color = Mix(color, BurnColor, burn * 0.9);
                var o = (y * Width + x) * 4;
                pixels[o] = color.B;
                pixels[o + 1] = color.G;
                pixels[o + 2] = color.R;
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        // Absolute mapping: texture coordinates are used as they are, not stretched to their bounding box.
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
