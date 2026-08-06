using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class ConfigAutoReloadStateTests
{
    [Fact]
    public void Requires_same_changed_stamp_twice_before_reload()
    {
        var state = new ConfigAutoReloadState();
        var initial = new ConfigFileStamp(100, 10);
        var changed = new ConfigFileStamp(200, 20);

        Assert.Equal(ConfigReloadDecision.NoChange, state.Observe(true, initial));
        Assert.Equal(
            ConfigReloadDecision.AwaitingStableChange,
            state.Observe(true, changed)
        );
        Assert.Equal(ConfigReloadDecision.Reload, state.Observe(true, changed));
    }

    [Fact]
    public void Restarts_stability_check_when_file_changes_again()
    {
        var state = new ConfigAutoReloadState();
        var initial = new ConfigFileStamp(100, 10);
        var firstWrite = new ConfigFileStamp(200, 15);
        var completedWrite = new ConfigFileStamp(300, 20);

        state.Observe(true, initial);
        Assert.Equal(
            ConfigReloadDecision.AwaitingStableChange,
            state.Observe(true, firstWrite)
        );
        Assert.Equal(
            ConfigReloadDecision.AwaitingStableChange,
            state.Observe(true, completedWrite)
        );
        Assert.Equal(ConfigReloadDecision.Reload, state.Observe(true, completedWrite));
    }

    [Fact]
    public void Missing_file_cancels_pending_reload_until_replacement_is_stable()
    {
        var state = new ConfigAutoReloadState();
        var initial = new ConfigFileStamp(100, 10);
        var replacement = new ConfigFileStamp(200, 20);

        state.Observe(true, initial);
        state.Observe(true, replacement);
        Assert.Equal(ConfigReloadDecision.NoChange, state.Observe(false, default));
        Assert.Equal(
            ConfigReloadDecision.AwaitingStableChange,
            state.Observe(true, replacement)
        );
        Assert.Equal(ConfigReloadDecision.Reload, state.Observe(true, replacement));
    }

    [Fact]
    public void Acknowledged_stamp_does_not_reload_again()
    {
        var state = new ConfigAutoReloadState();
        var initial = new ConfigFileStamp(100, 10);
        var changed = new ConfigFileStamp(200, 20);

        state.Observe(true, initial);
        state.Observe(true, changed);
        Assert.Equal(ConfigReloadDecision.Reload, state.Observe(true, changed));

        state.Acknowledge(changed);

        Assert.Equal(ConfigReloadDecision.NoChange, state.Observe(true, changed));
    }
}
