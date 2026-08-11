using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class BattleSessionAutoSLPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Idle_exploration_encounters_are_excluded_from_auto_sl(
        bool isIdleExplorationEncounter,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            BattleSessionAutoSLRoutingPolicy.ShouldInterceptExploration(
                isIdleExplorationEncounter
            )
        );
    }

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

    [Fact]
    public void Any_content_type_preserves_legacy_unfiltered_behavior()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 30, 50001, 1, 4, true)],
            1
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Red,
            BattleSessionNormalContentTypeFilter.Any
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal(1, Assert.Single(evaluation.Targets).Sid);
    }

    [Fact]
    public void Content_type_filter_accepts_any_selected_equipment_combination()
    {
        var report = new BattleDropProbeReport(
            [
                new BattleDropItem(1, 70, 1001, 1, 4, true),
                new BattleDropItem(2, 80, 1002, 1, 4, true),
                new BattleDropItem(3, 90, 1003, 1, 4, true),
                new BattleDropItem(4, 30, 1004, 1, 4, true),
            ],
            4
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Red,
            BattleSessionNormalContentTypeFilter.Weapon
                | BattleSessionNormalContentTypeFilter.Accessory
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal([1L, 3L], evaluation.Targets.Select(item => item.Sid));
    }

    [Fact]
    public void Selecting_all_equipment_types_still_excludes_non_equipment_content()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 30, 50001, 1, 4, true)],
            1
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Red,
            BattleSessionNormalContentTypeFilter.Weapon
                | BattleSessionNormalContentTypeFilter.Armor
                | BattleSessionNormalContentTypeFilter.Accessory
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Invalid_content_type_filter_fails_open_instead_of_retrying_forever()
    {
        var report = new BattleDropProbeReport([], 0);

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Red,
            (BattleSessionNormalContentTypeFilter)8
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("unsupported-normal-content-types:8", evaluation.Error);
    }

    [Fact]
    public void Content_type_filter_description_exposes_game_content_type_values()
    {
        Assert.Equal(
            "rarity>=Red(4), contentTypes=Weapon(70)|Accessory(90)",
            BattleSessionAutoSLPolicy.DescribeNormalStopCondition(
                BattleSessionAutoSLStopMode.Rarity,
                BattleSessionDropRarity.Red,
                BattleSessionNormalContentTypeFilter.Weapon
                    | BattleSessionNormalContentTypeFilter.Accessory
            )
        );
    }

    [Fact]
    public void Flags_value_parses_from_the_cfg_comma_syntax()
    {
        BattleSessionNormalContentTypeFilter parsed =
            Enum.Parse<BattleSessionNormalContentTypeFilter>("Weapon, Accessory");

        Assert.Equal(
            BattleSessionNormalContentTypeFilter.Weapon
                | BattleSessionNormalContentTypeFilter.Accessory,
            parsed
        );
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

    [Fact]
    public void Empty_exact_config_preserves_the_legacy_normal_policy()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 80, 23010440, 1, 0, false)],
            0
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.EvaluateNormal(
            report,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold,
            BattleSessionNormalContentTypeFilter.Any,
            "  "
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Equal(
            "isRare, contentTypes=Any",
            BattleSessionAutoSLPolicy.DescribeNormalStopCondition(
                BattleSessionAutoSLStopMode.IsRare,
                BattleSessionDropRarity.Gold,
                BattleSessionNormalContentTypeFilter.Any,
                ""
            )
        );
    }

    [Fact]
    public void Exact_mode_matches_content_type_and_content_id_not_sid()
    {
        var report = new BattleDropProbeReport(
            [
                new BattleDropItem(23010440, 80, 999, 1, 5, true),
                new BattleDropItem(2, 80, 23010440, 1, 0, false),
            ],
            1
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.EvaluateNormal(
            report,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.UniqueWeapon,
            BattleSessionNormalContentTypeFilter.Weapon,
            "Armor:23010440"
        );

        BattleDropItem matched = Assert.Single(evaluation.Targets);
        Assert.Equal(2, matched.Sid);
        Assert.False(evaluation.ShouldRetry);
    }

    [Fact]
    public void Exact_mode_does_not_stop_for_a_different_drop_that_matches_legacy_filters()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 80, 999, 1, 5, true)],
            1
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.EvaluateNormal(
            report,
            BattleSessionAutoSLStopMode.IsRareOrRarity,
            BattleSessionDropRarity.Red,
            BattleSessionNormalContentTypeFilter.Armor,
            "Armor:23010440"
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Multiple_exact_targets_are_or_alternatives()
    {
        var report = new BattleDropProbeReport(
            [new BattleDropItem(9, 90, 456, 1, 0, false)],
            0
        );

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.EvaluateNormal(
            report,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold,
            BattleSessionNormalContentTypeFilter.Any,
            "Weapon:123, Armor:23010440, Accessory:456"
        );

        Assert.Equal(9, Assert.Single(evaluation.Targets).Sid);
        Assert.Equal(
            "exactTargets=Weapon:123,Armor:23010440,Accessory:456",
            BattleSessionAutoSLPolicy.DescribeNormalStopCondition(
                BattleSessionAutoSLStopMode.IsRare,
                BattleSessionDropRarity.Gold,
                BattleSessionNormalContentTypeFilter.Any,
                "Weapon:123, Armor:23010440, Accessory:456"
            )
        );
    }

    [Fact]
    public void Invalid_exact_config_fails_open_instead_of_retrying_forever()
    {
        var report = new BattleDropProbeReport([], 0);

        BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.EvaluateNormal(
            report,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold,
            BattleSessionNormalContentTypeFilter.Any,
            "Armor:not-a-number"
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
        Assert.StartsWith("invalid-normal-exact-target:", evaluation.Error);
        Assert.StartsWith(
            "exactTargets=invalid:",
            BattleSessionAutoSLPolicy.DescribeNormalStopCondition(
                BattleSessionAutoSLStopMode.IsRare,
                BattleSessionDropRarity.Gold,
                BattleSessionNormalContentTypeFilter.Any,
                "Armor:not-a-number"
            )
        );
    }

}
