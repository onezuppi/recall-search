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

    [Theory]
    [InlineData("browser", null)]
    [InlineData("Chrome", null)]
    [InlineData("msedge.exe", null)]
    [InlineData("explorer", null)]
    [InlineData("SearchHost", null)]
    [InlineData("SomeGame", "full screen app, active window: SomeGame")]
    public void Busy_state_with_a_browser_in_front_is_not_a_full_screen_app(string foreground, string? expected)
    {
        var monitor = new WindowsActivityMonitor([], () => 2, foregroundProcess: () => foreground, foregroundCoversMonitor: () => true);
        Assert.Equal(expected, monitor.BusyReason());
    }

    [Theory]
    [InlineData(false, null)]                                              // an ordinary maximized window (ChatGPT, an IDE, ...)
    [InlineData(true, "full screen app, active window: SomeApp")]          // covers the whole monitor
    [InlineData(null, "full screen app, active window: SomeApp")]          // cannot be told: keep the shell's word
    public void Busy_state_counts_only_when_the_window_covers_the_whole_monitor(bool? covers, string? expected)
    {
        var monitor = new WindowsActivityMonitor([], () => 2, foregroundProcess: () => "SomeApp", foregroundCoversMonitor: () => covers);
        Assert.Equal(expected, monitor.BusyReason());
    }

    [Fact]
    public void A_game_or_presentation_is_not_hidden_by_the_window_size_check()
    {
        Assert.StartsWith("full screen game", new WindowsActivityMonitor([], () => 3, foregroundProcess: () => "SomeApp", foregroundCoversMonitor: () => false).BusyReason());
        Assert.StartsWith("presentation mode", new WindowsActivityMonitor([], () => 4, foregroundProcess: () => "SomeApp", foregroundCoversMonitor: () => false).BusyReason());
    }

    [Fact]
    public void Store_app_state_with_the_search_box_in_front_is_not_busy()
    {
        Assert.Null(new WindowsActivityMonitor([], () => 7, foregroundProcess: () => "SearchHost").BusyReason());
        Assert.StartsWith("full screen store app", new WindowsActivityMonitor([], () => 7, foregroundProcess: () => "SomeGame").BusyReason());
    }

    [Fact]
    public void A_browser_in_front_does_not_hide_a_real_game_or_presentation()
    {
        Assert.StartsWith("full screen game", new WindowsActivityMonitor([], () => 3, foregroundProcess: () => "browser").BusyReason());
        Assert.StartsWith("presentation mode", new WindowsActivityMonitor([], () => 4, foregroundProcess: () => "chrome").BusyReason());
    }

    [Fact]
    public void Real_shell_query_does_not_throw() => _ = new WindowsActivityMonitor([]).BusyReason();
}
