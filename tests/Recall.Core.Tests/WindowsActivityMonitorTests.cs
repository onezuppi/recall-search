using Recall.Core.Activity;

namespace Recall.Core.Tests;

public class WindowsActivityMonitorTests
{
    [Theory]
    [InlineData(3, true)]   // Direct3D game in full screen
    [InlineData(4, true)]   // presentation settings
    [InlineData(2, false)]  // "busy": also an ordinary maximized window, so it does not pause
    [InlineData(7, false)]  // "app": the search box and other shell windows
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
    [InlineData("ChatGPT")]
    [InlineData("browser")]
    [InlineData("explorer")]
    [InlineData("ShareX")]
    public void Busy_state_with_any_window_in_front_does_not_pause(string foreground)
    {
        Assert.Null(new WindowsActivityMonitor([], () => 2, foregroundProcess: () => foreground).BusyReason());
        Assert.Null(new WindowsActivityMonitor([], () => 7, foregroundProcess: () => foreground).BusyReason());
    }

    [Fact]
    public void A_game_or_presentation_pauses_and_the_log_names_the_active_window()
    {
        Assert.Equal("full screen game, active window: SomeGame", new WindowsActivityMonitor([], () => 3, foregroundProcess: () => "SomeGame").BusyReason());
        Assert.StartsWith("presentation mode", new WindowsActivityMonitor([], () => 4, foregroundProcess: () => "POWERPNT").BusyReason());
        Assert.Equal("full screen game", new WindowsActivityMonitor([], () => 3, foregroundProcess: () => null).BusyReason());
    }

    [Fact]
    public void Real_shell_query_does_not_throw() => _ = new WindowsActivityMonitor([]).BusyReason();
}
