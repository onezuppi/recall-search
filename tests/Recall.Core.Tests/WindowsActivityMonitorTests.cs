using Recall.Core.Activity;

namespace Recall.Core.Tests;

public class WindowsActivityMonitorTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    [InlineData(1, false)]  // not present (locked)
    [InlineData(5, false)]  // accepts notifications
    [InlineData(6, false)]  // quiet time
    [InlineData(null, false)]
    public void Shell_states_are_classified(int? state, bool busy) =>
        Assert.Equal(busy, WindowsActivityMonitor.Classify(state) != null);

    [Fact]
    public void Configured_process_marks_busy_regardless_of_extension_and_case()
    {
        var monitor = new WindowsActivityMonitor(["Game.EXE"], () => 5, () => ["explorer", "game"]);
        Assert.Equal("process game", monitor.BusyReason());
    }

    [Fact]
    public void Unlisted_processes_and_calm_shell_mean_idle()
    {
        var monitor = new WindowsActivityMonitor(["game.exe"], () => 5, () => ["explorer", "chrome"]);
        Assert.Null(monitor.BusyReason());
    }

    [Fact]
    public void Process_list_is_not_enumerated_when_no_names_are_configured()
    {
        var monitor = new WindowsActivityMonitor([], () => 5, () => throw new InvalidOperationException("must not run"));
        Assert.Null(monitor.BusyReason());
    }

    [Fact]
    public void Real_shell_query_does_not_throw() => _ = new WindowsActivityMonitor([]).BusyReason();
}
