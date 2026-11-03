using Recall.Agent;
using Recall.Core.Config;
using Recall.Core.Ipc;
using Recall.Core.Scan;

namespace Recall.Core.Tests;

public class SlowScanScheduleTests
{
    private static readonly TimeOnly Three = new(3, 0);

    [Theory]
    [InlineData("03:00", true)]
    [InlineData("3:05", true)]
    [InlineData(" 23:59 ", true)]
    [InlineData("", false)]
    [InlineData("24:00", false)]
    [InlineData("3pm", false)]
    public void Parse_accepts_a_24_hour_time_only(string text, bool valid) =>
        Assert.Equal(valid, SlowScanSchedule.TryParse(text, out _));

    [Fact]
    public void Scan_is_due_once_the_planned_time_has_passed_since_the_last_scan()
    {
        var now = new DateTime(2026, 11, 5, 3, 1, 0);
        Assert.True(SlowScanSchedule.IsDue(Three, new DateTime(2026, 11, 4, 3, 0, 5), now));
        Assert.False(SlowScanSchedule.IsDue(Three, new DateTime(2026, 11, 5, 3, 0, 5), now));
    }

    [Fact]
    public void Scan_is_not_due_before_the_planned_time_when_yesterdays_ran()
    {
        var now = new DateTime(2026, 11, 5, 2, 0, 0);
        Assert.False(SlowScanSchedule.IsDue(Three, new DateTime(2026, 11, 4, 3, 10, 0), now));
    }

    [Fact]
    public void A_computer_that_was_off_at_the_time_scans_once_when_it_is_back()
    {
        var now = new DateTime(2026, 11, 5, 14, 0, 0);
        Assert.True(SlowScanSchedule.IsDue(Three, new DateTime(2026, 11, 3, 20, 0, 0), now));
    }

    [Fact]
    public void Controller_saves_a_normalized_time_and_accepts_empty_to_turn_it_off()
    {
        var file = Path.Combine(Path.GetTempPath(), "rs-slow-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var controller = new SettingsController(new RecallConfig([], new HashSet<string>(), []), file, null, new NoEffects());
            var set = controller.Handle(new IpcRequest(IpcOps.SetSlowScan, Value: "4:30"));
            Assert.True(set.Ok);
            Assert.Equal("04:30", set.Settings!.SlowScanTime);
            Assert.Equal("04:30", RecallConfig.Load(file).SlowScanTime);

            Assert.False(controller.Handle(new IpcRequest(IpcOps.SetSlowScan, Value: "soon")).Ok);
            Assert.Equal("", controller.Handle(new IpcRequest(IpcOps.SetSlowScan, Value: "")).Settings!.SlowScanTime);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public void Default_configuration_scans_at_three_in_the_morning() =>
        Assert.Equal("03:00", new RecallConfig([], new HashSet<string>(), []).SlowScanTime);

    private sealed class NoEffects : ISettingsEffects
    {
        public void RootAdded(RootFolder root) { }
        public void RootRemoved(RootFolder root) { }
        public void RootWatchChanged(RootFolder root) { }
        public void ExcludesChanged(IReadOnlyList<string> patterns) { }
        public void FileTypesChanged(IReadOnlyDictionary<string, string> types) { }
        public void SimilarityChanged(float minSimilarity) { }
    }
}
