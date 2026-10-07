using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.Core.Tests;

/// <summary>3D rest machining: a smaller tool machines only what the earlier relief operations left above the model.</summary>
public class ReliefRestTests
{
    /// <summary>40 × 20 mm relief, 2 mm deep, with a 2 mm wide valley in the middle (along Y).</summary>
    private static GrayImage Valley()
    {
        var pixels = new byte[40 * 20];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                pixels[y * 40 + x] = x is 19 or 20 ? (byte)0 : (byte)255;
            }
        }

        return new GrayImage { Width = 40, Height = 20, Pixels = pixels };
    }

    private static (CamProject Project, ReliefOperation Big, ReliefOperation RestPass) ValleyProject()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Stock.Origin = OriginAnchor.Drawing;
        project.Stock.Thickness = 10;
        var big = new Tool { Kind = ToolKind.BallNose, Diameter = 6, StepDown = 3, FeedRate = 600, PlungeRate = 200 };
        var small = new Tool { Kind = ToolKind.BallNose, Diameter = 1, StepDown = 3, FeedRate = 300, PlungeRate = 100 };
        project.Tools.AddRange(new[] { big, small });
        ReliefOperation Relief(Tool tool) => new()
        {
            ToolId = tool.Id, Source = ReliefSource.Image, Image = Valley(), WidthMm = 40, Depth = 2, Resolution = 0.2,
            StepOverMm = 0.3, Roughing = false,
        };

        var first = Relief(big);
        var rest = Relief(small);
        rest.RestMachining = true;
        project.Operations.Add(first);
        project.Operations.Add(rest);
        return (project, first, rest);
    }

    /// <summary>Material left above the model after simulating the enabled operations.</summary>
    private static ComparisonResult Leftover(CamProject project)
    {
        var generation = ToolpathGenerator.Generate(project);
        var simulation = new StockSimulation(project, generation, 0.1);
        simulation.RunToEnd();
        return new ModelComparison(project, generation, simulation.Field).Compare(0.1);
    }

    [Fact]
    public void Rest_pass_stays_in_the_valley_the_large_ball_could_not_reach()
    {
        var (project, _, rest) = ValleyProject();

        var result = ToolpathGenerator.Generate(project);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("остатков нет") || w.Contains("nothing left"));
        var cuts = result.Toolpaths.Single(t => t.Operation == rest).Moves.Where(m => m.Kind == MoveKind.Cut).ToList();
        Assert.NotEmpty(cuts);
        // The valley is x = 19…21; the 6 mm ball reaches its walls only from about 2 mm away.
        Assert.All(cuts, m => Assert.InRange(m.Target.X, 16.5, 23.5));
        Assert.Contains(cuts, m => m.Target.Z < -1.5);
    }

    [Fact]
    public void Rest_pass_removes_most_of_what_the_large_ball_left()
    {
        var (project, big, rest) = ValleyProject();
        rest.Enabled = false;
        var afterBig = Leftover(project);

        rest.Enabled = true;
        var afterRest = Leftover(project);

        // The small ball alone over the whole relief, for its own accuracy on the vertical walls of the pixels.
        big.Enabled = false;
        var smallAlone = Leftover(project);

        Assert.True(afterBig.MaxLeftover > 1, $"{afterBig.MaxLeftover:0.###}");
        Assert.True(afterRest.LeftoverPercent < afterBig.LeftoverPercent * 0.5, $"{afterRest.LeftoverPercent:0.#} % vs {afterBig.LeftoverPercent:0.#} %");
        Assert.True(afterRest.MaxGouge <= smallAlone.MaxGouge + 0.02, $"gouge {afterRest.MaxGouge:0.###} vs {smallAlone.MaxGouge:0.###}");
    }

    [Fact]
    public void Rest_pass_cuts_much_less_than_the_same_relief_in_full()
    {
        static double CutLength(List<ToolMove> moves) =>
            moves.Zip(moves.Skip(1)).Where(m => m.Second.Kind == MoveKind.Cut).Sum(m => m.First.Target.XY.DistanceTo(m.Second.Target.XY));

        var (project, _, rest) = ValleyProject();
        var withRest = CutLength(ToolpathGenerator.Generate(project).Toolpaths.Single(t => t.Operation == rest).Moves);

        rest.RestMachining = false;
        var full = CutLength(ToolpathGenerator.Generate(project).Toolpaths.Single(t => t.Operation == rest).Moves);

        Assert.True(withRest * 3 < full, $"{withRest:0} vs {full:0} mm");
    }

    [Fact]
    public void Nothing_left_after_the_same_tool_is_reported()
    {
        var (project, big, rest) = ValleyProject();
        rest.ToolId = big.ToolId;

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("остатков нет") || w.Contains("nothing left"));
        Assert.DoesNotContain(result.Toolpaths, t => t.Operation == rest);
    }

    [Fact]
    public void Without_earlier_operations_the_rest_relief_is_machined_in_full()
    {
        var (project, big, rest) = ValleyProject();
        big.Enabled = false;

        var result = ToolpathGenerator.Generate(project);

        var cuts = result.Toolpaths.Single(t => t.Operation == rest).Moves.Where(m => m.Kind == MoveKind.Cut).ToList();
        Assert.Contains(cuts, m => m.Target.X < 5);
        Assert.Contains(cuts, m => m.Target.X > 35);
    }

    [Fact]
    public void Rest_settings_survive_saving()
    {
        var project = CamProject.CreateDefault();
        project.Operations.Add(new ReliefOperation { RestMachining = true, RestTolerance = 0.08 });

        var copy = Assert.IsType<ReliefOperation>(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Operations.Single());

        Assert.True(copy.RestMachining);
        Assert.Equal(0.08, copy.RestTolerance);
    }
}
