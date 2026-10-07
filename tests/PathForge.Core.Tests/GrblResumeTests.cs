using PathForge.Core.Geometry;
using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>Continuing a program from a line.</summary>
public class GrblResumeTests
{
    private const string Program = """
        G90 G21 G17
        G54
        M3 S8000
        G4 P3
        G0 Z5
        G0 X10 Y10
        G1 Z-1 F100
        G1 X20 F400
        G1 Y20
        G1 X10
        G0 Z5
        M5
        M0 (Insert T2 Drill 0.8)
        M3 S10000
        G0 X30 Y30
        G1 Z-2 F80
        G0 Z5
        M30
        """;

    private static readonly GrblResumeOptions Options = new(SafeZ: 3, SpindleDelaySeconds: 3);

    private static List<GrblLine> Lines() => GrblProgram.Prepare(Program).Lines;

    [Fact]
    public void Resume_in_the_middle_of_a_cut_goes_up_over_and_down_with_the_spindle_on()
    {
        var result = GrblResume.Build(Lines(), fromLine: 9, Options);

        Assert.Equal(9, result.StartLine);
        Assert.Equal(new Vec3(20, 10, -1), result.Position);
        Assert.Equal("", result.Tool);
        Assert.Empty(result.Warnings);
        var texts = result.Lines.Select(l => l.Text).ToList();
        Assert.Equal(new[] { "G21G90G94G17G54", "G0Z5", "G0X20Y10", "M3S8000", "G4P3", "G0Z0", "G1Z-1F100", "F400", "G1Y20" }, texts.Take(9));
        Assert.Equal("M30", texts[^1]);
        // An error in an approach line holds the job like a program line.
        Assert.All(result.Lines.Take(8), l => Assert.Equal(9, l.SourceLine));
    }

    [Fact]
    public void Resume_after_a_tool_change_names_the_tool_and_uses_the_new_speed()
    {
        var result = GrblResume.Build(Lines(), fromLine: 16, Options);

        Assert.Equal("Insert T2 Drill 0.8", result.Tool);
        Assert.Equal(new Vec3(30, 30, 5), result.Position);
        var texts = result.Lines.Select(l => l.Text).ToList();
        Assert.Contains("M3S10000", texts);
        Assert.DoesNotContain(texts, t => t == "");
        // Above the stock already: no fast descent, the line itself plunges with its own feed.
        Assert.Equal(new[] { "G1Z5F100", "F400", "G1Z-2F80" }, texts.Skip(texts.IndexOf("G1Z5F100")).Take(3));
    }

    [Fact]
    public void Line_without_a_motion_word_gets_the_modal_one()
    {
        var lines = GrblProgram.Prepare("G21 G90\nM3 S1000\nG0 Z5\nG0 X0 Y0\nG1 Z-1 F100\nG1 X5 F300\nY5\nX0\n").Lines;

        var result = GrblResume.Build(lines, fromLine: 7, Options);

        Assert.Contains("G1Y5", result.Lines.Select(l => l.Text));
        Assert.Equal(new Vec3(5, 0, -1), result.Position);
    }

    [Fact]
    public void Inch_and_relative_programs_are_resumed_in_millimetres_and_their_modes_restored()
    {
        var lines = GrblProgram.Prepare("G20 G90\nM3 S1000\nG0 Z0.2\nG0 X1 Y1\nG1 Z-0.04 F4\nG91\nG1 X1\nG1 X1\nG1 Y1\n").Lines;

        var result = GrblResume.Build(lines, fromLine: 8, Options);

        Assert.Equal(new Vec3(50.8, 25.4, -1.016), result.Position);
        var texts = result.Lines.Select(l => l.Text).ToList();
        Assert.Contains("G0X50.8Y25.4", texts);
        Assert.Contains("G1Z-1.016F101.6", texts);
        Assert.Equal("G20G91", texts[texts.IndexOf("G1X1") - 1]);
        // The program's own highest Z (0.2 in = 5.08 mm) is above the configured safe height.
        Assert.Contains("G0Z5.08", texts);
    }

    [Fact]
    public void Laser_program_has_no_z_moves_and_no_spindle_pause()
    {
        var lines = GrblProgram.Prepare("G21 G90\nM4 S0\nG0 X0 Y0\nG1 X10 S600 F1500\nG1 Y10\nG0 X20\nG1 X30 S400\n").Lines;

        var result = GrblResume.Build(lines, fromLine: 5, Options);

        var texts = result.Lines.Select(l => l.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains('Z'));
        Assert.DoesNotContain(texts, t => t.StartsWith("G4"));
        Assert.Contains("M4S600", texts);
        Assert.Equal(new[] { "G21G90G94G17G54", "G0X10Y0", "M4S600", "F1500", "G1Y10" }, texts.Take(5));
    }

    [Fact]
    public void Coordinate_changes_in_the_program_are_reported()
    {
        var lines = GrblProgram.Prepare("G21 G90\nG92 X0 Y0\nG0 X1 Y1\nG1 X2 F100\n").Lines;

        var result = GrblResume.Build(lines, fromLine: 4, Options);

        Assert.Contains(result.Warnings, w => w.Contains("G92"));
    }

    [Fact]
    public void Line_past_the_end_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => GrblResume.Build(Lines(), fromLine: 100, Options));
    }

    [Fact]
    public void Suggested_line_is_some_lines_before_the_last_accepted_one()
    {
        var lines = GrblProgram.Prepare(string.Join("\n", Enumerable.Range(1, 100).Select(i => $"G1 X{i} F100"))).Lines;

        Assert.Equal(60, GrblResume.SuggestLine(lines, 80));
        Assert.Equal(1, GrblResume.SuggestLine(lines, 5));
        Assert.Equal(1, GrblResume.SuggestLine(lines, 0));
    }

    [Fact]
    public void Controller_remembers_the_last_accepted_line_after_a_stop()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        board.Send("Grbl 1.1f ['$' for help]");
        board.Status("Idle");
        var program = GrblProgram.Prepare(string.Join("\n", Enumerable.Range(1, 30).Select(i => $"G1 X{i} F100"))).Lines;

        controller.StartJob(program);
        for (var i = 0; i < 12; i++)
        {
            board.ProcessOne();
        }

        board.Status("Idle");
        controller.Stop();

        Assert.Equal(12, controller.LastAcknowledgedLine);
    }
}
