using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Relief limited by contours and waterline finishing only on steep areas.</summary>
public class ReliefAreaTests
{
    private static Contour Square(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    /// <summary>Picture relief 20 × 10 mm: flat top on the left, a steep step in the middle, flat bottom on the right.</summary>
    private static (CamProject Project, ReliefOperation Relief) StepProject()
    {
        var project = new CamProject();
        var tool = new Tool { Kind = ToolKind.BallNose, Diameter = 1, StepDown = 10, FeedRate = 600, PlungeRate = 200 };
        project.Tools.Add(tool);
        var pixels = new byte[20 * 10];
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                pixels[y * 20 + x] = x < 10 ? (byte)255 : x < 12 ? (byte)(255 - (x - 9) * 85) : (byte)0;
            }
        }

        var relief = new ReliefOperation
        {
            ToolId = tool.Id, Image = new GrayImage { Width = 20, Height = 10, Pixels = pixels },
            WidthMm = 20, Depth = 5, Resolution = 0.25, StepOverMm = 0.5, Roughing = false,
        };
        project.Operations.Add(relief);
        return (project, relief);
    }

    private static List<Vec3> Cuts(CamProject project) =>
        ToolpathGenerator.Generate(project).Toolpaths.Single().Moves.Where(m => m.Kind == MoveKind.Cut).Select(m => m.Target).ToList();

    [Fact]
    public void Relief_stays_inside_the_selected_contour()
    {
        var (project, relief) = StepProject();
        project.Contours.Add(Square(1, 4, 2, 16, 8));
        relief.ContourIds = new List<int> { 1 };

        var everywhere = Cuts(project);
        relief.LimitToContours = true;
        var limited = Cuts(project);

        Assert.Contains(everywhere, p => p.X < 3 || p.Y > 9);
        Assert.NotEmpty(limited);
        Assert.All(limited, p =>
        {
            Assert.InRange(p.X, 4 - 1e-6, 16 + 1e-6);
            Assert.InRange(p.Y, 2 - 1e-6, 8 + 1e-6);
        });
        // The step is still machined to its full depth inside the area.
        Assert.InRange(limited.Min(p => p.Z), -5.01, -4.9);
    }

    [Fact]
    public void Missing_contour_falls_back_to_the_whole_relief_with_a_warning()
    {
        var (project, relief) = StepProject();
        relief.LimitToContours = true;

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("контур", StringComparison.OrdinalIgnoreCase) || w.Contains("contour", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Toolpaths.Single().Moves, m => m.Kind == MoveKind.Cut && m.Target.X < 3);
    }

    [Fact]
    public void Waterline_runs_only_on_the_steep_step()
    {
        var (project, relief) = StepProject();
        relief.Finishing = ReliefFinishing.Waterline;
        relief.WaterlineStepZ = 0.5;

        var everywhere = Cuts(project);
        relief.SteepAngle = 30;
        var steep = Cuts(project);

        // Without the limit every level also runs around the flat top, along the left edge of the relief.
        Assert.Contains(everywhere, p => p.X < 2);
        Assert.NotEmpty(steep);
        // With it only the step (x 10…12 mm, widened by the tool and one line step) is left.
        Assert.All(steep, p => Assert.InRange(p.X, 8, 14));
        Assert.Contains(steep, p => p.Z < -4);
    }

    [Fact]
    public void Steep_mask_grows_by_the_given_cells()
    {
        var map = new HeightMap(10, 1, 1, 0, 0);
        map[5, 0] = -10;

        var exact = ToolpathGenerator.SteepMask(map, 45, 0);
        var grown = ToolpathGenerator.SteepMask(map, 45, 1);

        Assert.Equal(new[] { false, false, false, false, true, false, true, false, false, false }, exact);
        Assert.Equal(new[] { false, false, false, true, true, true, true, true, false, false }, grown);
    }
}
