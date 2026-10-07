using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.Core.Tests;

/// <summary>Rest machining of pockets and profiles by the 3D simulation of the operations above.</summary>
public class StockRestTests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1)
    {
        var p = new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };
        return new Contour(id, p.Select((a, i) => (Segment)new LineSegment(a, p[(i + 1) % 4])).ToList());
    }

    private static CamProject Project(params Contour[] contours)
    {
        var project = new CamProject();
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 10;
        project.Contours.AddRange(contours);
        return project;
    }

    private static Tool AddTool(CamProject project, double diameter)
    {
        var tool = new Tool { Diameter = diameter, StepDown = 1, StepOverPercent = 40, FeedRate = 300, PlungeRate = 100 };
        project.Tools.Add(tool);
        return tool;
    }

    private static List<Vec3> Cuts(Toolpath toolpath) =>
        toolpath.Moves.Where(m => m.Kind == MoveKind.Cut && m.Target.Z < 0).Select(m => m.Target).ToList();

    /// <summary>Cuts on the flat of a pass (not the ramps going down to it).</summary>
    private static List<Vec3> FlatCuts(Toolpath toolpath) =>
        toolpath.Moves.Zip(toolpath.Moves.Skip(1))
            .Where(m => m.Second.Kind == MoveKind.Cut && m.Second.Target.Z < 0 && Math.Abs(m.First.Target.Z - m.Second.Target.Z) < 1e-9)
            .Select(m => m.Second.Target).ToList();

    [Fact]
    public void Pocket_rest_by_simulation_cuts_only_the_corners_the_larger_tool_left()
    {
        var project = Project(Rectangle(1, 0, 0, 20, 20));
        var big = AddTool(project, 6);
        var small = AddTool(project, 2);
        project.Operations.Add(new PocketOperation { ToolId = big.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new PocketOperation { ToolId = small.Id, Depth = 1, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Toolpaths.Count);
        var cuts = Cuts(result.Toolpaths[1]);
        Assert.NotEmpty(cuts);
        var corners = new[] { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, 20), new Vec2(0, 20) };
        Assert.All(cuts, p => Assert.True(corners.Min(c => c.DistanceTo(p.XY)) < 4.5, $"{p.X:0.##}, {p.Y:0.##}"));

        // After both operations the corners are as deep as the pocket, up to the small tool's radius.
        var simulation = new StockSimulation(project, result, 0.05);
        simulation.RunToEnd();
        foreach (var corner in new[] { new Vec2(0.5, 0.5), new Vec2(19.5, 0.5), new Vec2(19.5, 19.5), new Vec2(0.5, 19.5) })
        {
            Assert.True(simulation.Field.HeightAt(corner) < -0.95, $"{corner}: {simulation.Field.HeightAt(corner):0.###}");
        }
    }

    [Fact]
    public void Pocket_rest_by_simulation_clears_in_full_below_the_depth_of_the_operation_above()
    {
        var project = Project(Rectangle(1, 0, 0, 20, 20));
        var big = AddTool(project, 6);
        var small = AddTool(project, 2);
        project.Operations.Add(new PocketOperation { ToolId = big.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new PocketOperation { ToolId = small.Id, Depth = 3, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = FlatCuts(result.Toolpaths[1]);
        // The first level only in the corners, the deeper ones all over the pocket.
        Assert.All(cuts.Where(p => Math.Abs(p.Z + 1) < 1e-9), p => Assert.True(p.X < 5 || p.X > 15 || p.Y < 5 || p.Y > 15, $"{p}"));
        Assert.Contains(cuts, p => Math.Abs(p.Z + 3) < 1e-9 && p.XY.DistanceTo(new Vec2(10, 10)) < 3);
        Assert.Contains(cuts, p => Math.Abs(p.Z + 3) < 1e-9 && Math.Abs(p.X - 1) < 1e-6);

        // Moves between the pieces never go through material at rapid speed.
        var simulation = new StockSimulation(project, result, 0.1);
        simulation.RunToEnd();
        Assert.Equal(0, simulation.RapidHits);
        Assert.True(simulation.Field.HeightAt(new Vec2(0.5, 0.5)) < -2.95);
    }

    [Fact]
    public void Pocket_rest_by_simulation_after_the_same_tool_finds_nothing_left()
    {
        var project = Project(Rectangle(1, 0, 0, 20, 20));
        var tool = AddTool(project, 2);
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Single(result.Toolpaths);
        Assert.Contains(result.Warnings, w => w.Contains("остатков нет") || w.Contains("nothing left"));
    }

    [Fact]
    public void Rest_by_simulation_without_milling_above_machines_in_full_with_a_warning()
    {
        var project = Project(Rectangle(1, 0, 0, 20, 20));
        var tool = AddTool(project, 2);
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Single(result.Toolpaths);
        Assert.Contains(FlatCuts(result.Toolpaths[0]), p => p.XY.DistanceTo(new Vec2(10, 10)) < 3);
        Assert.Contains(result.Warnings, w => w.Contains("полностью") || w.Contains("in full"));
    }

    [Fact]
    public void Profile_rest_by_simulation_cuts_only_the_passes_below_the_operation_above()
    {
        var project = Project(Rectangle(1, 0, 0, 30, 20));
        var tool = AddTool(project, 3);
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 4, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = FlatCuts(result.Toolpaths[1]);
        Assert.DoesNotContain(cuts, p => p.Z > -2.5);
        // The deeper passes go all the way round, 1.5 mm outside the part.
        var bottom = cuts.Where(p => Math.Abs(p.Z + 4) < 1e-9).ToList();
        Assert.Contains(bottom, p => Math.Abs(p.X + 1.5) < 0.01);
        Assert.Contains(bottom, p => Math.Abs(p.X - 31.5) < 0.01);
        Assert.Contains(bottom, p => Math.Abs(p.Y + 1.5) < 0.01);
        Assert.Contains(bottom, p => Math.Abs(p.Y - 21.5) < 0.01);

        var simulation = new StockSimulation(project, result, 0.1);
        simulation.RunToEnd();
        Assert.Equal(0, simulation.RapidHits);
    }

    private static double DistanceToPart(Vec2 p)
    {
        var dx = Math.Max(Math.Max(-p.X, 0), p.X - 30);
        var dy = Math.Max(Math.Max(-p.Y, 0), p.Y - 20);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [Fact]
    public void Profile_rest_by_simulation_takes_off_the_allowance_left_on_the_wall()
    {
        var project = Project(Rectangle(1, 0, 0, 30, 20));
        var tool = AddTool(project, 3);
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 }, Allowance = 0.5 });
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Empty(result.Warnings);
        var cuts = FlatCuts(result.Toolpaths[1]);
        foreach (var z in new[] { -1.0, -2.0 })
        {
            var level = cuts.Where(p => Math.Abs(p.Z - z) < 1e-9).ToList();
            Assert.True(level.Count > 4, $"z {z}");
            // The full tool path, 1.5 mm from the part (rounded at the corners).
            Assert.All(level, p => Assert.Equal(1.5, DistanceToPart(p.XY), 1));
        }
    }

    [Fact]
    public void Profile_rest_by_simulation_after_the_same_cut_finds_nothing_left()
    {
        var project = Project(Rectangle(1, 0, 0, 30, 20));
        var tool = AddTool(project, 3);
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 }, RestFromStock = true });

        var result = ToolpathGenerator.Generate(project);

        Assert.Single(result.Toolpaths);
        Assert.Contains(result.Warnings, w => w.Contains("остатков нет") || w.Contains("nothing left"));
    }

    [Fact]
    public void Profile_rest_by_simulation_keeps_its_tabs()
    {
        var project = Project(Rectangle(1, 0, 0, 30, 20));
        var tool = AddTool(project, 3);
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 4, ContourIds = { 1 }, RestFromStock = true, TabCount = 2, TabWidth = 4, TabHeight = 1 });

        var result = ToolpathGenerator.Generate(project);

        var simulation = new StockSimulation(project, result, 0.1);
        simulation.RunToEnd();
        // Along the outside of the part: through to -4 except over the two tabs, which stay at -3.
        var heights = Enumerable.Range(0, 300).Select(k => simulation.Field.HeightAt(new Vec2(k * 0.1 + 0.05, -0.8))).ToList();
        Assert.Contains(heights, h => h < -3.95);
        Assert.Contains(heights, h => Math.Abs(h + 3) < 0.05);
        Assert.DoesNotContain(heights, h => h > -2.95);
        Assert.Equal(0, simulation.RapidHits);
    }

    [Fact]
    public void Flagged_runs_wrap_around_a_closed_path_and_overlap_by_a_point()
    {
        var runs = ToolpathGenerator.FlaggedRuns(new[] { true, true, false, false, true, false, true }, closed: true);

        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.SequenceEqual(new[] { 3, 4, 5 }));
        Assert.Contains(runs, r => r.SequenceEqual(new[] { 5, 6, 0, 1, 2 }));

        var open = ToolpathGenerator.FlaggedRuns(new[] { true, false, false, true }, closed: false);
        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2, 3 } }, open.Select(r => r.ToArray()));

        Assert.Equal(new[] { 0, 1, 2, 0 }, ToolpathGenerator.FlaggedRuns(new[] { true, true, true }, closed: true).Single());
    }

    [Fact]
    public void Rest_by_simulation_settings_survive_saving()
    {
        var project = new CamProject();
        project.Operations.Add(new PocketOperation { RestFromStock = true, RestTolerance = 0.1 });
        project.Operations.Add(new ProfileOperation { RestFromStock = true, RestTolerance = 0.2 });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var pocket = Assert.IsType<PocketOperation>(copy.Operations[0]);
        Assert.True(pocket.RestFromStock);
        Assert.Equal(0.1, pocket.RestTolerance);
        var profile = Assert.IsType<ProfileOperation>(copy.Operations[1]);
        Assert.True(profile.RestFromStock);
        Assert.Equal(0.2, profile.RestTolerance);
    }
}
