using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeCategorySemanticsTests
{
    [Fact]
    public void RO_categories_map_all_four_values_without_guessing_a_rush_or_impact_lane()
    {
        NetherCodeMasterSemantic technique = NetherCodeCategorySemantics.Resolve(51001, rawCategory: 1, effectType: 1);
        NetherCodeMasterSemantic strength = NetherCodeCategorySemantics.Resolve(51002, rawCategory: 2, effectType: 2);
        NetherCodeMasterSemantic resistance = NetherCodeCategorySemantics.Resolve(51003, rawCategory: 3, effectType: 1);
        NetherCodeMasterSemantic enhancement = NetherCodeCategorySemantics.Resolve(51004, rawCategory: 4, effectType: 2);

        Assert.True(technique.IsKnown);
        Assert.Equal(NetherCodeCategory.Technique, technique.Category);
        Assert.Equal(NetherCodeCategoryGroup.Tactics, technique.Group);
        Assert.Equal(NetherCodeCategory.Strength, technique.PairedCategory);
        Assert.Equal(NetherCodeEffectKind.General, technique.EffectKind);
        Assert.Equal(NetherCodeEffectKind.General, strength.EffectKind);
        Assert.Equal(NetherCodeEffectKind.Safe, resistance.EffectKind);
        Assert.Equal(NetherCodeEffectKind.Risk, enhancement.EffectKind);
    }

    [Fact]
    public void RO_category_pairs_are_exclusive_only_inside_their_confirmed_group()
    {
        Assert.Equal(
            NetherCodeCategory.Strength,
            NetherCodeCategorySemantics.GetPairedCategory(NetherCodeCategory.Technique)
        );
        Assert.Equal(
            NetherCodeCategory.ErosionEnhancement,
            NetherCodeCategorySemantics.GetPairedCategory(NetherCodeCategory.ErosionResistance)
        );
        Assert.True(NetherCodeCategorySemantics.IsExclusive(
            NetherCodeCategory.Technique,
            NetherCodeCategory.Strength
        ));
        Assert.True(NetherCodeCategorySemantics.IsExclusive(
            NetherCodeCategory.ErosionResistance,
            NetherCodeCategory.ErosionEnhancement
        ));
        Assert.False(NetherCodeCategorySemantics.IsExclusive(
            NetherCodeCategory.Technique,
            NetherCodeCategory.ErosionResistance
        ));
        Assert.False(NetherCodeCategorySemantics.IsExclusive(
            NetherCodeCategory.Technique,
            NetherCodeCategory.Technique
        ));
    }

    [Fact]
    public void Exact_30024_and_40024_override_an_invalid_category_but_other_invalid_rows_fail_closed()
    {
        NetherCodeMasterSemantic safeOverride = NetherCodeCategorySemantics.Resolve(30024, rawCategory: 99, effectType: 99);
        NetherCodeMasterSemantic riskOverride = NetherCodeCategorySemantics.Resolve(40024, rawCategory: 0, effectType: 99);
        NetherCodeMasterSemantic invalid = NetherCodeCategorySemantics.Resolve(51005, rawCategory: 99, effectType: 1);

        Assert.True(safeOverride.IsKnown);
        Assert.Equal(NetherCodeEffectKind.Safe, safeOverride.EffectKind);
        Assert.True(riskOverride.IsKnown);
        Assert.Equal(NetherCodeEffectKind.Risk, riskOverride.EffectKind);
        Assert.False(invalid.IsKnown);
    }

    [Fact]
    public void Ordinary_category_candidates_rank_deterministically_without_fake_lane_semantics()
    {
        NetherCodeDecision decision = new NetherCodePolicy().Decide(
            new NetherCodePortfolio { Capacity = 3, ReloadCount = 1, IsMasterComplete = true },
            new[]
            {
                Candidate(51002, NetherCodeCategory.Technique, rarity: 3, level: 2),
                Candidate(51001, NetherCodeCategory.Technique, rarity: 3, level: 2),
            },
            new NetherAutoClimbSettings { CombatLane = NetherCombatLane.Auto, CodeReloadReserve = 1 }
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(51001, decision.SelectedCodeId);
        Assert.Equal(NetherCombatLane.Auto, decision.LockedLane);
    }

    [Fact]
    public void Ordinary_offer_replaces_the_single_confirmed_paired_category_conflict()
    {
        NetherCodeDecision decision = new NetherCodePolicy().Decide(
            new NetherCodePortfolio
            {
                Capacity = 1,
                ReloadCount = 1,
                IsMasterComplete = true,
                CurrentCodes = new[] { State(51010, NetherCodeCategory.Strength) },
            },
            new[] { Candidate(51011, NetherCodeCategory.Technique, rarity: 4) },
            new NetherAutoClimbSettings { CombatLane = NetherCombatLane.Auto, CodeReloadReserve = 1 }
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(51011, decision.SelectedCodeId);
        Assert.Equal(51010, decision.RemoveCodeId);
    }

    [Fact]
    public void Category_safe_risk_pair_cancels_before_capacity_replacement_and_reload_reserve_is_preserved()
    {
        NetherCodeDecision replacement = new NetherCodePolicy().Decide(
            new NetherCodePortfolio
            {
                Capacity = 2,
                ReloadCount = 1,
                IsMasterComplete = true,
                CurrentCodes = new[]
                {
                    State(40024, NetherCodeCategory.ErosionEnhancement, level: 3),
                    State(51020, NetherCodeCategory.Technique),
                },
            },
            new[] { Candidate(30024, NetherCodeCategory.ErosionResistance, level: 3) },
            new NetherAutoClimbSettings { CombatLane = NetherCombatLane.Auto, CodeReloadReserve = 1 }
        );
        NetherCodeDecision reserve = new NetherCodePolicy().Decide(
            new NetherCodePortfolio { Capacity = 2, ReloadCount = 1, IsMasterComplete = true },
            new[] { Candidate(40024, NetherCodeCategory.ErosionEnhancement, level: 9) },
            new NetherAutoClimbSettings { CombatLane = NetherCombatLane.Auto, CodeReloadReserve = 1 }
        );

        Assert.Equal(NetherCodeDecisionKind.Select, replacement.Kind);
        Assert.Equal(30024, replacement.SelectedCodeId);
        Assert.Equal(40024, replacement.RemoveCodeId);
        Assert.Equal(NetherCodeDecisionKind.Keep, reserve.Kind);
    }

    private static NetherCodeCandidate Candidate(long id, NetherCodeCategory category, int rarity = 0, int level = 1) =>
        new(id, NetherCodeCategorySemantics.Resolve(id, (int)category, effectType: 1).EffectKind, level)
        {
            IsKnown = true,
            Category = category,
            Rarity = rarity,
        };

    private static NetherCodeState State(long id, NetherCodeCategory category, int level = 1) =>
        new(id, NetherCodeCategorySemantics.Resolve(id, (int)category, effectType: 1).EffectKind, level)
        {
            IsKnown = true,
            Category = category,
            PartyCoverageKnown = true,
            IsResearchOnlyKnown = true,
        };
}
