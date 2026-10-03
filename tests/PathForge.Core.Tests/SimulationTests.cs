using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.Core.Tests;

public class SimulationTests
{
    private static CamProject NewProject(Tool tool, Operation operation, params Contour[] contours)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 10;
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        operation.ToolId = tool.Id;
        operation.ContourIds.AddRange(contours.Select(c => c.Id));
        project.Operations.Add(operation);
        return project;
    }

    private static Contour Line(double x0, double y0, double x1, double y1, int id = 1) =>
        new(id, new Segment[] { new LineSegment(new Vec2(x0, y0), new Vec2(x1, y1)) });

    private static Contour Rectangle(double x0, double y0, double x1, double y1) => new(1, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static StockSimulation Simulate(CamProject project, double cell = 0.05)
    {
        var simulation = new StockSimulation(project, ToolpathGenerator.Generate(project), cell);
        simulation.RunToEnd();
        return simulation;
    }

    [Fact]
    public void End_mill_cuts_a_flat_slot_as_wide_as_the_tool()
    {
        var tool = new Tool { Diameter = 3, StepDown = 1 };
        var simulation = Simulate(NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 2 }, Line(0, 0, 40, 0)));
        var field = simulation.Field;

        Assert.Equal(-2, field.HeightAt(new Vec2(20, 0)), 3);
        Assert.Equal(-2, field.HeightAt(new Vec2(20, 1.3)), 3);
        Assert.Equal(0, field.HeightAt(new Vec2(20, 1.7)), 3);
        Assert.Equal(0, field.HeightAt(new Vec2(20, -1.7)), 3);
        Assert.Empty(simulation.Issues);
        Assert.True(simulation.IsFinished);
        Assert.Equal(simulation.TotalTime, simulation.CurrentTime, 9);
    }

    [Fact]
    public void Ball_nose_leaves_a_round_bottom()
    {
        var tool = new Tool { Kind = ToolKind.BallNose, Diameter = 4, StepDown = 2 };
        var field = Simulate(NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 2 }, Line(0, 0, 40, 0))).Field;

        Assert.Equal(-2, field.HeightAt(new Vec2(20, 0.01)), 2);
        Assert.Equal(-2 + (2 - Math.Sqrt(3)), field.HeightAt(new Vec2(20, 1.0)), 1);
        Assert.Equal(0, field.HeightAt(new Vec2(20, 2.1)), 3);
    }

    [Fact]
    public void V_bit_cuts_a_groove_that_widens_with_depth()
    {
        var tool = new Tool { Kind = ToolKind.VBit, Diameter = 6, TipDiameter = 0, TipAngle = 90, StepDown = 1 };
        var field = Simulate(NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 1 }, Line(0, 0, 40, 0))).Field;

        Assert.Equal(-1, field.HeightAt(new Vec2(20, 0.01)), 1);
        Assert.Equal(-0.5, field.HeightAt(new Vec2(20, 0.5)), 1);
        Assert.Equal(0, field.HeightAt(new Vec2(20, 1.1)), 3);
    }

    [Fact]
    public void Pocket_removes_the_expected_volume()
    {
        var tool = new Tool { Diameter = 3, StepDown = 1, StepOverPercent = 40 };
        var simulation = Simulate(NewProject(tool, new PocketOperation { Depth = 2 }, Rectangle(0, 0, 20, 10)));

        // 20 × 10 × 2 minus the rounded inside corners (radius 1.5).
        var expected = 20 * 10 * 2 - 4 * (1 - Math.PI / 4) * 1.5 * 1.5 * 2;
        Assert.InRange(simulation.Field.RemovedVolume(), expected * 0.98, expected * 1.02);
        Assert.Equal(-2, simulation.Field.HeightAt(new Vec2(10, 5)), 3);
        Assert.Equal(0, simulation.Field.HeightAt(new Vec2(-0.2, 5)), 3);
    }

    [Fact]
    public void Rapid_moves_through_material_are_reported()
    {
        var project = new CamProject();
        project.Stock.Thickness = 10;
        var tool = new Tool { Diameter = 3 };
        var operation = new ProfileOperation { Name = "Тест" };
        var generation = new GenerationResult { SafeZ = 5 };
        generation.Toolpaths.Add(new Toolpath(operation, tool, new List<ToolMove>
        {
            new(MoveKind.Rapid, new Vec3(0, 0, 5)),
            new(MoveKind.Plunge, new Vec3(0, 0, -1)),
            new(MoveKind.Cut, new Vec3(5, 0, -1)),
            new(MoveKind.Rapid, new Vec3(15, 0, -1)),
            new(MoveKind.Rapid, new Vec3(15, 0, 5)),
        }));

        var simulation = new StockSimulation(project, generation, 0.1);
        simulation.RunToEnd();

        Assert.Equal(1, simulation.RapidHits);
        var issue = Assert.Single(simulation.Issues);
        Assert.Contains("Тест", issue);
        Assert.Contains("G0", issue);
    }

    [Fact]
    public void Cutting_below_the_stock_is_reported_once_per_operation()
    {
        var tool = new Tool { Diameter = 3, StepDown = 4 };
        var simulation = Simulate(NewProject(tool, new ProfileOperation { Name = "Вырезка", Depth = 10.5 }, Rectangle(0, 0, 20, 10)), cell: 0.2);

        var issue = Assert.Single(simulation.Issues);
        Assert.Contains("Вырезка", issue);
        Assert.Contains("0,5", issue.Replace('.', ','));
        // The part is cut free: the slot reaches the bottom.
        Assert.Equal(-10, Math.Max(simulation.Field.HeightAt(new Vec2(-1.5, 5)), simulation.Field.Bottom), 3);
    }

    [Fact]
    public void Zero_at_the_table_moves_the_stock_up()
    {
        var tool = new Tool { Diameter = 3, StepDown = 1 };
        var project = NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 2 }, Line(0, 0, 40, 0));
        project.Stock.ZeroAtBottom = true;

        var simulation = Simulate(project, 0.1);

        Assert.Equal(10, simulation.Field.Top);
        Assert.Equal(0, simulation.Field.Bottom);
        Assert.Equal(8, simulation.Field.HeightAt(new Vec2(20, 0)), 3);
    }

    [Fact]
    public void Step_by_step_playback_gives_the_same_result_and_can_go_back()
    {
        var tool = new Tool { Diameter = 3, StepDown = 1, StepOverPercent = 40 };
        var project = NewProject(tool, new PocketOperation { Depth = 2, Entry = EntryMode.Ramp }, Rectangle(0, 0, 20, 10));
        var generation = ToolpathGenerator.Generate(project);
        var full = new StockSimulation(project, generation, 0.1);
        full.RunToEnd();

        var stepped = new StockSimulation(project, generation, 0.1);
        var halfway = stepped.TotalTime / 2;
        Assert.True(stepped.AdvanceTo(halfway, null));
        Assert.Equal(halfway, stepped.CurrentTime, 9);
        Assert.False(stepped.IsFinished);
        var removedHalf = stepped.Field.RemovedVolume();
        Assert.InRange(removedHalf, 1, full.Field.RemovedVolume() - 1);

        // Many tiny budgets, as during playback.
        var guard = 0;
        while (!stepped.AdvanceTo(stepped.TotalTime, TimeSpan.Zero) && guard++ < 100000)
        {
        }

        Assert.True(stepped.IsFinished);
        Assert.Equal(full.Field.Heights, stepped.Field.Heights);

        // Going back restarts from the untouched stock.
        stepped.AdvanceTo(halfway, null);
        Assert.Equal(removedHalf, stepped.Field.RemovedVolume(), 6);
    }

    [Fact]
    public void Laser_burns_the_surface_without_removing_material()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Laser.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        var laser = new Tool { Kind = ToolKind.Laser, Diameter = 0.2 };
        project.Tools.Add(laser);
        project.Contours.Add(Rectangle(0, 0, 20, 10));
        project.Operations.Add(new LaserVectorOperation { ToolId = laser.Id, PowerPercent = 50, ContourIds = { 1 } });

        var simulation = Simulate(project, 0.1);

        Assert.Equal(0, simulation.Field.RemovedVolume(), 6);
        var i = (int)Math.Floor((10 - simulation.Field.Origin.X) / simulation.Field.CellSize);
        var j = (int)Math.Floor((0 - simulation.Field.Origin.Y) / simulation.Field.CellSize);
        Assert.InRange(simulation.Field.Burn[j * simulation.Field.Width + i], 120, 135);
        var center = (int)Math.Floor((5 - simulation.Field.Origin.Y) / simulation.Field.CellSize) * simulation.Field.Width + i;
        Assert.Equal(0, simulation.Field.Burn[center]);
    }

    [Fact]
    public void Display_grid_keeps_narrow_grooves()
    {
        var tool = new Tool { Kind = ToolKind.VBit, Diameter = 3, TipDiameter = 0.1, TipAngle = 30, StepDown = 1 };
        var simulation = Simulate(NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 0.2 }, Line(0, 0, 40, 0)));

        var coarse = simulation.Field.Downsample(100);

        Assert.True(coarse.Width <= 100 && coarse.Height <= 100);
        Assert.InRange(coarse.Heights.Min(), -0.21, -0.15);
        Assert.Equal(simulation.Field.Heights.Min(), coarse.Heights.Min());
    }

    [Fact]
    public void Cell_count_is_limited_for_large_stock()
    {
        var tool = new Tool { Diameter = 3, StepDown = 1 };
        var project = NewProject(tool, new ProfileOperation { Side = ProfileSide.OnLine, Depth = 1 }, Line(0, 0, 280, 170));

        var simulation = new StockSimulation(project, ToolpathGenerator.Generate(project), maxCells: 200_000);

        Assert.True((long)simulation.Field.Width * simulation.Field.Height <= 220_000);
        Assert.True(simulation.Field.SizeX >= 280 && simulation.Field.SizeY >= 170);
    }
}
