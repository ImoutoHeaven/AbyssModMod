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
    public void Exact_continue_ticket_map_floor_and_segment_is_applied_but_wrong_target_is_not()
    {
        NetherSnapshot before = Snapshot(ticketCount: 3, mapId: 2, floorLevel: 10);
        NetherSnapshot exact = Snapshot(floorId: 33, ticketCount: 2, mapId: 3, floorLevel: 11);
        NetherSnapshot wrongTicket = Snapshot(floorId: 33, ticketCount: 1, mapId: 3, floorLevel: 11);
        NetherSnapshot wrongMap = Snapshot(floorId: 33, ticketCount: 2, mapId: 4, floorLevel: 11);
        NetherSnapshot wrongFloor = Snapshot(floorId: 34, ticketCount: 2, mapId: 3, floorLevel: 11);
        NetherPlannedAction action = new(NetherActionKind.Continue)
        {
            TicketCost = 1,
            ExpectedMapId = 3,
            ExpectedFloorId = 33,
            ExpectedSegmentFloorLevel = 11,
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongTicket));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongMap));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongFloor));
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

    [Fact]
    public void Composed_event_parent_requires_the_exact_floor_status_and_resource_effects()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 20);
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, gold: 23);
        NetherSnapshot wrongGold = Snapshot(floorId: 11, floorLevel: 11, gold: 22);
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.Event,
            NetherActionKind.SelectEventOption
        ) with
        {
            OptionNumber = 2,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.NetherGoldGain, 3) },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongGold));
    }

    [Fact]
    public void Composed_recovery_treasure_and_event_effects_do_not_accept_wrong_hp_item_or_code()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 20) with
        {
            Characters = new[] { new NetherCharacterState(1, 900) },
            CharacterHpHash = "1:900:1",
            AcquiredItems = Array.Empty<NetherRewardItem>(),
            Codes = Array.Empty<NetherCodeState>(),
            CodeHash = "codes:none",
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, gold: 20, codeHash: "codes:30024") with
        {
            Characters = new[] { new NetherCharacterState(1, 920) },
            CharacterHpHash = "1:920:1",
            AcquiredItems = new[] { new NetherRewardItem(7001, 1) },
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherSnapshot wrongItem = exact with { AcquiredItems = new[] { new NetherRewardItem(7002, 1) } };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.Recovery,
            NetherActionKind.SelectEventOption
        ) with
        {
            OptionNumber = 1,
            ExpectedEffects = new[]
            {
                new NetherEffect(NetherEffectKind.Heal, 20),
                new NetherEffect(NetherEffectKind.Item, 1) { ContentId = 7001 },
                new NetherEffect(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongItem));
    }

    [Fact]
    public void Composed_shop_buy_and_battle_option_require_their_own_terminal_contract()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 100);
        NetherSnapshot bought = Snapshot(floorId: 11, floorLevel: 11, gold: 80) with
        {
            AcquiredItems = new[] { new NetherRewardItem(42, 1) },
        };
        NetherPlannedAction buy = ComposedFloor(NetherRuntimePopupKind.Shop, NetherActionKind.BuyShopItem) with
        {
            ContentId = 42,
            ContentAmount = 1,
            GoldCost = 20,
        };
        NetherPlannedAction battle = ComposedFloor(NetherRuntimePopupKind.Treasure, NetherActionKind.SelectEventOption) with
        {
            ExpectedAfterStatus = NetherSessionStatus.Battle,
            OptionNumber = 1,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.Battle, 0) },
        };
        NetherSnapshot battleAfter = Snapshot(floorId: 11, floorLevel: 11, status: NetherSessionStatus.Battle, gold: 100);

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(buy, before, bought));
        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(battle, before, battleAfter));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            battle,
            before,
            battleAfter with { Status = NetherSessionStatus.Play }
        ));
    }

    private static NetherPlannedAction ComposedFloor(
        NetherRuntimePopupKind popup,
        NetherActionKind child
    ) => new(NetherActionKind.SelectFloor)
    {
        FloorId = 11,
        FloorLevel = 11,
        FloorIndex = 0,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
        ExpectedAfterStatus = NetherSessionStatus.Play,
        OwnedPopupKind = popup,
        OwnedPopupActionKind = child,
    };

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
