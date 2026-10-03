using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Behaviour needed for GRBL hobby machines such as the CNC 3018.</summary>
public class Cnc3018Tests
{
    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    });

    private static (CamProject Project, Tool Tool) Project3018(params Contour[] contours)
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        var tool = ToolPresets.Cnc3018[0].Create(1);
        project.Tools.Add(tool);
        project.Contours.AddRange(contours);
        return (project, tool);
    }

    private static string[] Lines(string gcode) => gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData(10000, "M3 S1000")]
    [InlineData(5000, "M3 S500")]
    [InlineData(7500, "M3 S750")]
    [InlineData(15000, "M3 S1000")]
    public void Spindle_rpm_is_scaled_to_grbl_s_range(double rpm, string expected)
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 20, 20));
        tool.SpindleRpm = rpm;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(expected, Lines(GcodeWriter.Write("p", result, project.Machine)));
        Assert.Equal(rpm > 10000, result.Warnings.Any(w => w.Contains("Обороты")));
    }

    [Fact]
    public void Spindle_500w_profile_uses_its_own_max_rpm()
    {
        var machine = new MachineSettings();
        MachineProfiles.Cnc3018Spindle500W.ApplyTo(machine);

        Assert.Equal(500, machine.SpindleWord(6000));
        Assert.Equal(GcodeDialect.Grbl, machine.Dialect);
    }

    [Fact]
    public void Generic_dialect_writes_rpm_unchanged()
    {
        Assert.Equal(18000, new MachineSettings().SpindleWord(18000));
    }

    [Fact]
    public void Grbl_output_has_short_ascii_lines_and_no_m6()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 20, 20));
        project.Machine.UseToolChange = true;
        var second = ToolPresets.Cnc3018[1].Create(2);
        project.Tools.Add(second);
        var veryLongName = "Очень длинное название операции, чтобы проверить ограничение длины строки GRBL (80 символов)";
        project.Operations.Add(new ProfileOperation { Name = veryLongName, ToolId = tool.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { Name = "Вторая фреза", ToolId = second.Id, Depth = 1, ContourIds = { 1 } });

        var gcode = GcodeWriter.Write("Проект", ToolpathGenerator.Generate(project), project.Machine);
        var lines = Lines(gcode);

        Assert.All(lines, l => Assert.True(l.Length <= 80, $"Line too long: {l}"));
        Assert.All(lines, l => Assert.True(l.All(c => c is >= ' ' and <= '~'), $"Non-ASCII line: {l}"));
        Assert.DoesNotContain(lines, l => l.Contains("M6"));
        Assert.Contains(lines, l => l.StartsWith("M0 (") && l.Contains("T2"));
        Assert.Contains("(Proekt)", lines);
        Assert.Contains(lines, l => l.StartsWith("(Ochen dlinnoe"));
    }

    [Fact]
    public void Ascii_transliteration_keeps_meaning()
    {
        Assert.Equal("Freza 1-zakhodnaya D3,175", GcodeWriter.ToAscii("Фреза 1-заходная Ø3,175"));
    }

    [Fact]
    public void Program_larger_than_the_work_area_is_reported()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 400, 100));
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.OnLine, Depth = 1, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);

        Assert.Contains(result.Warnings, w => w.Contains("по X"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("по Y"));
    }

    [Fact]
    public void Feed_above_machine_limit_is_reported()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 20, 20));
        tool.FeedRate = 2500;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 1, ContourIds = { 1 } });

        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("Подача"));
    }

    [Fact]
    public void Cutting_deeper_than_the_stock_is_reported()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 20, 20));
        project.Stock.Thickness = 4;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 4.3, ContourIds = { 1 } });
        Assert.DoesNotContain(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("толщины"));

        project.Operations[0].Depth = 6;
        Assert.Contains(ToolpathGenerator.Generate(project).Warnings, w => w.Contains("толщины"));
    }

    [Theory]
    [InlineData(OriginAnchor.Drawing, 50, 20)]
    [InlineData(OriginAnchor.LowerLeft, 0, 0)]
    [InlineData(OriginAnchor.UpperRight, -40, -30)]
    [InlineData(OriginAnchor.Center, -20, -15)]
    public void Work_zero_moves_the_program(OriginAnchor anchor, double minX, double minY)
    {
        var (project, tool) = Project3018(Rectangle(1, 50, 20, 90, 50));
        project.Stock.Origin = anchor;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Side = ProfileSide.OnLine, Depth = 1, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);
        var cuts = result.Toolpaths.SelectMany(t => t.Moves).Where(m => m.Kind == MoveKind.Cut).Select(m => m.Target).ToList();

        Assert.Equal(minX, cuts.Min(p => p.X), 6);
        Assert.Equal(minY, cuts.Min(p => p.Y), 6);
    }

    [Fact]
    public void Zero_at_the_bottom_lifts_all_z_by_the_stock_thickness()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 20, 20));
        project.Stock.Thickness = 10;
        project.Stock.ZeroAtBottom = true;
        project.Operations.Add(new ProfileOperation { ToolId = tool.Id, Depth = 3, ContourIds = { 1 } });

        var result = ToolpathGenerator.Generate(project);
        var zs = result.Toolpaths.SelectMany(t => t.Moves).Select(m => m.Target.Z).ToList();
        var gcode = Lines(GcodeWriter.Write("p", result, project.Machine));

        Assert.Equal(7, zs.Min(), 6);
        Assert.Equal(15, zs.Max(), 6);
        Assert.Equal(15, result.SafeZ, 6);
        Assert.Contains("G0 Z15", gcode);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void Ramp_entry_never_plunges_into_the_material_and_respects_the_angle(double angle)
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 30, 20));
        project.Operations.Add(new ProfileOperation
        {
            ToolId = tool.Id, Depth = 3, ContourIds = { 1 }, Entry = EntryMode.Ramp, RampAngle = angle,
        });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        var previous = new Vec3(0, 0, project.Machine.SafeZ);
        var maxSlope = Math.Tan(angle * Math.PI / 180) * 1.0001;
        foreach (var move in moves)
        {
            var t = move.Target;
            if (t.Z < previous.Z - 1e-9 && t.Z < -1e-9)
            {
                Assert.NotEqual(MoveKind.Plunge, move.Kind);
                var horizontal = t.XY.DistanceTo(previous.XY);
                Assert.True((previous.Z - t.Z) <= horizontal * maxSlope + 1e-9, $"Too steep at {t}");
            }

            previous = t;
        }

        Assert.Equal(-3, moves.Min(m => m.Target.Z), 9);
    }

    [Fact]
    public void Ramp_stays_in_front_of_the_first_tab()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 40, 40));
        project.Operations.Add(new ProfileOperation
        {
            ToolId = tool.Id, Side = ProfileSide.OnLine, Depth = 3, ContourIds = { 1 },
            Entry = EntryMode.Ramp, RampAngle = 2, TabCount = 4, TabWidth = 4, TabHeight = 1.5,
        });

        var result = ToolpathGenerator.Generate(project);
        var moves = result.Toolpaths.Single().Moves;

        Assert.Empty(result.Warnings);
        // The loop is 160 mm long and starts at (0,0); the first tab begins 20 - (4 + 3.175) / 2 mm along it.
        var firstTabStart = 20 - (4 + tool.Diameter) / 2;
        var start = new Vec2(0, 0);
        var previous = new Vec3(0, 0, project.Machine.SafeZ);
        var rampMoves = 0;
        foreach (var move in moves)
        {
            var t = move.Target;
            var isRamp = t.Z < previous.Z - 1e-9 && !t.XY.IsNear(previous.XY, 1e-9);
            if (isRamp)
            {
                rampMoves++;
                Assert.True(t.XY.DistanceTo(start) <= firstTabStart + 1e-6, $"Ramp reaches the tab at {t}");
            }

            previous = t;
        }

        Assert.True(rampMoves > 0);
    }

    [Fact]
    public void Pocket_ramps_into_every_ring_that_needs_a_new_entry()
    {
        var (project, tool) = Project3018(Rectangle(1, 0, 0, 30, 30));
        project.Operations.Add(new PocketOperation { ToolId = tool.Id, Depth = 2, ContourIds = { 1 }, Entry = EntryMode.Ramp });

        var moves = ToolpathGenerator.Generate(project).Toolpaths.Single().Moves;

        Assert.DoesNotContain(moves, m => m.Kind == MoveKind.Plunge && m.Target.Z < -1e-9);
        Assert.Equal(-2, moves.Min(m => m.Target.Z), 9);
    }

    [Fact]
    public void Presets_have_sane_values_and_default_project_targets_the_3018()
    {
        Assert.All(ToolPresets.Cnc3018, p =>
        {
            Assert.InRange(p.Template.Diameter, 0.1, 6.35);
            Assert.InRange(p.Template.FeedRate, 50, 1000);
            Assert.InRange(p.Template.PlungeRate, 20, p.Template.FeedRate);
            Assert.InRange(p.Template.StepDown, 0.05, 3);
            if (p.Template.Kind != ToolKind.Laser)
            {
                Assert.InRange(p.Template.SpindleRpm, 1000, 10000);
            }
        });

        var project = CamProject.CreateDefault();
        Assert.Equal(GcodeDialect.Grbl, project.Machine.Dialect);
        Assert.Equal(300, project.Machine.WorkAreaX);
        Assert.Equal(OriginAnchor.LowerLeft, project.Stock.Origin);
        Assert.Equal(3, project.Tools.Select(t => t.Number).Distinct().Count());
        Assert.NotEqual(project.Tools[0].Id, ToolPresets.Cnc3018[0].Create(1).Id);
    }

    [Fact]
    public void New_settings_survive_saving()
    {
        var project = CamProject.CreateDefault();
        project.Stock.ZeroAtBottom = true;
        project.Stock.Origin = OriginAnchor.Center;
        project.Operations.Add(new PocketOperation { Entry = EntryMode.Ramp, RampAngle = 5 });

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.True(copy.Stock.ZeroAtBottom);
        Assert.Equal(OriginAnchor.Center, copy.Stock.Origin);
        Assert.Equal(GcodeDialect.Grbl, copy.Machine.Dialect);
        Assert.Equal(1000, copy.Machine.SpindleMaxS);
        Assert.Equal(EntryMode.Ramp, copy.Operations[0].Entry);
        Assert.Equal(5, copy.Operations[0].RampAngle);
    }
}
