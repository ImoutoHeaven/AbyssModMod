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
    public void Rarity_mode_accepts_gold_even_when_is_rare_is_false()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 31, 210021, 1, 3, false)],
            0
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void IsRare_mode_does_not_treat_gold_rarity_as_the_rare_flag()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 31, 210021, 1, 3, false)],
            0
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Theory]
    [InlineData(BattleSessionAutoSLStopMode.IsRareOrRarity, true)]
    [InlineData(BattleSessionAutoSLStopMode.IsRareAndRarity, false)]
    public void Combined_modes_apply_or_and_semantics(
        BattleSessionAutoSLStopMode stopMode,
        bool shouldStop
    )
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 2, 3, 1, 1, true)],
            1
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            stopMode,
            BattleSessionDropRarity.Gold
        );

        Assert.Equal(shouldStop, !evaluation.ShouldRetry);
    }

    [Fact]
    public void Stop_condition_description_contains_the_cfg_rarity_name_and_value()
    {
        Assert.Equal(
            "isRare-or-rarity>=Red(4)",
            BattleSessionAutoSLPolicy.DescribeStopCondition(
                BattleSessionAutoSLStopMode.IsRareOrRarity,
                BattleSessionDropRarity.Red
            )
        );
    }

    [Fact]
    public void Invalid_cfg_enum_values_fail_open_instead_of_retrying_forever()
    {
        var report = new BattleDropProbeReport([], 0);

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            (BattleSessionDropRarity)99
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("unsupported-minimum-rarity:99", evaluation.Error);
    }

    [Fact]
    public void Clamps_cooldown_to_zero_or_above()
    {
        Assert.Equal(0f, BattleSessionAutoSLPolicy.ClampCooldown(-1f));
        Assert.Equal(4f, BattleSessionAutoSLPolicy.ClampCooldown(4f));
    }

}
