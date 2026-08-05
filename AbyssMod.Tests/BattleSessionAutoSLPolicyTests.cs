using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class BattleSessionAutoSLPolicyTests
{
    [Fact]
    public void Retries_without_a_fixed_limit_when_no_rare_drop_exists()
    {
        var report = new BattleDropProbeReport([], 0);

        Assert.True(BattleSessionAutoSLPolicy.ShouldRetry(report));
        Assert.True(BattleSessionAutoSLPolicy.ShouldRetry(report));
    }

    [Fact]
    public void Stops_immediately_when_a_rare_drop_exists()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 2, 3, 1, 5, true)],
            1
        );

        Assert.False(BattleSessionAutoSLPolicy.ShouldRetry(report));
    }

    [Fact]
    public void Stops_when_the_drop_payload_cannot_be_parsed()
    {
        var report = new BattleDropProbeReport([], 0, "missing");

        Assert.False(BattleSessionAutoSLPolicy.ShouldRetry(report));
    }

    [Fact]
    public void Clamps_cooldown_to_zero_or_above()
    {
        Assert.Equal(0f, BattleSessionAutoSLPolicy.ClampCooldown(-1f));
        Assert.Equal(4f, BattleSessionAutoSLPolicy.ClampCooldown(4f));
    }

}
