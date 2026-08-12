using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NovelLive2DScaleStateTests
{
    [Fact]
    public void Wheel_adjustment_uses_hundredth_steps_and_delays_persistence()
    {
        var state = new NovelLive2DScaleState(saveDelaySeconds: 1f);

        Assert.True(state.TryAdjust(1f, now: 10f, configuredScale: 1f, out var scale));
        Assert.Equal(1.01f, scale);
        Assert.False(state.ShouldSave(now: 10.99f));
        Assert.True(state.ShouldSave(now: 11f));
    }

    [Theory]
    [InlineData(0.1f, -1f, 0.1f)]
    [InlineData(10f, 1f, 10f)]
    [InlineData(1.234f, 1f, 1.24f)]
    public void Adjustment_rounds_and_clamps_to_supported_range(
        float configured,
        float wheelDelta,
        float expected
    )
    {
        var state = new NovelLive2DScaleState(saveDelaySeconds: 1f);

        state.TryAdjust(wheelDelta, now: 0f, configured, out var scale);

        Assert.Equal(expected, scale);
    }

    [Fact]
    public void Config_reload_cancels_unsaved_wheel_value_and_reports_visual_change()
    {
        var state = new NovelLive2DScaleState(saveDelaySeconds: 1f);
        state.TryAdjust(5f, now: 2f, configuredScale: 1f, out _);

        Assert.True(state.Reload(1.5f, out var reloaded));
        Assert.Equal(1.5f, reloaded);
        Assert.False(state.ShouldSave(now: 100f));
    }
}
