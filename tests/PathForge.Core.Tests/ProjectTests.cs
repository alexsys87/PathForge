using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class ProjectTests
{
    [Fact]
    public void Project_round_trips_through_json()
    {
        var project = CamProject.CreateDefault();
        project.Name = "Тест";
        project.Contours.Add(new Contour(1, new Segment[]
        {
            new LineSegment(new Vec2(0, 0), new Vec2(10, 0)),
            new ArcSegment(new Vec2(10, 5), 5, -Math.PI / 2, Math.PI),
            new LineSegment(new Vec2(10, 10), new Vec2(0, 0)),
        }, "Layer 1"));
        project.Operations.Add(new ProfileOperation { Name = "P", ToolId = project.Tools[0].Id, Side = ProfileSide.Inside, TabCount = 3, ContourIds = { 1 } });
        project.Operations.Add(new PocketOperation { Name = "K", ToolId = project.Tools[1].Id, Allowance = 0.2 });
        project.Operations.Add(new DrillOperation { Name = "D", ToolId = project.Tools[2].Id, PeckDepth = 1.5 });
        project.Machine.SafeZ = 7;

        var json = ProjectSerializer.Serialize(project);
        var copy = ProjectSerializer.Deserialize(json);

        Assert.Equal("Тест", copy.Name);
        Assert.Equal(7, copy.Machine.SafeZ);
        Assert.Equal(3, copy.Tools.Count);
        Assert.Equal(ToolKind.Drill, copy.Tools[2].Kind);
        var contour = Assert.Single(copy.Contours);
        Assert.Equal("Layer 1", contour.Layer);
        Assert.IsType<ArcSegment>(contour.Segments[1]);
        Assert.True(contour.IsClosed);
        var profile = Assert.IsType<ProfileOperation>(copy.Operations[0]);
        Assert.Equal(ProfileSide.Inside, profile.Side);
        Assert.Equal(new[] { 1 }, profile.ContourIds);
        Assert.Equal(0.2, Assert.IsType<PocketOperation>(copy.Operations[1]).Allowance);
        Assert.Equal(1.5, Assert.IsType<DrillOperation>(copy.Operations[2]).PeckDepth);
        Assert.Contains("\"Inside\"", json);
    }

    [Fact]
    public void Newer_format_is_rejected()
    {
        var json = ProjectSerializer.Serialize(new CamProject { FormatVersion = CamProject.CurrentFormatVersion + 1 });
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }
}
