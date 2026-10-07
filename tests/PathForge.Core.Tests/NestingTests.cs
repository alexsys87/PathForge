using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

public class NestingTests
{
    private static Contour Polygon(int id, params (double X, double Y)[] points) =>
        new(id, points.Select((p, i) => (Segment)new LineSegment(new Vec2(p.X, p.Y), new Vec2(points[(i + 1) % points.Length].X, points[(i + 1) % points.Length].Y))).ToList());

    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) =>
        Polygon(id, (x0, y0), (x1, y0), (x1, y1), (x0, y1));

    private static double Distance(Contour a, Contour b)
    {
        var pa = a.Flatten(0.01);
        var pb = b.Flatten(0.01);
        return Math.Min(pa.Min(p => Polyline.DistanceTo(pb, p, closed: true)), pb.Min(p => Polyline.DistanceTo(pa, p, closed: true)));
    }

    [Fact]
    public void Parts_with_holes_fit_on_the_sheet_apart_from_each_other()
    {
        var project = new CamProject();
        for (var i = 0; i < 6; i++)
        {
            // Parts scattered far away, each with a hole that must travel with it.
            project.Contours.Add(Rectangle(2 * i + 1, i * 100, 500, i * 100 + 40, 530));
            project.Contours.Add(Rectangle(2 * i + 2, i * 100 + 10, 510, i * 100 + 20, 520));
        }

        project.Operations.Add(new ProfileOperation { ContourIds = Enumerable.Range(1, 12).ToList() });
        var settings = new NestingSettings { SheetWidth = 300, SheetHeight = 180, Margin = 5, Spacing = 5 };

        var result = Nesting.Arrange(project, Array.Empty<int>(), settings);

        Assert.Equal(6, result.Placed);
        Assert.Equal(0, result.NotPlaced);
        var outers = project.Contours.Where(c => c.Id % 2 == 1).ToList();
        foreach (var part in outers)
        {
            var b = part.GetBounds();
            Assert.True(b.MinX >= 5 - 1e-6 && b.MinY >= 5 - 1e-6 && b.MaxX <= 295 + 1e-6 && b.MaxY <= 175 + 1e-6, $"{b}");
            // The hole stays inside its part.
            var hole = project.Contours.Single(c => c.Id == part.Id + 1).GetBounds();
            Assert.True(hole.MinX > b.MinX && hole.MaxX < b.MaxX && hole.MinY > b.MinY && hole.MaxY < b.MaxY);
        }

        for (var i = 0; i < outers.Count; i++)
        {
            for (var j = i + 1; j < outers.Count; j++)
            {
                Assert.True(Distance(outers[i], outers[j]) >= 5 - 0.01, $"parts {outers[i].Id} and {outers[j].Id} too close");
            }
        }

        Assert.Equal(Enumerable.Range(1, 12), project.Operations[0].ContourIds);
        Assert.InRange(result.UsedPercent, 10, 20);
    }

    [Fact]
    public void Copies_are_added_to_the_operations_of_the_original()
    {
        var project = new CamProject();
        project.Contours.Add(Rectangle(1, 0, 0, 30, 20));
        project.Contours.Add(Rectangle(2, 5, 5, 10, 10));
        project.Operations.Add(new PocketOperation { ContourIds = { 2 } });
        project.Operations.Add(new ProfileOperation { ContourIds = { 1 } });

        var result = Nesting.Arrange(project, new[] { 1, 2 }, new NestingSettings { Copies = 4 });

        Assert.Equal(4, result.Placed);
        Assert.Equal(8, project.Contours.Count);
        Assert.Equal(4, project.Operations[0].ContourIds.Count);
        Assert.Equal(4, project.Operations[1].ContourIds.Count);
        Assert.Equal(project.Contours.Count, project.Contours.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Shapes_interlock_where_rectangles_would_not_fit()
    {
        var project = new CamProject();
        // Two right triangles with 50 mm legs: as rectangles they need 102 mm, turned to each other about 53.
        project.Contours.Add(Polygon(1, (0, 0), (50, 0), (0, 50)));
        project.Contours.Add(Polygon(2, (200, 0), (250, 0), (200, 50)));
        var settings = new NestingSettings { SheetWidth = 60, SheetHeight = 80, Margin = 0, Spacing = 2 };

        var result = Nesting.Arrange(project, Array.Empty<int>(), settings);

        Assert.Equal(2, result.Placed);
        Assert.True(Distance(project.Contours[0], project.Contours[1]) >= 2 - 0.01);
        Assert.All(project.Contours, c => Assert.True(c.GetBounds().MaxX <= 60 + 1e-6 && c.GetBounds().MaxY <= 80 + 1e-6));
    }

    [Fact]
    public void Parts_that_do_not_fit_are_parked_beside_the_sheet()
    {
        var project = new CamProject();
        project.Contours.Add(Rectangle(1, 0, 0, 100, 100));
        project.Contours.Add(Rectangle(2, 0, 200, 100, 300));

        var result = Nesting.Arrange(project, Array.Empty<int>(), new NestingSettings { SheetWidth = 120, SheetHeight = 120, Margin = 5, Spacing = 5 });

        Assert.Equal(1, result.Placed);
        Assert.Equal(1, result.NotPlaced);
        Assert.Contains(result.Warnings, w => w.Contains('1'));
        Assert.Contains(project.Contours, c => c.GetBounds().MinX > 120);
    }

    [Fact]
    public void Text_letters_move_with_their_text_item()
    {
        var project = new CamProject();
        project.Contours.Add(Rectangle(1, 1000, 1000, 1060, 1030));
        var text = new TextItem { X = 1010, Y = 1010 };
        project.Texts.Add(text);
        project.Contours.Add(new Contour(2, Rectangle(0, 1010, 1010, 1015, 1020).Segments) { TextId = text.Id });

        Nesting.Arrange(project, Array.Empty<int>(), new NestingSettings { Margin = 5 });

        var letter = project.Contours.Single(c => c.Id == 2).GetBounds();
        Assert.Equal(letter.MinX, text.X, 6);
        Assert.Equal(letter.MinY, text.Y, 6);
        Assert.True(text.X < 100);
    }

    /// <summary>A 100 × 100 frame with a 70 × 70 window and two small 20 × 20 parts far away.</summary>
    private static CamProject FrameProject()
    {
        var project = new CamProject();
        project.Contours.Add(Rectangle(1, 0, 400, 100, 500));
        project.Contours.Add(Rectangle(2, 15, 415, 85, 485));
        project.Contours.Add(Rectangle(3, 300, 400, 320, 420));
        project.Contours.Add(Rectangle(4, 400, 400, 420, 420));
        project.Operations.Add(new ProfileOperation { ContourIds = { 1, 2, 3, 4 } });
        return project;
    }

    private static bool Inside(Bounds2 inner, Bounds2 outer, double gap) =>
        inner.MinX >= outer.MinX + gap - 0.01 && inner.MaxX <= outer.MaxX - gap + 0.01 &&
        inner.MinY >= outer.MinY + gap - 0.01 && inner.MaxY <= outer.MaxY - gap + 0.01;

    [Fact]
    public void Small_parts_go_into_the_hole_of_a_larger_part_when_holes_are_used()
    {
        var project = FrameProject();
        var settings = new NestingSettings { SheetWidth = 200, SheetHeight = 150, Margin = 5, Spacing = 4, UseHoles = true };

        var result = Nesting.Arrange(project, Array.Empty<int>(), settings);

        Assert.Equal(3, result.Placed);
        var window = project.Contours.Single(c => c.Id == 2).GetBounds();
        var small = project.Contours.Where(c => c.Id is 3 or 4).Select(c => c.GetBounds()).ToList();
        Assert.All(small, b => Assert.True(Inside(b, window, 4), $"{b} not in {window}"));
        // Apart from each other too.
        Assert.True(Distance(project.Contours.Single(c => c.Id == 3), project.Contours.Single(c => c.Id == 4)) >= 4 - 0.01);
        Assert.Contains(result.Warnings, w => w.Contains("отверстиях") || w.Contains("holes"));
    }

    [Fact]
    public void Without_holes_small_parts_stay_outside_the_frame()
    {
        var project = FrameProject();
        var settings = new NestingSettings { SheetWidth = 200, SheetHeight = 150, Margin = 5, Spacing = 4 };

        Nesting.Arrange(project, Array.Empty<int>(), settings);

        var frame = project.Contours.Single(c => c.Id == 1).GetBounds();
        var small = project.Contours.Where(c => c.Id is 3 or 4).Select(c => c.GetBounds()).ToList();
        Assert.All(small, b => Assert.False(b.MaxX > frame.MinX && b.MinX < frame.MaxX && b.MaxY > frame.MinY && b.MinY < frame.MaxY, $"{b} overlaps {frame}"));
    }

    [Fact]
    public void Parts_too_big_for_the_hole_are_placed_outside()
    {
        var project = FrameProject();
        project.Contours[2] = Rectangle(3, 300, 400, 368, 468);
        var settings = new NestingSettings { SheetWidth = 250, SheetHeight = 150, Margin = 5, Spacing = 4, UseHoles = true };

        Nesting.Arrange(project, Array.Empty<int>(), settings);

        var frame = project.Contours.Single(c => c.Id == 1).GetBounds();
        var big = project.Contours.Single(c => c.Id == 3).GetBounds();
        Assert.True(Distance(project.Contours.Single(c => c.Id == 1), project.Contours.Single(c => c.Id == 3)) >= 4 - 0.01);
        Assert.False(big.MinX > frame.MinX && big.MaxX < frame.MaxX && big.MinY > frame.MinY && big.MaxY < frame.MaxY);
    }

    [Fact]
    public void A_part_already_in_a_hole_is_a_part_of_its_own_and_letters_are_never_holes()
    {
        var contours = new List<Contour>
        {
            Rectangle(1, 0, 0, 100, 100),
            Rectangle(2, 10, 10, 90, 90),
            Rectangle(3, 20, 20, 40, 40),
            Rectangle(4, 60, 60, 70, 70),
        };
        contours[3].TextId = "t";

        var parts = Nesting.FindParts(contours, new List<string>(), holesAreThrough: true);

        Assert.Equal(new[] { 1, 3 }, parts.Select(p => p.Contours[0].Id).OrderBy(i => i));
        var frame = parts.Single(p => p.Contours[0].Id == 1);
        Assert.Single(frame.Holes);
        // The letter inside the window belongs to the frame, as an engraving.
        Assert.Contains(frame.Contours, c => c.Id == 4);
        // Without the option everything inside the outer border moves with it as one part.
        Assert.Equal(4, Nesting.FindParts(contours, new List<string>()).Single().Contours.Count);
    }
}
