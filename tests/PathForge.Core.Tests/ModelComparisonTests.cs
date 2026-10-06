using PathForge.Core.Import;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.Core.Tests;

public class ModelComparisonTests
{
    private static float[] Pyramid()
    {
        float[] a = { 0, 0, 0 }, b = { 20, 0, 0 }, c = { 20, 20, 0 }, d = { 0, 20, 0 }, top = { 10, 10, 10 };
        var faces = new[] { (a, b, top), (b, c, top), (c, d, top), (d, a, top), (a, c, b), (a, d, c) };
        return faces.SelectMany(f => f.Item1.Concat(f.Item2).Concat(f.Item3)).ToArray();
    }

    private static (CamProject Project, ReliefOperation Relief) PyramidProject()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 10;
        var tool = new Tool { Kind = ToolKind.BallNose, Diameter = 2, StepDown = 2, StepOverPercent = 40, FeedRate = 600, PlungeRate = 200 };
        project.Tools.Add(tool);
        var relief = new ReliefOperation
        {
            ToolId = tool.Id, Source = ReliefSource.Mesh, Mesh = StlMesh.FromVertices(Pyramid(), "p.stl"),
            WidthMm = 20, Depth = 5, Resolution = 0.25, StepOverMm = 0.3,
        };
        project.Operations.Add(relief);
        return (project, relief);
    }

    [Fact]
    public void Finished_relief_matches_its_model_without_gouges()
    {
        var (project, _) = PyramidProject();
        var generation = ToolpathGenerator.Generate(project);
        var simulation = new StockSimulation(project, generation, 0.1);
        var comparison = new ModelComparison(project, generation, simulation.Field);

        var before = comparison.Compare(0.1);
        simulation.RunToEnd();
        var after = comparison.Compare(0.1);

        Assert.True(comparison.HasModel);
        // Untouched stock: everything is material left above the model, up to the full depth.
        Assert.InRange(before.MaxLeftover, 4.9, 5.01);
        Assert.Equal(0, before.MaxGouge);
        // Machined: no gouges beyond the grid accuracy; scallops between lines stay within a fraction of a mm.
        Assert.True(after.MaxGouge < 0.15, $"gouge {after.MaxGouge}");
        Assert.True(after.WithinPercent > 80, $"within {after.WithinPercent}");
        Assert.True(after.GougePercent < 1, $"gouged {after.GougePercent}");
    }

    [Fact]
    public void A_dent_in_the_stock_is_reported_as_a_gouge()
    {
        var (project, _) = PyramidProject();
        var generation = ToolpathGenerator.Generate(project);
        var simulation = new StockSimulation(project, generation, 0.1);
        simulation.RunToEnd();
        var comparison = new ModelComparison(project, generation, simulation.Field);
        var field = simulation.Field;
        var (i, j) = ((int)((10 - field.Origin.X) / field.CellSize), (int)((10 - field.Origin.Y) / field.CellSize));
        field.Heights[j * field.Width + i] -= 1;

        var result = comparison.Compare(0.1);

        Assert.InRange(result.MaxGouge, 0.9, 1.15);
        var display = field.Downsample(Math.Max(field.Width, field.Height) / 4);
        var shown = ModelComparison.Downsample(field, result.Deviation, display);
        Assert.Contains(shown, d => d < -0.9);
    }

    [Fact]
    public void Projects_without_a_relief_have_no_model()
    {
        var project = new CamProject();
        var generation = new GenerationResult();
        var field = new HeightField(new Geometry.Vec2(0, 0), 10, 10, 1, 0, -5);

        Assert.False(new ModelComparison(project, generation, field).HasModel);
    }
}
