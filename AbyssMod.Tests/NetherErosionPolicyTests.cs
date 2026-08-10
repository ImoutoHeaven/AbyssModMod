using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherErosionPolicyTests
{
    [Theory]
    [InlineData(40, 5, 45)]
    [InlineData(40, 0, 40)]
    [InlineData(40, 10, 50)]
    public void Battle_projection_uses_effective_dynamic_delta(int current, int delta, int expected)
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectBattle(
            current,
            delta,
            [],
            softLimit: 90,
            isMandatoryBoss: false
        );

        Assert.True(projection.IsAllowed);
        Assert.Equal(expected, projection.ProjectedErosion);
    }

    [Fact]
    public void Optional_action_reaching_soft_limit_90_is_rejected()
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectBattle(85, 5, [], 90, false);

        Assert.False(projection.IsAllowed);
        Assert.Equal(NetherPauseReason.UnsafeErosion, projection.PauseReason);
        Assert.Equal(90, projection.ProjectedErosion);
    }

    [Fact]
    public void Mandatory_boss_below_hard_limit_is_allowed_at_soft_limit()
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectBattle(86, 5, [], 90, true);

        Assert.True(projection.IsAllowed);
        Assert.Equal(91, projection.ProjectedErosion);
    }

    [Fact]
    public void Any_projection_reaching_100_is_rejected()
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectBattle(95, 5, [], 90, true);

        Assert.False(projection.IsAllowed);
        Assert.Equal(NetherPauseReason.UnsafeErosion, projection.PauseReason);
        Assert.Equal(100, projection.ProjectedErosion);
    }

    [Fact]
    public void Three_event_effects_are_aggregated_before_limit_check()
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectEffects(
            80,
            [
                new NetherEffect(NetherEffectKind.Erosion, 5),
                new NetherEffect(NetherEffectKind.ErosionHeal, 2),
                new NetherEffect(NetherEffectKind.Erosion, 6),
            ],
            softLimit: 90,
            isMandatoryBoss: false
        );

        Assert.True(projection.IsAllowed);
        Assert.Equal(89, projection.ProjectedErosion);
    }

    [Fact]
    public void Unknown_rate_or_addition_effect_pauses()
    {
        NetherErosionProjection projection = new NetherErosionPolicy().ProjectBattle(
            40,
            5,
            [new NetherErosionModifier(NetherErosionOperation.Rate, 100, isIncrease: true, known: false, orderKnown: false)],
            90,
            false
        );

        Assert.False(projection.IsAllowed);
        Assert.Equal(NetherPauseReason.UnknownEffect, projection.PauseReason);
    }

    [Fact]
    public void Unchanged_code_fingerprint_with_wrong_observed_delta_reports_drift()
    {
        NetherErosionObservation observation = new NetherErosionPolicy().CompareObserved(50, 48, "code-a", "code-a");

        Assert.True(observation.IsDrift);
        Assert.Equal(NetherPauseReason.ErosionDrift, observation.PauseReason);
    }

    [Fact]
    public void Changed_code_fingerprint_requires_rebaseline_instead_of_drift_claim()
    {
        NetherErosionObservation observation = new NetherErosionPolicy().CompareObserved(50, 48, "code-a", "code-b");

        Assert.False(observation.IsDrift);
        Assert.True(observation.RequiresRebaseline);
        Assert.Equal(NetherPauseReason.None, observation.PauseReason);
    }
}
