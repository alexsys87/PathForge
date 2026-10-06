using System.Globalization;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Stand-in for a GRBL board: keeps received lines in a 128-byte serial buffer until told to process them.</summary>
internal sealed class FakeGrbl : IGrblTransport
{
    private readonly Queue<string> _buffer = new();

    public event Action<string>? LineReceived;

    public event Action<Exception>? Failed;

    public List<string> Lines { get; } = new();

    public List<byte> Realtime { get; } = new();

    public int BufferBytes { get; private set; }

    public int MaxBufferBytes { get; private set; }

    /// <summary>Answer for a line; "ok" when null.</summary>
    public Func<string, string?>? Responder { get; set; }

    public int Pending => _buffer.Count;

    public void Write(string text)
    {
        Assert.EndsWith("\n", text);
        var line = text[..^1];
        Assert.DoesNotContain('\n', line);
        Lines.Add(line);
        _buffer.Enqueue(line);
        BufferBytes += text.Length;
        MaxBufferBytes = Math.Max(MaxBufferBytes, BufferBytes);
        Assert.True(BufferBytes <= 127, $"serial buffer overflow: {BufferBytes} bytes");
    }

    public void WriteRealtime(byte command) => Realtime.Add(command);

    public void ProcessOne()
    {
        var line = _buffer.Dequeue();
        BufferBytes -= line.Length + 1;
        // A response may hold several lines (e.g. a probe report followed by "ok").
        foreach (var response in (Responder?.Invoke(line) ?? "ok").Split('\n'))
        {
            Send(response);
        }
    }

    public void ProcessAll()
    {
        while (_buffer.Count > 0)
        {
            ProcessOne();
        }
    }

    public void Send(string line) => LineReceived?.Invoke(line);

    public void Status(string state, double x = 0, double y = 0, double z = 0) =>
        Send(FormattableString.Invariant($"<{state}|MPos:{x:0.000},{y:0.000},{z:0.000}|FS:0,0|WCO:0.000,0.000,0.000>"));

    public void Fail() => Failed?.Invoke(new IOException("port closed"));

    public void Dispose()
    {
    }
}

public class GrblTests
{
    private static (GrblController Controller, FakeGrbl Board, List<GrblJobResult> Results) Connect()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        var results = new List<GrblJobResult>();
        controller.JobFinished += results.Add;
        board.Send("Grbl 1.1f ['$' for help]");
        board.Status("Idle");
        return (controller, board, results);
    }

    private static List<GrblLine> Program(int count)
    {
        var text = string.Join("\n", Enumerable.Range(0, count).Select(i =>
            FormattableString.Invariant($"G1 X{i * 0.123:0.000} Y{i * 0.456:0.000} Z-{i % 7 * 0.1:0.0} F{400 + i % 3}")));
        return GrblProgram.Prepare(text).Lines;
    }

    [Fact]
    public void Status_reports_give_work_and_machine_positions()
    {
        Assert.True(GrblStatus.TryParse("<Run|MPos:10.000,20.000,-1.000|FS:400,10000|WCO:2.000,3.000,-5.000|Ov:120,100,90|Bf:12,100|Pn:PZ>",
            GrblStatus.Unknown, out var status));

        Assert.Equal(GrblState.Run, status.State);
        Assert.Equal(new Vec3(10, 20, -1), status.MachinePosition);
        Assert.Equal(new Vec3(8, 17, 4), status.WorkPosition);
        Assert.Equal(400, status.Feed);
        Assert.Equal(10000, status.Spindle);
        Assert.Equal(120, status.FeedOverride);
        Assert.Equal(90, status.SpindleOverride);
        Assert.Equal(12, status.PlannerFree);
        Assert.Equal("PZ", status.Pins);

        // WCO and overrides are not in every report: they are kept.
        Assert.True(GrblStatus.TryParse("<Hold:0|MPos:1.000,1.000,1.000|FS:0,0>", status, out var next));
        Assert.True(next.IsHoldComplete);
        Assert.Equal(new Vec3(-1, -2, 6), next.WorkPosition);
        Assert.Equal(120, next.FeedOverride);

        // Controllers set to report work positions.
        Assert.True(GrblStatus.TryParse("<Idle|WPos:1.000,2.000,3.000|F:0>", status, out var work));
        Assert.Equal(new Vec3(3, 5, -2), work.MachinePosition);
        Assert.False(GrblStatus.TryParse("ok", status, out _));
    }

    [Fact]
    public void Program_is_cleaned_and_stops_are_found()
    {
        var prepared = GrblProgram.Prepare("%\n(header)\ng0 x1 y2 ; move\n/G1 X3 F100\nM5\nM0 (Insert T2 V-bit)\nM3 S1000 M00\nG4 P1.5\n" +
                                           new string('X', 90) + "\n$H\n%");

        var lines = prepared.Lines;
        Assert.Equal(new[] { "G0X1Y2", "G1X3F100", "M5", "", "M3S1000", "G4P1.5", new string('X', 90), "$H" }, lines.Select(l => l.Text));
        Assert.Equal("move", lines[0].Comment);
        Assert.True(lines[3].StopAfter);
        Assert.Equal("Insert T2 V-bit", lines[3].Comment);
        Assert.True(lines[4].StopAfter);
        Assert.False(lines[5].StopAfter);
        Assert.Equal(3, lines[0].SourceLine);
        Assert.Equal(2, prepared.Problems.Count);
        Assert.Equal(7, prepared.CommandCount);
    }

    [Fact]
    public void Streaming_keeps_grbls_buffer_full_but_never_overflows_it()
    {
        var (controller, board, results) = Connect();
        var program = Program(500);

        controller.StartJob(program);
        Assert.Equal(GrblJobState.Running, controller.Job);
        Assert.True(board.MaxBufferBytes > 100, "the buffer should be used, not one line at a time");

        while (board.Pending > 0)
        {
            board.ProcessOne();
        }

        Assert.Equal(program.Select(l => l.Text), board.Lines);
        Assert.Equal(500, controller.AcknowledgedCommands);
        Assert.Equal(0, controller.BufferUsed);

        // Finished only when the machine has stopped moving.
        board.Status("Run");
        Assert.Equal(GrblJobState.Running, controller.Job);
        board.Status("Idle");
        board.Status("Idle");
        Assert.Equal(GrblJobState.None, controller.Job);
        var result = Assert.Single(results);
        Assert.True(result.Success);
    }

    [Fact]
    public void Tool_change_stops_the_stream_until_the_operator_resumes()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Contours.Add(new Contour(1, new Segment[] { new ArcSegment(new Vec2(20, 20), 5, 0, 2 * Math.PI) }));
        var first = new Tool { Number = 1, Name = "Mill" };
        var second = new Tool { Number = 2, Name = "Engraver", FeedRate = 250 };
        project.Tools.AddRange(new[] { first, second });
        project.Operations.Add(new ProfileOperation { ToolId = first.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { ToolId = second.Id, Depth = 0.5, Side = ProfileSide.OnLine, ContourIds = { 1 } });
        var gcode = GcodeWriter.Write("job", ToolpathGenerator.Generate(project), project.Machine);
        var program = GrblProgram.Prepare(gcode).Lines;
        var stopIndex = program.FindIndex(l => l.StopAfter);
        Assert.True(stopIndex > 0);
        var (controller, board, results) = Connect();

        controller.StartJob(program);
        board.ProcessAll();
        board.Status("Run");
        board.Status("Idle");
        board.Status("Idle");

        Assert.Equal(GrblJobState.ProgramStop, controller.Job);
        Assert.Contains("T2", controller.JobMessage);
        Assert.DoesNotContain(board.Lines, l => l == "M0");
        Assert.Equal(program.Take(stopIndex + 1).Count(l => l.Text.Length > 0), board.Lines.Count);

        // The operator jogs, probes the new tool and resumes.
        controller.Jog(0, 0, 5, 300);
        board.ProcessAll();
        controller.ProbeZ(plateThickness: 1.6, maxTravel: 20, feed: 50, retract: 3);
        while (board.Pending > 0)
        {
            board.ProcessAll();
        }

        Assert.Contains("$J=G91G21Z5F300", board.Lines);
        Assert.Contains("G91G38.2Z-20F50", board.Lines);
        Assert.Contains("G10L20P1Z1.6", board.Lines);
        Assert.Contains("G0Z4.6", board.Lines);

        var before = board.Lines.Count;
        controller.Resume();
        Assert.Equal(GrblJobState.Running, controller.Job);
        // Feed and distance mode are restored before the program continues.
        Assert.StartsWith("G90G21G94F", board.Lines[before]);
        board.ProcessAll();
        board.Status("Idle");
        board.Status("Idle");
        Assert.True(Assert.Single(results).Success);
    }

    [Fact]
    public void Program_after_a_tool_change_goes_up_before_moving_sideways()
    {
        var project = new CamProject();
        MachineProfiles.Cnc3018Stock.ApplyTo(project.Machine);
        project.Contours.Add(new Contour(1, new Segment[] { new ArcSegment(new Vec2(20, 20), 5, 0, 2 * Math.PI) }));
        var first = new Tool { Number = 1 };
        var second = new Tool { Number = 2 };
        project.Tools.AddRange(new[] { first, second });
        project.Operations.Add(new ProfileOperation { ToolId = first.Id, Depth = 1, ContourIds = { 1 } });
        project.Operations.Add(new ProfileOperation { ToolId = second.Id, Depth = 1, ContourIds = { 1 } });

        var lines = GcodeWriter.Write("job", ToolpathGenerator.Generate(project), project.Machine).Split('\n');

        var stop = Array.FindIndex(lines, l => l.StartsWith("M0", StringComparison.Ordinal));
        var firstMove = lines.Skip(stop + 1).First(l => l.StartsWith("G0", StringComparison.Ordinal) || l.StartsWith("G1", StringComparison.Ordinal));
        Assert.Equal("G0 Z" + project.Machine.SafeZ.ToString("0.###", CultureInfo.InvariantCulture), firstMove.Trim());
    }

    [Fact]
    public void Rejected_line_holds_the_machine_until_the_operator_decides()
    {
        var (controller, board, _) = Connect();
        var program = Program(200);
        var bad = program[10].Text;
        board.Responder = line => line == bad ? "error:20" : null;

        controller.StartJob(program);
        while (board.Pending > 0 && controller.Job == GrblJobState.Running)
        {
            board.ProcessOne();
        }

        Assert.Equal(GrblJobState.Error, controller.Job);
        Assert.Contains((byte)'!', board.Realtime);
        Assert.Contains("неподдерживаемая", controller.JobMessage);
        var sent = board.Lines.Count;
        board.ProcessAll();
        Assert.Equal(sent, board.Lines.Count);

        controller.Resume();
        Assert.Equal((byte)'~', board.Realtime[^1]);
        board.Responder = null;
        board.ProcessAll();
        Assert.Equal(200, board.Lines.Count);
    }

    [Fact]
    public void Stop_holds_first_and_resets_when_the_machine_stands_still()
    {
        var (controller, board, results) = Connect();
        controller.StartJob(Program(300));
        board.ProcessOne();
        board.Status("Run", 5, 5, -1);

        controller.Stop();

        Assert.Equal(GrblJobState.Stopping, controller.Job);
        Assert.Equal((byte)'!', board.Realtime[^1]);
        Assert.DoesNotContain((byte)0x18, board.Realtime);
        board.Status("Hold:1");
        Assert.DoesNotContain((byte)0x18, board.Realtime);
        board.Status("Hold:0");
        Assert.Equal((byte)0x18, board.Realtime[^1]);
        Assert.Equal(GrblJobState.None, controller.Job);
        Assert.False(Assert.Single(results).Success);
        Assert.Equal(0, controller.BufferUsed);

        // GRBL restarts after the reset; nothing else is sent.
        var sent = board.Lines.Count;
        board.Send("Grbl 1.1f ['$' for help]");
        board.Status("Idle");
        Assert.Equal(sent, board.Lines.Count);
    }

    [Fact]
    public void Alarm_aborts_the_program()
    {
        var (controller, board, results) = Connect();
        controller.StartJob(Program(300));
        board.ProcessOne();

        board.Send("ALARM:2");

        Assert.Equal(GrblJobState.None, controller.Job);
        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("Авария 2", result.Message);
        Assert.Equal(0, controller.BufferUsed);
    }

    [Fact]
    public void Commands_are_sent_one_by_one_and_a_failed_step_cancels_the_rest()
    {
        var (controller, board, _) = Connect();
        board.Responder = line => line.StartsWith("G91G38.2", StringComparison.Ordinal) ? "error:33" : null;
        var log = new List<GrblLogEntry>();
        controller.Log += log.Add;

        controller.ProbeZ(0, 10, 50, 2);

        Assert.Single(board.Lines);
        board.ProcessAll();
        Assert.Single(board.Lines);
        Assert.Contains(log, e => e.Kind == GrblLogKind.Error && e.Text.Contains("Ошибка 33", StringComparison.Ordinal));

        controller.SetWorkZero(true, true, false);
        board.ProcessAll();
        Assert.Equal("G10L20P1X0Y0", board.Lines[^1]);
    }

    [Fact]
    public void Commands_and_jogging_are_refused_while_a_program_runs()
    {
        var (controller, board, _) = Connect();
        controller.StartJob(Program(50));

        Assert.Throws<InvalidOperationException>(() => controller.Jog(1, 0, 0, 100));
        Assert.Throws<InvalidOperationException>(() => controller.SendCommand("$X"));
        Assert.Throws<InvalidOperationException>(() => controller.StartJob(Program(5)));

        // Real-time status requests are always possible.
        controller.SendCommand("?");
        Assert.Equal((byte)'?', board.Realtime[^1]);
    }

    [Fact]
    public void Program_needs_an_idle_machine()
    {
        var (controller, board, _) = Connect();
        board.Status("Alarm");

        var error = Assert.Throws<InvalidOperationException>(() => controller.StartJob(Program(5)));

        Assert.Contains("Авария", error.Message);
        Assert.Empty(board.Lines);
    }

    [Fact]
    public void Lost_connection_aborts_the_job()
    {
        var (controller, board, results) = Connect();
        controller.StartJob(Program(100));

        board.Fail();

        Assert.False(controller.IsConnected);
        Assert.Equal(GrblJobState.None, controller.Job);
        Assert.False(Assert.Single(results).Success);
        Assert.Throws<InvalidOperationException>(() => controller.SendCommand("$X"));
    }

    [Fact]
    public void Silent_controller_is_treated_as_lost_once_it_has_answered()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        var later = Environment.TickCount64 + 60_000;

        // Nothing heard yet (wrong port, board still booting): no verdict.
        controller.CheckResponse(TimeSpan.FromSeconds(10), later);
        Assert.True(controller.IsConnected);
        Assert.False(controller.HasAnswered);

        board.Status("Idle");
        Assert.True(controller.HasAnswered);
        controller.CheckResponse(TimeSpan.FromSeconds(10), Environment.TickCount64 + 1_000);
        Assert.True(controller.IsConnected);

        // Homing: GRBL does not answer status queries until the cycle is done.
        controller.Home();
        controller.CheckResponse(TimeSpan.FromSeconds(10), later);
        Assert.True(controller.IsConnected);

        board.ProcessAll();
        controller.CheckResponse(TimeSpan.FromSeconds(10), later);
        Assert.False(controller.IsConnected);
    }

    [Fact]
    public void Jog_cancel_drops_queued_jogs()
    {
        var (controller, board, _) = Connect();
        controller.Jog(10, 0, 0, 1000);
        controller.Jog(10, 0, 0, 1000);
        controller.Jog(0, -5, 0, 1000);

        controller.JogCancel();
        board.ProcessAll();

        Assert.Single(board.Lines);
        Assert.Equal(0x85, board.Realtime[^1]);
    }

    [Fact]
    public void Error_and_alarm_numbers_are_explained()
    {
        Assert.Equal("Ошибка 22: не задана подача F", GrblMessages.Describe("error:22"));
        Assert.StartsWith("Авария 5: щуп", GrblMessages.Describe("ALARM:5"));
        Assert.Equal("Ошибка 99", GrblMessages.Describe("error:99"));
        Assert.Equal("[MSG:Caution: Unlocked]", GrblMessages.Describe("[MSG:Caution: Unlocked]"));
    }
}
