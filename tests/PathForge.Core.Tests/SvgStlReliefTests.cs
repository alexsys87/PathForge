using PathForge.Core.Geometry;
using PathForge.Core.Import;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class SvgTests
{
    private static List<Contour> Import(string svg, out DxfImportResult result)
    {
        result = SvgReader.ReadText(svg);
        return ContourBuilder.Build(result.Paths);
    }

    private static List<Contour> Import(string svg) => Import(svg, out _);

    private const string Mm100 = "width=\"100mm\" height=\"50mm\" viewBox=\"0 0 100 50\"";

    [Fact]
    public void Rect_in_a_millimetre_document_is_flipped_to_y_up()
    {
        var contour = Assert.Single(Import($"""<svg xmlns="http://www.w3.org/2000/svg" {Mm100}><rect x="10" y="5" width="20" height="10"/></svg>"""));

        Assert.True(contour.IsClosed);
        var b = contour.GetBounds();
        Assert.Equal(10, b.MinX, 6);
        Assert.Equal(30, b.MaxX, 6);
        // y = 5..15 from the top of a 50 mm page → 35..45 from the bottom.
        Assert.Equal(35, b.MinY, 6);
        Assert.Equal(45, b.MaxY, 6);
    }

    [Fact]
    public void Pixels_without_viewbox_are_96_dpi()
    {
        var contour = Assert.Single(Import("""<svg xmlns="http://www.w3.org/2000/svg"><line x1="0" y1="0" x2="96" y2="0"/></svg>"""));
        Assert.Equal(25.4, contour.Length, 6);
    }

    [Fact]
    public void Path_with_relative_commands_curves_and_close()
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" {Mm100}>
              <path d="m10,10 h20 v10 c0,5 -20,5 -20,0 z"/>
            </svg>
            """;

        var contour = Assert.Single(Import(svg));

        Assert.True(contour.IsClosed);
        var b = contour.GetBounds();
        Assert.Equal(10, b.MinX, 6);
        Assert.Equal(30, b.MaxX, 6);
        // The Bézier bulges 3.75 mm below y = 20 (SVG), i.e. down to y = 50 - 23.75 in Y-up coordinates.
        Assert.Equal(50 - 23.75, b.MinY, 2);
    }

    [Fact]
    public void Circular_arc_command_becomes_a_true_arc()
    {
        // Half circle of radius 10 from (0,20) to (20,20) with packed flags "01".
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" {Mm100}><path d="M0 20A10 10 0 0120 20"/></svg>""";

        var contour = Assert.Single(Import(svg));
        var arc = Assert.IsType<ArcSegment>(Assert.Single(contour.Segments));

        Assert.Equal(10, arc.Radius, 6);
        Assert.True(arc.Center.IsNear(new Vec2(10, 30), 1e-6));
        Assert.Equal(Math.PI * 10, contour.Length, 6);
    }

    [Fact]
    public void Group_transforms_are_combined()
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" {Mm100}>
              <g transform="translate(50 10)">
                <g transform="rotate(90) scale(2)">
                  <line x1="0" y1="0" x2="5" y2="0"/>
                </g>
              </g>
            </svg>
            """;

        var contour = Assert.Single(Import(svg));

        // (5,0) → scale → (10,0) → rotate 90° → (0,10) → translate → (50,20); flipped: y = 50 - 20 = 30.
        Assert.True(contour.Start.IsNear(new Vec2(50, 40), 1e-6));
        Assert.True(contour.End.IsNear(new Vec2(50, 30), 1e-6));
    }

    [Fact]
    public void Inkscape_layers_hidden_elements_and_use()
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
                 xmlns:xlink="http://www.w3.org/1999/xlink" {Mm100}>
              <defs><circle id="hole" cx="0" cy="0" r="2"/></defs>
              <g inkscape:groupmode="layer" inkscape:label="Резка">
                <rect x="0" y="0" width="10" height="10"/>
                <use xlink:href="#hole" x="5" y="5"/>
              </g>
              <g inkscape:groupmode="layer" inkscape:label="Скрытый" style="display:none">
                <rect x="20" y="0" width="10" height="10"/>
              </g>
              <text x="0" y="0">Hi</text>
            </svg>
            """;

        var contours = Import(svg, out var result);

        Assert.Equal(2, contours.Count);
        Assert.All(contours, c => Assert.Equal("Резка", c.Layer));
        Assert.Contains(contours, c => c.TryGetCircle(out var center, out var r) && center.IsNear(new Vec2(5, 45), 1e-6) && Math.Abs(r - 2) < 1e-9);
        Assert.Contains(result.Warnings, w => w.Contains("text"));
    }

    [Fact]
    public void Rounded_rect_and_ellipse()
    {
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" {Mm100}><rect x="0" y="0" width="20" height="10" rx="2"/><ellipse cx="50" cy="25" rx="10" ry="5"/></svg>""";

        var contours = Import(svg);

        Assert.Equal(2, contours.Count);
        Assert.All(contours, c => Assert.True(c.IsClosed));
        var rounded = contours.Single(c => c.GetBounds().MaxX < 25);
        Assert.Equal(4, rounded.Segments.OfType<ArcSegment>().Count());
        Assert.Equal(2 * (16 + 6) + 2 * Math.PI * 2, rounded.Length, 6);
        var ellipse = contours.Single(c => c.GetBounds().MinX > 35);
        Assert.Equal(20, ellipse.GetBounds().Width, 3);
        Assert.Equal(10, ellipse.GetBounds().Height, 3);
    }

    [Fact]
    public void Transform_list_order_matches_the_spec()
    {
        // translate(10) scale(2): the point is scaled first, then moved.
        var t = SvgReader.ParseTransform("translate(10,0) scale(2)");
        Assert.Equal(new Vec2(12, 0), t.Apply(new Vec2(1, 0)));

        var m = SvgReader.ParseTransform("matrix(1 0 0 1 3 4)");
        Assert.Equal(new Vec2(4, 5), m.Apply(new Vec2(1, 1)));
    }
}

public class StlAndReliefTests
{
    /// <summary>Square pyramid: base 20×20 at z = 0, apex 10 high at the centre.</summary>
    private static float[] Pyramid()
    {
        float[] a = { 0, 0, 0 }, b = { 20, 0, 0 }, c = { 20, 20, 0 }, d = { 0, 20, 0 }, top = { 10, 10, 10 };
        var faces = new[] { (a, b, top), (b, c, top), (c, d, top), (d, a, top), (a, c, b), (a, d, c) };
        return faces.SelectMany(f => f.Item1.Concat(f.Item2).Concat(f.Item3)).ToArray();
    }

    private static byte[] Binary(float[] vertices)
    {
        var count = vertices.Length / 9;
        var bytes = new byte[84 + count * 50];
        BitConverter.GetBytes(count).CopyTo(bytes, 80);
        for (var t = 0; t < count; t++)
        {
            for (var k = 0; k < 9; k++)
            {
                BitConverter.GetBytes(vertices[t * 9 + k]).CopyTo(bytes, 84 + t * 50 + 12 + k * 4);
            }
        }

        return bytes;
    }

    [Fact]
    public void Binary_and_ascii_stl_give_the_same_mesh()
    {
        var vertices = Pyramid();
        var ascii = "solid p\n" + string.Join("\n", Enumerable.Range(0, vertices.Length / 9).Select(t =>
            "facet normal 0 0 0\nouter loop\n" +
            string.Join("\n", Enumerable.Range(0, 3).Select(k => FormattableString.Invariant($"vertex {vertices[t * 9 + k * 3]} {vertices[t * 9 + k * 3 + 1]} {vertices[t * 9 + k * 3 + 2]}"))) +
            "\nendloop\nendfacet")) + "\nendsolid p\n";

        var fromBinary = StlReader.Read(Binary(vertices));
        var fromAscii = StlReader.Read(System.Text.Encoding.ASCII.GetBytes(ascii));

        Assert.Equal(6, fromBinary.TriangleCount);
        Assert.Equal(fromBinary.Vertices, fromAscii.Vertices);
        Assert.Equal((0, 0, 0, 20, 20, 10), fromBinary.Bounds());
    }

    private static (CamProject Project, ReliefOperation Relief, Tool Tool) ReliefProject(ToolKind kind)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        var tool = new Tool { Kind = kind, Diameter = 2, StepDown = 1, StepOverPercent = 40, FeedRate = 600, PlungeRate = 200 };
        project.Tools.Add(tool);
        var relief = new ReliefOperation
        {
            Name = "Relief", ToolId = tool.Id, Source = ReliefSource.Mesh, Mesh = StlMesh.FromVertices(Pyramid(), "p.stl"),
            WidthMm = 20, Depth = 5, Resolution = 0.25, StepOverMm = 0.5,
        };
        project.Operations.Add(relief);
        return (project, relief, tool);
    }

    [Fact]
    public void Mesh_height_map_follows_the_model_top()
    {
        var (_, relief, _) = ReliefProject(ToolKind.BallNose);

        var map = HeightMap.FromMesh(relief, 80, 80);

        // Apex at the top of the relief, edges at the bottom, half way in between half the depth.
        Assert.InRange(map[40, 40], -0.15, 0);
        Assert.InRange(map[0, 40], -5, -4.8);
        Assert.InRange(map[20, 40], -2.65, -2.35);
    }

    [Theory]
    [InlineData(ToolKind.BallNose)]
    [InlineData(ToolKind.EndMill)]
    public void Relief_never_cuts_below_the_surface(ToolKind kind)
    {
        var (project, relief, tool) = ReliefProject(kind);
        project.Stock.Origin = OriginAnchor.Drawing;

        var result = ToolpathGenerator.Generate(project);
        var moves = result.Toolpaths.Single().Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target).ToList();
        var surface = HeightMap.FromMesh(relief, 80, 80);
        var radius = tool.Diameter / 2;

        Assert.NotEmpty(moves);
        Assert.True(moves.Min(m => m.Z) >= -5 - 1e-6);
        foreach (var m in moves.Where((_, i) => i % 7 == 0))
        {
            // Check the cutter surface against every grid cell under it.
            for (var r = 0; r < surface.Rows; r++)
            {
                for (var c = 0; c < surface.Columns; c++)
                {
                    var d = Math.Sqrt(Math.Pow(surface.CellX(c) - m.X, 2) + Math.Pow(surface.CellY(r) - m.Y, 2));
                    if (d > radius)
                    {
                        continue;
                    }

                    var edge = kind == ToolKind.BallNose ? radius - Math.Sqrt(radius * radius - d * d) : 0;
                    Assert.True(m.Z + edge >= surface[c, r] - 0.01, $"Gouge at {m} over cell ({c},{r})");
                }
            }
        }
    }

    [Fact]
    public void Roughing_goes_down_in_levels_and_leaves_an_allowance()
    {
        var (project, relief, _) = ReliefProject(ToolKind.BallNose);
        relief.RoughAllowance = 0.5;

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        var plunges = moves.Where(m => m.Kind == MoveKind.Plunge).Select(m => m.Target.Z).ToList();
        // Five roughing levels (step-down 1 mm, 5 mm deep) and the finishing pass, each entered once.
        Assert.Equal(6, plunges.Count);
        // The first roughing level cuts flat at -1 mm where the model is lower.
        Assert.Contains(moves, m => m.Kind == MoveKind.Cut && Math.Abs(m.Target.Z + 1) < 1e-6);
        // A pyramid has no flat bottom inside the area, so the cutter shape keeps the tip above -5 mm.
        Assert.InRange(moves.Min(m => m.Target.Z), -5, -4.5);
    }

    [Fact]
    public void Image_relief_dark_is_deep()
    {
        var project = new CamProject();
        var tool = new Tool { Kind = ToolKind.BallNose, Diameter = 1, StepDown = 3 };
        project.Tools.Add(tool);
        // 2×1 picture: black left, white right.
        project.Operations.Add(new ReliefOperation
        {
            ToolId = tool.Id, Image = new GrayImage { Width = 2, Height = 1, Pixels = new byte[] { 0, 255 } },
            WidthMm = 10, Depth = 2, Resolution = 0.5, StepOverMm = 0.5, Roughing = false,
        });

        var cuts = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves.Where(m => m.Kind != MoveKind.Rapid).ToList();

        Assert.Equal(-2, cuts.Where(m => m.Target.X < 2).Min(m => m.Target.Z), 2);
        Assert.Equal(0, cuts.Where(m => m.Target.X > 8).Max(m => m.Target.Z), 2);
    }

    [Fact]
    public void Relief_with_mesh_survives_saving()
    {
        var (project, _, _) = ReliefProject(ToolKind.BallNose);

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var relief = Assert.IsType<ReliefOperation>(copy.Operations[0]);
        Assert.Equal(6, relief.Mesh.TriangleCount);
        Assert.Equal(Pyramid(), relief.Mesh.Vertices);
        Assert.Equal(20, relief.HeightMm, 9);
    }
}
