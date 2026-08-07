using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherActionReconcilePolicyTests
{
    [Fact]
    public void Select_floor_requires_an_authoritative_floor_or_status_postcondition()
    {
        NetherSnapshot before = Snapshot(floorId: 10, floorLevel: 10);
        NetherSnapshot after = Snapshot(floorId: 11, floorLevel: 11, status: NetherSessionStatus.Battle);

        Assert.Equal(
            NetherActionOutcome.Applied,
            NetherActionReconcilePolicy.Evaluate(
                new NetherPlannedAction(NetherActionKind.SelectFloor)
                {
                    FloorId = 11,
                    ExpectedBeforeStatus = NetherSessionStatus.Play,
                    ExpectedAfterStatus = NetherSessionStatus.Battle,
                },
                before,
                after
            )
        );
    }

    [Fact]
    public void Wrong_floor_or_status_is_never_treated_as_the_selected_floor()
    {
        NetherSnapshot before = Snapshot(floorId: 10, floorLevel: 10);
        NetherSnapshot wrongFloor = Snapshot(floorId: 12, floorLevel: 11, status: NetherSessionStatus.Battle);
        NetherSnapshot wrongStatus = Snapshot(floorId: 11, floorLevel: 11, status: NetherSessionStatus.Wait);
        NetherPlannedAction action = new(NetherActionKind.SelectFloor)
        {
            FloorId = 11,
            ExpectedBeforeStatus = NetherSessionStatus.Play,
            ExpectedAfterStatus = NetherSessionStatus.Battle,
        };

        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongFloor));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongStatus));
    }

    [Fact]
    public void Exact_code_add_and_replace_is_applied_but_a_wrong_code_is_not()
    {
        NetherSnapshot before = Snapshot(codes: new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) });
        NetherSnapshot exact = Snapshot(codes: new[] { new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1) }, codeHash: "40024:1:1");
        NetherSnapshot wrong = Snapshot(codes: new[] { new NetherCodeState(50024, NetherCodeEffectKind.Rush, 1) }, codeHash: "50024:1:1");
        NetherPlannedAction action = new(NetherActionKind.SelectCode) { CodeId = 40024, ReplaceCodeId = 30024 };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrong));
    }

    [Fact]
    public void Exact_shop_content_and_cost_is_applied_but_a_wrong_content_is_not()
    {
        NetherSnapshot before = Snapshot(items: Array.Empty<NetherRewardItem>(), gold: 100);
        NetherSnapshot exact = Snapshot(items: new[] { new NetherRewardItem(42, 1) }, gold: 80);
        NetherSnapshot wrong = Snapshot(items: new[] { new NetherRewardItem(99, 1) }, gold: 80);
        NetherPlannedAction action = new(NetherActionKind.BuyShopItem) { ContentId = 42, GoldCost = 20, ContentAmount = 1 };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrong));
    }

    [Fact]
    public void Exact_continue_ticket_map_and_segment_is_applied_but_wrong_ticket_or_map_is_not()
    {
        NetherSnapshot before = Snapshot(ticketCount: 3, mapId: 2, floorLevel: 10);
        NetherSnapshot exact = Snapshot(ticketCount: 2, mapId: 3, floorLevel: 11);
        NetherSnapshot wrongTicket = Snapshot(ticketCount: 1, mapId: 3, floorLevel: 11);
        NetherSnapshot wrongMap = Snapshot(ticketCount: 2, mapId: 4, floorLevel: 11);
        NetherPlannedAction action = new(NetherActionKind.Continue)
        {
            TicketCost = 1,
            ExpectedMapId = 3,
            ExpectedSegmentFloorLevel = 11,
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongTicket));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongMap));
    }

    [Fact]
    public void Only_an_unchanged_exact_target_is_a_genuine_not_applied_outcome()
    {
        NetherSnapshot before = Snapshot(items: Array.Empty<NetherRewardItem>(), gold: 100);
        NetherSnapshot unchanged = Snapshot(items: Array.Empty<NetherRewardItem>(), gold: 100);
        NetherSnapshot unrelatedChange = Snapshot(items: Array.Empty<NetherRewardItem>(), gold: 90);
        NetherPlannedAction action = new(NetherActionKind.BuyShopItem) { ContentId = 42, GoldCost = 20, ContentAmount = 1 };

        Assert.Equal(NetherActionOutcome.NotApplied, NetherActionReconcilePolicy.Evaluate(action, before, unchanged));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, unrelatedChange));
    }

    [Fact]
    public void Reload_code_requires_code_or_reload_resource_change_not_an_unrelated_map_change()
    {
        NetherSnapshot before = Snapshot(codeReload: 2, mapHash: "map-a");
        NetherSnapshot unrelated = Snapshot(codeReload: 2, mapHash: "map-b");
        NetherSnapshot applied = Snapshot(codeReload: 1, mapHash: "map-a");

        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.ReloadCode), before, unrelated)
        );
        Assert.Equal(
            NetherActionOutcome.Applied,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.ReloadCode), before, applied)
        );
    }

    [Fact]
    public void Unknown_outcome_with_no_action_specific_postcondition_stays_ambiguous_and_is_never_replayed()
    {
        NetherSnapshot snapshot = Snapshot();

        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.BuyShopItem) { ContentId = 7 }, snapshot, snapshot)
        );
    }

    private static NetherSnapshot Snapshot(
        long floorId = 10,
        int floorLevel = 10,
        int codeReload = 2,
        string mapHash = "map-a",
        NetherSessionStatus status = NetherSessionStatus.Play,
        long mapId = 2,
        int ticketCount = 3,
        int gold = 100,
        string codeHash = "30024:5:1",
        IReadOnlyList<NetherCodeState>? codes = null,
        IReadOnlyList<NetherRewardItem>? items = null
    ) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = mapId,
        CurrentFloorId = floorId,
        FloorLevel = floorLevel,
        FloorIndex = 0,
        ErosionPoint = 20,
        TicketCount = ticketCount,
        TreasureKeyCount = 1,
        NetherGold = gold,
        CodeReloadCount = codeReload,
        LockReward = 1,
        CharacterHpHash = "1:1000:1",
        CodeHash = codeHash,
        MapHash = mapHash,
        Codes = codes ?? Array.Empty<NetherCodeState>(),
        AcquiredItems = items ?? Array.Empty<NetherRewardItem>(),
    };
}
