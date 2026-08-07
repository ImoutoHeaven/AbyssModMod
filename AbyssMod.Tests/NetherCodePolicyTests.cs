using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodePolicyTests
{
    [Fact]
    public void Exact_30024_beats_all_other_candidates()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(),
            Candidate(40000, NetherCodeEffectKind.Rush, coverage: 10),
            Candidate(30024, NetherCodeEffectKind.Safe, level: 1)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(30024, decision.SelectedCodeId);
    }

    [Fact]
    public void Effective_safe_is_max_zero_safe_minus_risk()
    {
        NetherCodeEffectiveLevels levels = NetherCodePolicy.CalculateEffectiveLevels(
            [Code(1, NetherCodeEffectKind.Safe, 3), Code(2, NetherCodeEffectKind.Risk, 5)]
        );

        Assert.Equal(0, levels.Safe);
    }

    [Fact]
    public void Effective_rush_and_impact_cancel_each_other()
    {
        NetherCodeEffectiveLevels levels = NetherCodePolicy.CalculateEffectiveLevels(
            [Code(1, NetherCodeEffectKind.Rush, 5), Code(2, NetherCodeEffectKind.Impact, 2)]
        );

        Assert.Equal(3, levels.Rush);
        Assert.Equal(0, levels.Impact);
    }

    [Fact]
    public void Risk_40024_is_never_selected()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(),
            Candidate(40024, NetherCodeEffectKind.Risk, level: 9),
            Candidate(30001, NetherCodeEffectKind.Safe, level: 1)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(30001, decision.SelectedCodeId);
    }

    [Fact]
    public void Existing_risk_is_first_capacity_replacement()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(capacity: 2, current: [Code(40024, NetherCodeEffectKind.Risk, 1), Code(30100, NetherCodeEffectKind.Rush, 1)]),
            Candidate(30024, NetherCodeEffectKind.Safe)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(40024, decision.RemoveCodeId);
    }

    [Fact]
    public void Safe_five_is_protected_from_replacement()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(capacity: 2, current: [Code(30024, NetherCodeEffectKind.Safe, 5), Code(30100, NetherCodeEffectKind.Impact, 1)]),
            Candidate(30200, NetherCodeEffectKind.Rush, coverage: 4)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(30100, decision.RemoveCodeId);
        Assert.Contains(30024, decision.ProtectedCodeIds);
    }

    [Fact]
    public void Auto_lane_locks_to_party_coverage_and_does_not_oscillate()
    {
        NetherCodeDecision first = Decide(
            Portfolio(),
            Candidate(31001, NetherCodeEffectKind.Rush, coverage: 5),
            Candidate(32001, NetherCodeEffectKind.Impact, coverage: 1)
        );
        NetherCodeDecision second = Decide(
            Portfolio(lockedLane: NetherCombatLane.Rush, current: [Code(31000, NetherCodeEffectKind.Rush, 1, coverage: 1)]),
            Candidate(31002, NetherCodeEffectKind.Rush, coverage: 1),
            Candidate(32002, NetherCodeEffectKind.Impact, coverage: 99)
        );

        Assert.Equal(NetherCombatLane.Rush, first.LockedLane);
        Assert.Equal(31001, first.SelectedCodeId);
        Assert.Equal(NetherCombatLane.Rush, second.LockedLane);
        Assert.Equal(31002, second.SelectedCodeId);
    }

    [Fact]
    public void Reload_is_used_only_when_remaining_is_greater_than_reserve_one()
    {
        NetherCodeDecision decision = Decide(Portfolio(reloadCount: 2), Candidate(40024, NetherCodeEffectKind.Risk));

        Assert.Equal(NetherCodeDecisionKind.Reload, decision.Kind);
    }

    [Fact]
    public void Last_reload_is_preserved_and_best_safe_candidate_is_selected()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(reloadCount: 1),
            Candidate(31001, NetherCodeEffectKind.Rush, coverage: 1),
            Candidate(30024, NetherCodeEffectKind.Safe)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(30024, decision.SelectedCodeId);
    }

    [Fact]
    public void Missing_master_or_over_capacity_snapshot_pauses()
    {
        NetherCodeDecision missingMaster = Decide(
            Portfolio(masterComplete: false),
            Candidate(30024, NetherCodeEffectKind.Safe)
        );
        NetherCodeDecision overCapacity = Decide(
            Portfolio(capacity: 1, current: [Code(1, NetherCodeEffectKind.Rush), Code(2, NetherCodeEffectKind.Impact)]),
            Candidate(30024, NetherCodeEffectKind.Safe)
        );

        Assert.Equal(NetherCodeDecisionKind.Pause, missingMaster.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, missingMaster.PauseReason);
        Assert.Equal(NetherCodeDecisionKind.Pause, overCapacity.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, overCapacity.PauseReason);
    }

    [Fact]
    public void Overflowed_code_levels_pause_instead_of_selecting()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(current: [Code(1, NetherCodeEffectKind.Safe, int.MaxValue), Code(2, NetherCodeEffectKind.Safe, 1)]),
            Candidate(30024, NetherCodeEffectKind.Safe)
        );

        Assert.Equal(NetherCodeDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownEffect, decision.PauseReason);
    }

    [Fact]
    public void Category_confirmed_ordinary_offer_with_unresolved_lane_facts_pauses_without_select_or_reload()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(reloadCount: 3),
            Candidate(51001, NetherCodeEffectKind.General, rarity: 4, coverage: 0)
        );

        Assert.Equal(NetherCodeDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, decision.PauseReason);
    }

    [Fact]
    public void Proven_safe_offer_can_be_selected_without_using_an_unresolved_ordinary_offer()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(),
            Candidate(51001, NetherCodeEffectKind.General, rarity: 4),
            Candidate(30024, NetherCodeEffectKind.Safe, level: 1)
        );

        Assert.Equal(NetherCodeDecisionKind.Select, decision.Kind);
        Assert.Equal(30024, decision.SelectedCodeId);
    }

    [Fact]
    public void Full_portfolio_does_not_rank_or_remove_an_ordinary_code_with_unresolved_facts()
    {
        NetherCodeDecision decision = Decide(
            Portfolio(
                capacity: 1,
                current: [Code(51001, NetherCodeEffectKind.General, rarity: 3)]
            ),
            Candidate(51002, NetherCodeEffectKind.Safe, level: 1)
        );

        Assert.Equal(NetherCodeDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, decision.PauseReason);
    }

    private static NetherCodeDecision Decide(NetherCodePortfolio portfolio, params NetherCodeCandidate[] candidates) => new NetherCodePolicy().Decide(
        portfolio,
        candidates,
        new NetherAutoClimbSettings { CombatLane = NetherCombatLane.Auto, CodeReloadReserve = 1 }
    );

    private static NetherCodePortfolio Portfolio(
        int capacity = 5,
        int reloadCount = 1,
        bool masterComplete = true,
        NetherCombatLane? lockedLane = null,
        IReadOnlyList<NetherCodeState>? current = null
    ) => new()
    {
        Capacity = capacity,
        ReloadCount = reloadCount,
        IsMasterComplete = masterComplete,
        LockedLane = lockedLane,
        CurrentCodes = current ?? [],
    };

    private static NetherCodeState Code(long id, NetherCodeEffectKind kind, int level = 1, int rarity = 0, int coverage = 0) => new(id, kind, level)
    {
        Rarity = rarity,
        PartyCoverageKnown = true,
        PartyCoverage = coverage,
        IsResearchOnlyKnown = true,
    };

    private static NetherCodeCandidate Candidate(long id, NetherCodeEffectKind kind, int level = 1, int rarity = 0, int coverage = 0) => new(id, kind, level)
    {
        Rarity = rarity,
        PartyCoverageKnown = true,
        PartyCoverage = coverage,
        IsResearchOnlyKnown = true,
    };
}
