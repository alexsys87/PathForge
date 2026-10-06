using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

public class ContinuousJogTests
{
    private const double Feed = 1200;
    private const double FeedZ = 300;

    [Fact]
    public void Holding_keeps_the_planner_a_short_time_ahead()
    {
        var jog = new ContinuousJog();
        var right = new JogVector(1, 0, 0);
        var sent = new List<JogSegment>();
        for (long now = 0; now <= 2000; now += ContinuousJog.TickMs)
        {
            var action = jog.Update(right, Feed, FeedZ, now, busy: false, machineJogging: now > 0);
            if (action.Kind == JogActionKind.Move)
            {
                sent.Add(action.Segment);
            }

            // Never more planned than the lead: the machine stops soon even if the cancel is lost.
            var planned = sent.Count * ContinuousJog.SegmentMs - now;
            Assert.InRange(planned, 0, ContinuousJog.LeadMs);
        }

        // 1200 mm/min for 150 ms = 3 mm per segment; about 2 s of motion in 2 s of holding.
        Assert.All(sent, s => Assert.Equal(new JogSegment(3, 0, 0, Feed), s));
        Assert.InRange(sent.Count * 3.0, 40, 47);

        Assert.Equal(JogActionKind.Cancel, jog.Update(default, Feed, FeedZ, 2050, false, true).Kind);
        Assert.Equal(JogActionKind.None, jog.Update(default, Feed, FeedZ, 2100, false, true).Kind);
    }

    [Fact]
    public void Joystick_deflection_sets_the_speed_and_z_has_its_own_feed()
    {
        var jog = new ContinuousJog();
        var action = jog.Update(new JogVector(0.5, 0, 1), Feed, FeedZ, 0, false, false);

        Assert.Equal(JogActionKind.Move, action.Kind);
        Assert.Equal(1.5, action.Segment.Dx, 6);
        Assert.Equal(0.75, action.Segment.Dz, 6);
        Assert.Equal(Math.Sqrt(600 * 600 + 300 * 300), action.Segment.Feed, 6);
    }

    [Fact]
    public void A_new_direction_stops_first_and_waits_for_the_machine()
    {
        var jog = new ContinuousJog();
        Assert.Equal(JogActionKind.Move, jog.Update(new JogVector(1, 0, 0), Feed, FeedZ, 0, false, false).Kind);

        // Still the same direction, only slower: no stop, the next segment is simply slower.
        var slower = jog.Update(new JogVector(0.5, 0, 0), Feed, FeedZ, 50, false, true);
        Assert.Equal(JogActionKind.Move, slower.Kind);
        Assert.Equal(Feed / 2, slower.Segment.Feed, 6);

        var up = new JogVector(0, 1, 0);
        Assert.Equal(JogActionKind.Cancel, jog.Update(up, Feed, FeedZ, 100, false, true).Kind);
        // Decelerating after the cancel: nothing new yet.
        Assert.Equal(JogActionKind.None, jog.Update(up, Feed, FeedZ, 150, false, true).Kind);
        var next = jog.Update(up, Feed, FeedZ, 200, false, false);
        Assert.Equal(JogActionKind.Move, next.Kind);
        Assert.Equal(3, next.Segment.Dy, 6);
    }

    [Fact]
    public void Waits_for_the_answer_and_halts_on_rejection_until_released()
    {
        var jog = new ContinuousJog();
        var left = new JogVector(-1, 0, 0);
        Assert.Equal(JogActionKind.Move, jog.Update(left, Feed, FeedZ, 0, false, false).Kind);
        Assert.Equal(JogActionKind.None, jog.Update(left, Feed, FeedZ, 200, busy: true, machineJogging: true).Kind);

        jog.Halt();
        Assert.False(jog.IsMoving);
        Assert.Equal(JogActionKind.None, jog.Update(left, Feed, FeedZ, 250, false, false).Kind);
        Assert.Equal(JogActionKind.None, jog.Update(default, Feed, FeedZ, 300, false, false).Kind);
        Assert.Equal(JogActionKind.Move, jog.Update(left, Feed, FeedZ, 350, false, false).Kind);
    }
}
