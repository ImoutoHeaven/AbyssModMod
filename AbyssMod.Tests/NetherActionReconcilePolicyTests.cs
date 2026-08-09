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
    public void Direct_code_select_requires_zero_reload_delta_when_no_reload_stage_was_retained()
    {
        NetherSnapshot before = Snapshot(codeReload: 2, codeHash: "codes:none") with
        {
            Codes = Array.Empty<NetherCodeState>(),
        };
        NetherSnapshot wrongReloadDelta = Snapshot(codeReload: 1, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };

        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(
                new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
                before,
                wrongReloadDelta
            )
        );
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
    public void Composed_recovery_treasure_and_event_effects_do_not_accept_wrong_hp_or_item()
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
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongItem));
    }

    [Fact]
    public void Composed_event_damage_requires_the_exact_delta_for_each_active_party_member()
    {
        NetherSnapshot before = Snapshot(floorId: 10) with
        {
            Characters = new[]
            {
                new NetherCharacterState(101, 1000),
                new NetherCharacterState(102, 1000),
                new NetherCharacterState(103, 0, IsActive: false),
            },
            CharacterHpHash = "101:1000:1;102:1000:1;103:0:0",
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11) with
        {
            Characters = new[]
            {
                new NetherCharacterState(101, 900),
                new NetherCharacterState(102, 900),
                new NetherCharacterState(103, 0, IsActive: false),
            },
            CharacterHpHash = "101:900:1;102:900:1;103:0:0",
        };
        NetherSnapshot wrongMemberDelta = exact with
        {
            Characters = new[]
            {
                new NetherCharacterState(101, 900),
                new NetherCharacterState(102, 800),
                new NetherCharacterState(103, 0, IsActive: false),
            },
            CharacterHpHash = "101:900:1;102:800:1;103:0:0",
        };
        NetherEffect[] effects = { new(NetherEffectKind.Damage, 100) };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.Event,
            NetherActionKind.SelectEventOption
        ) with
        {
            OptionNumber = 1,
            ExpectedEffects = effects,
            OwnedPopupStages = new[]
            {
                new NetherFloorPopupStage(
                    NetherRuntimePopupKind.Event,
                    NetherActionKind.SelectEventOption,
                    OwnerGeneration: 7,
                    Sequence: 1,
                    ExpectedAfterStatus: NetherSessionStatus.Play,
                    OptionNumber: 1,
                    ExpectedEffects: effects,
                    ContentId: 0,
                    ContentAmount: 0,
                    GoldCost: 0,
                    CodeId: 0,
                    ReplaceCodeId: 0
                ),
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(action, before, wrongMemberDelta));
    }

    [Fact]
    public void Multi_stage_event_then_code_parent_requires_both_effect_contracts_from_one_get()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 20, codeHash: "codes:none") with
        {
            Codes = Array.Empty<NetherCodeState>(),
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, gold: 25, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.SelectCode
        ) with
        {
            CodeId = 30024,
            OwnedPopupStages = new NetherFloorPopupStage[]
            {
                new(
                    NetherRuntimePopupKind.Event,
                    NetherActionKind.SelectEventOption,
                    OwnerGeneration: 7,
                    Sequence: 1,
                    ExpectedAfterStatus: NetherSessionStatus.Play,
                    OptionNumber: 1,
                    ExpectedEffects: new NetherEffect[]
                    {
                        new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                        new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
                    },
                    ContentId: 0,
                    ContentAmount: 0,
                    GoldCost: 0,
                    CodeId: 0,
                    ReplaceCodeId: 0
                ),
                new(
                    NetherRuntimePopupKind.CodeOffer,
                    NetherActionKind.SelectCode,
                    OwnerGeneration: 7,
                    Sequence: 2,
                    ExpectedAfterStatus: NetherSessionStatus.Play,
                    OptionNumber: 0,
                    ExpectedEffects: Array.Empty<NetherEffect>(),
                    ContentId: 0,
                    ContentAmount: 0,
                    GoldCost: 0,
                    CodeId: 30024,
                    ReplaceCodeId: 0
                ),
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { NetherGold = 24 }
        ));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with
            {
                Codes = new[] { new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1) },
                CodeHash = "codes:40024",
            }
        ));
    }

    [Fact]
    public void Multi_reload_parent_aggregates_exact_reload_consumption_once_before_final_code_select()
    {
        NetherSnapshot before = Snapshot(floorId: 10, codeReload: 3, codeHash: "codes:none") with
        {
            Codes = Array.Empty<NetherCodeState>(),
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, codeReload: 1, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.SelectCode
        ) with
        {
            CodeId = 30024,
            OwnedPopupStages = new NetherFloorPopupStage[]
            {
                CodeStage(NetherActionKind.ReloadCode, epoch: 0),
                CodeStage(NetherActionKind.ReloadCode, epoch: 1),
                CodeStage(NetherActionKind.SelectCode, epoch: 2, codeId: 30024),
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { CodeReloadCount = 2 }
        ));
    }

    [Fact]
    public void Keep_code_requires_an_unchanged_portfolio_and_unchanged_reload_count()
    {
        NetherSnapshot before = Snapshot(codeReload: 2, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherSnapshot alteredPortfolio = before with
        {
            Codes = new[] { new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1) },
            CodeHash = "codes:40024",
        };

        Assert.Equal(
            NetherActionOutcome.Applied,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.KeepCode), before, before)
        );
        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.KeepCode), before, alteredPortfolio)
        );
        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(
                new NetherPlannedAction(NetherActionKind.KeepCode),
                before,
                before with { CodeReloadCount = 1 }
            )
        );
    }

    [Fact]
    public void Reload_then_keep_aggregates_reload_consumption_while_preserving_the_original_portfolio()
    {
        NetherSnapshot before = Snapshot(floorId: 10, codeReload: 2, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, codeReload: 1, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.KeepCode
        ) with
        {
            OwnedPopupStages = new NetherFloorPopupStage[]
            {
                CodeStage(NetherActionKind.ReloadCode, epoch: 0),
                CodeStage(NetherActionKind.KeepCode, epoch: 1),
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { CodeReloadCount = 2 }
        ));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with
            {
                Codes = new[] { new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1) },
                CodeHash = "codes:40024",
            }
        ));
    }

    [Fact]
    public void Composed_code_terminals_require_an_exact_zero_reload_delta_when_there_are_no_reload_stages()
    {
        NetherSnapshot selectBefore = Snapshot(floorId: 10, codeReload: 2, codeHash: "codes:none") with
        {
            Codes = Array.Empty<NetherCodeState>(),
        };
        NetherSnapshot selectWrongReload = Snapshot(floorId: 11, floorLevel: 11, codeReload: 1, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction select = ComposedFloor(NetherRuntimePopupKind.CodeOffer, NetherActionKind.SelectCode) with
        {
            CodeId = 30024,
            OwnedPopupStages = new[] { CodeStage(NetherActionKind.SelectCode, epoch: 0, codeId: 30024) },
        };

        NetherSnapshot keepBefore = Snapshot(floorId: 10, codeReload: 2, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherSnapshot keepWrongReload = Snapshot(floorId: 11, floorLevel: 11, codeReload: 1, codeHash: "codes:30024") with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction keep = ComposedFloor(NetherRuntimePopupKind.CodeOffer, NetherActionKind.KeepCode) with
        {
            OwnedPopupStages = new[] { CodeStage(NetherActionKind.KeepCode, epoch: 0) },
        };

        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(select, selectBefore, selectWrongReload));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(keep, keepBefore, keepWrongReload));
    }

    [Fact]
    public void Event_code_change_battle_and_resource_stage_all_require_one_final_battle_snapshot()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 20, codeHash: "codes:none") with
        {
            Codes = Array.Empty<NetherCodeState>(),
        };
        NetherSnapshot exact = Snapshot(
            floorId: 11,
            floorLevel: 11,
            gold: 25,
            codeHash: "codes:30024",
            status: NetherSessionStatus.Battle
        ) with
        {
            Codes = new[] { new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1) },
        };
        NetherPlannedAction action = ComposedFloor(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.SelectCode
        ) with
        {
            ExpectedAfterStatus = NetherSessionStatus.Battle,
            CodeId = 30024,
            OwnedPopupStages = new NetherFloorPopupStage[]
            {
                new(
                    NetherRuntimePopupKind.Event,
                    NetherActionKind.SelectEventOption,
                    OwnerGeneration: 7,
                    Sequence: 1,
                    ExpectedAfterStatus: NetherSessionStatus.Battle,
                    OptionNumber: 1,
                    ExpectedEffects: new NetherEffect[]
                    {
                        new(NetherEffectKind.NetherGoldGain, 5),
                        new(NetherEffectKind.AbyssCodeOffer, 1),
                        new(NetherEffectKind.Battle, 0),
                    },
                    ContentId: 0,
                    ContentAmount: 0,
                    GoldCost: 0,
                    CodeId: 0,
                    ReplaceCodeId: 0
                ),
                CodeStage(NetherActionKind.SelectCode, epoch: 0, codeId: 30024, terminal: NetherSessionStatus.Battle),
            },
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { NetherGold = 24 }
        ));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with
            {
                Codes = new[] { new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1) },
                CodeHash = "codes:40024",
            }
        ));
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

    [Fact]
    public void Transform_code_requires_exact_one_removed_one_added_and_preserves_other_codes()
    {
        NetherSnapshot before = Snapshot(codeHash: "30024|40024") with
        {
            Codes =
            [
                new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1),
                new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1),
            ],
        };
        NetherSnapshot exact = before with
        {
            Codes =
            [
                new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1),
                new NetherCodeState(51001, NetherCodeEffectKind.General, 1),
            ],
            CodeHash = "30024|51001",
        };
        NetherPlannedAction action = new(NetherActionKind.TransformCode) { ReplaceCodeId = 40024 };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { Codes = [new NetherCodeState(51001, NetherCodeEffectKind.General, 1)], CodeHash = "51001" }
        ));
        Assert.Equal(NetherActionOutcome.NotApplied, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            before
        ));
    }

    [Fact]
    public void Composed_event_transform_and_offer_reconcile_each_contract_once()
    {
        NetherSnapshot before = Snapshot(floorId: 10, gold: 20, codeHash: "30024|40024") with
        {
            Codes =
            [
                new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1),
                new NetherCodeState(40024, NetherCodeEffectKind.Risk, 1),
            ],
        };
        NetherSnapshot exact = Snapshot(floorId: 11, floorLevel: 11, gold: 25, codeHash: "30024|51001") with
        {
            Codes =
            [
                new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1),
                new NetherCodeState(51001, NetherCodeEffectKind.General, 1),
            ],
        };
        NetherPlannedAction action = ComposedFloor(NetherRuntimePopupKind.CodeOffer, NetherActionKind.KeepCode) with
        {
            OwnedPopupStages =
            [
                new NetherFloorPopupStage(
                    NetherRuntimePopupKind.Event,
                    NetherActionKind.SelectEventOption,
                    7, 1, NetherSessionStatus.Play, 1,
                    [
                        new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                        new NetherEffect(NetherEffectKind.AbyssCodeTransform, 0),
                        new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
                    ],
                    0, 0, 0, 0, 0
                ),
                new NetherFloorPopupStage(
                    NetherRuntimePopupKind.CodeTransform,
                    NetherActionKind.TransformCode,
                    7, 2, NetherSessionStatus.Play, 0,
                    Array.Empty<NetherEffect>(),
                    0, 0, 0, 0, 40024
                ),
                CodeStage(NetherActionKind.KeepCode, epoch: 0) with { Sequence = 3 },
            ],
        };

        Assert.Equal(NetherActionOutcome.Applied, NetherActionReconcilePolicy.Evaluate(action, before, exact));
        Assert.Equal(NetherActionOutcome.Ambiguous, NetherActionReconcilePolicy.Evaluate(
            action,
            before,
            exact with { NetherGold = 24 }
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

    private static NetherFloorPopupStage CodeStage(
        NetherActionKind action,
        long epoch,
        long codeId = 0,
        NetherSessionStatus terminal = NetherSessionStatus.Play
    ) => new(
        NetherRuntimePopupKind.CodeOffer,
        action,
        OwnerGeneration: 7,
        Sequence: 2,
        ExpectedAfterStatus: terminal,
        OptionNumber: 0,
        ExpectedEffects: Array.Empty<NetherEffect>(),
        ContentId: 0,
        ContentAmount: 0,
        GoldCost: 0,
        CodeId: codeId,
        ReplaceCodeId: 0,
        DecisionEpoch: epoch
    );

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
