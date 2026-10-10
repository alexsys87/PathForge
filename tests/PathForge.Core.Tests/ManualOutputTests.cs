using PathForge.Core.Grbl;
using PathForge.Core.Machining;
using Xunit;

namespace PathForge.Core.Tests;

public class ManualOutputTests
{
    private static MachineSettings Machine(double maxS = 1000, double maxRpm = 24000) =>
        new() { SpindleMaxS = maxS, SpindleMaxRpm = maxRpm };

    [Fact]
    public void A_spindle_is_switched_off_with_M5()
    {
        Assert.Equal("M5", ManualOutput.Off(laser: false));
    }

    [Fact]
    public void A_laser_is_switched_off_with_M5_and_the_modal_motion_goes_back_to_G0()
    {
        Assert.Equal("G0 M5", ManualOutput.Off(laser: true));
    }

    [Fact]
    public void Laser_is_constant_power_M3_in_G1_scaled_to_the_controller_range()
    {
        // 5 % of S1000 is S50. In GRBL laser mode a plain M3 under the default G0 switches on with zero power:
        // G1 (no axes, so nothing moves; it needs a feed rate) is what lets the beam light up where it stands.
        Assert.Equal("G1 F100 M3 S50", ManualOutput.On(true, 5, Machine()));
    }

    [Fact]
    public void The_laser_command_has_a_feed_rate_or_GRBL_answers_error_22()
    {
        Assert.Contains(" F100 ", ManualOutput.On(true, 5, Machine()));
    }

    [Fact]
    public void Laser_power_uses_S1000_when_no_range_is_configured()
    {
        Assert.Equal("G1 F100 M3 S50", ManualOutput.On(true, 5, Machine(maxS: 0)));
    }

    [Fact]
    public void Laser_power_is_rounded_and_is_never_S0()
    {
        Assert.Equal("G1 F100 M3 S13", ManualOutput.On(true, 5, Machine(maxS: 255)));
        Assert.Equal("G1 F100 M3 S1", ManualOutput.On(true, 0.01, Machine()));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(100.5)]
    [InlineData(double.NaN)]
    public void Laser_power_outside_0_to_100_is_refused(double power)
    {
        Assert.Throws<InvalidOperationException>(() => ManualOutput.On(true, power, Machine()));
    }

    [Fact]
    public void A_spindle_command_does_not_touch_the_modal_motion()
    {
        Assert.DoesNotContain("G", ManualOutput.On(false, 10000, Machine()));
    }

    [Fact]
    public void Spindle_speed_is_written_as_the_S_word_of_the_machine()
    {
        // 10 000 rpm of 24 000 at S1000 is S417, the same mapping as the program's M3.
        Assert.Equal("M3 S417", ManualOutput.On(false, 10000, Machine()));
    }

    [Fact]
    public void Spindle_speed_is_written_as_rpm_when_no_range_is_configured()
    {
        Assert.Equal("M3 S10000", ManualOutput.On(false, 10000, Machine(maxS: 0)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Spindle_speed_must_be_positive(double rpm)
    {
        Assert.Throws<InvalidOperationException>(() => ManualOutput.On(false, rpm, Machine()));
    }

    private static GrblStatus Report(string report, GrblStatus? previous = null)
    {
        Assert.True(GrblStatus.TryParse(report, previous ?? GrblStatus.Unknown, out var status), report);
        return status;
    }

    // GRBL sends the accessory state only together with the overrides (every 10th report idle, every 20th busy).
    private const string OutputOn = "<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100|A:S>";
    private const string OutputOff = "<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100>";
    private const string Plain = "<Idle|MPos:0.000,0.000,0.000|FS:0,0>";

    [Theory]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100|A:S>", true)]
    [InlineData("<Run|MPos:1.000,2.000,3.000|FS:500,10000|Ov:100,100,100|A:C>", true)]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100|A:SFM>", true)]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100|A:F>", false)]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0|Ov:100,100,100>", false)]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0>", false)]
    public void Status_shows_the_spindle_or_laser_output_only_while_it_is_enabled(string report, bool expected)
    {
        Assert.Equal(expected, Report(report).SpindleOn);
    }

    [Fact]
    public void A_report_with_overrides_but_without_the_accessory_state_means_off()
    {
        var on = Report(OutputOn);
        Assert.True(on.SpindleOn);
        Assert.False(Report(OutputOff, on).SpindleOn);
    }

    [Fact]
    public void A_report_without_overrides_keeps_the_previous_output_state()
    {
        // Between two override reports GRBL says nothing about the output: it must not flicker to "off".
        var on = Report(OutputOn);
        var next = on;
        for (var i = 0; i < 9; i++)
        {
            next = Report(Plain, next);
            Assert.True(next.SpindleOn);
        }

        Assert.False(Report(OutputOff, next).SpindleOn);
    }

    [Fact]
    public void Only_reports_with_the_accessory_state_are_counted()
    {
        var status = Report(Plain);
        status = Report(Plain, status);
        Assert.Equal(0, status.AccessoryReports);
        status = Report(OutputOff, status);
        status = Report(Plain, status);
        status = Report(OutputOn, status);
        Assert.Equal(2, status.AccessoryReports);
    }

    [Fact]
    public void The_controller_forgets_the_output_when_GRBL_restarts()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        board.Send("Grbl 1.1f ['$' for help]");
        board.Send(OutputOn);
        Assert.True(controller.Status.SpindleOn);

        board.Send("Grbl 1.1f ['$' for help]");

        Assert.False(controller.Status.SpindleOn);
    }

    [Fact]
    public void The_controller_forgets_the_output_after_a_soft_reset()
    {
        var board = new FakeGrbl();
        var controller = new GrblController(board);
        board.Send("Grbl 1.1f ['$' for help]");
        board.Send(OutputOn);
        Assert.True(controller.Status.SpindleOn);

        controller.SoftReset();

        Assert.False(controller.Status.SpindleOn);
    }

    [Fact]
    public void Without_a_command_the_tracker_shows_what_GRBL_reports()
    {
        var tracker = new ManualOutputTracker();
        Assert.True(tracker.Resolve(Report(OutputOn), 0));
        Assert.False(tracker.Resolve(Report(OutputOff), 0));
    }

    [Fact]
    public void A_commanded_state_is_shown_at_once_and_confirmed_by_the_next_report()
    {
        var tracker = new ManualOutputTracker();
        var before = Report(OutputOff);
        tracker.Commanded(true, before, 0);

        // The old state is still all GRBL has told: the panel shows the command.
        Assert.True(tracker.Resolve(Report(Plain, before), 0));

        // GRBL confirms: from now on the panel follows GRBL again, so a later switch-off is seen too.
        var confirmed = Report(OutputOn, before);
        Assert.True(tracker.Resolve(confirmed, 0));
        Assert.False(tracker.Resolve(Report(OutputOff, confirmed), 0));
    }

    [Fact]
    public void The_first_contradicting_report_may_predate_the_command_the_second_does_not()
    {
        var tracker = new ManualOutputTracker();
        var before = Report(OutputOff);
        tracker.Commanded(true, before, 0);

        var first = Report(OutputOff, before);
        Assert.True(tracker.Resolve(first, 0));
        var second = Report(OutputOff, first);
        Assert.False(tracker.Resolve(second, 0));
    }

    [Fact]
    public void A_rejected_command_drops_the_commanded_state()
    {
        var tracker = new ManualOutputTracker();
        var before = Report(OutputOff);
        tracker.Commanded(true, before, failedCommands: 3);

        Assert.False(tracker.Resolve(before, failedCommands: 4));
    }

    [Fact]
    public void A_firmware_that_never_reports_the_accessory_state_keeps_the_commanded_state()
    {
        var tracker = new ManualOutputTracker();
        var status = Report(Plain);
        tracker.Commanded(true, status, 0);

        for (var i = 0; i < 50; i++)
        {
            status = Report(Plain, status);
            Assert.True(tracker.Resolve(status, 0));
        }
    }

    [Fact]
    public void Reset_drops_the_commanded_state()
    {
        var tracker = new ManualOutputTracker();
        var status = Report(OutputOff);
        tracker.Commanded(true, status, 0);
        tracker.Reset();

        Assert.False(tracker.Resolve(status, 0));
    }

    [Fact]
    public void A_new_controller_that_counts_again_drops_the_commanded_state()
    {
        var tracker = new ManualOutputTracker();
        var old = Report(OutputOff);
        old = Report(OutputOff, old);
        old = Report(OutputOff, old);
        tracker.Commanded(true, old, 0);

        // After a reconnect the report counter starts again from zero.
        Assert.False(tracker.Resolve(Report(Plain), 0));
    }
}
