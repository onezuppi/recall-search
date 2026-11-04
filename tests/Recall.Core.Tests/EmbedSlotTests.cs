using Recall.Agent;

namespace Recall.Core.Tests;

public class EmbedSlotTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_run_after_start_never_waits()
    {
        Assert.Equal(TimeSpan.Zero, EmbedSlot.Remaining(null, intervalMinutes: 20, Now));
    }

    [Fact]
    public void The_next_slot_opens_one_interval_after_the_last_run()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), EmbedSlot.Remaining(Now.AddMinutes(-5), 20, Now));
        Assert.Equal(TimeSpan.Zero, EmbedSlot.Remaining(Now.AddMinutes(-25), 20, Now));
    }

    [Fact]
    public void A_shorter_interval_takes_effect_on_the_next_look()
    {
        // The loop re-reads the interval every few seconds, so a user who lowers 1440 to 0 does not wait for the old day.
        var last = Now.AddMinutes(-5);
        Assert.Equal(TimeSpan.FromMinutes(1435), EmbedSlot.Remaining(last, 1440, Now));
        Assert.Equal(TimeSpan.Zero, EmbedSlot.Remaining(last, 0, Now));
    }

    [Fact]
    public void One_look_never_sleeps_longer_than_the_slice()
    {
        Assert.Equal(EmbedSlot.Slice, EmbedSlot.NextSleep(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromSeconds(2), EmbedSlot.NextSleep(TimeSpan.FromSeconds(2)));
    }
}
