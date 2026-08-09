#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherFloorActionTransactionComposerTests
{
    [Fact]
    public void Event_floor_parent_composes_selected_option_effects_and_exact_play_terminal()
    {
        NetherPlannedAction parent = Parent();
        var popup = new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event };
        var child = new NetherPlannedAction(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 2,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.ErosionHeal, 3) },
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(parent, popup, child, out NetherPlannedAction composed));
        Assert.Equal(NetherActionKind.SelectFloor, composed.Kind);
        Assert.Equal(11, composed.FloorId);
        Assert.Equal(NetherSessionStatus.Play, composed.ExpectedAfterStatus);
        Assert.Equal(2, composed.OptionNumber);
        Assert.Single(composed.ExpectedEffects);
    }

    [Fact]
    public void Event_triggered_battle_composes_exact_battle_terminal_instead_of_unknown()
    {
        var popup = new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event };
        var child = new NetherPlannedAction(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.Battle, 0) },
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(Parent(), popup, child, out NetherPlannedAction composed));
        Assert.Equal(NetherSessionStatus.Battle, composed.ExpectedAfterStatus);
    }

    [Fact]
    public void Shop_buy_composes_exact_content_amount_and_gold_cost()
    {
        var popup = new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Shop };
        var child = new NetherPlannedAction(NetherActionKind.BuyShopItem)
        {
            ContentId = 9001,
            ContentAmount = 2,
            GoldCost = 7,
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(Parent(), popup, child, out NetherPlannedAction composed));
        Assert.Equal(NetherSessionStatus.Play, composed.ExpectedAfterStatus);
        Assert.Equal(9001, composed.ContentId);
        Assert.Equal(2, composed.ContentAmount);
        Assert.Equal(7, composed.GoldCost);
    }

    [Theory]
    [InlineData((int)NetherRuntimePopupKind.Recovery)]
    [InlineData((int)NetherRuntimePopupKind.Treasure)]
    public void Other_event_style_owned_popups_keep_the_exact_selected_option(int rawKind)
    {
        NetherRuntimePopupKind kind = (NetherRuntimePopupKind)rawKind;
        var child = new NetherPlannedAction(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 3,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.Heal, 20) },
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = kind },
            child,
            out NetherPlannedAction composed
        ));
        Assert.Equal(kind, composed.OwnedPopupKind);
        Assert.Equal(NetherActionKind.SelectEventOption, composed.OwnedPopupActionKind);
        Assert.Equal(3, composed.OptionNumber);
        Assert.Equal(NetherSessionStatus.Play, composed.ExpectedAfterStatus);
    }

    [Fact]
    public void Shop_leave_and_code_select_are_concrete_parent_transactions()
    {
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Shop },
            new NetherPlannedAction(NetherActionKind.LeaveShop),
            out NetherPlannedAction leave
        ));
        Assert.Equal(NetherActionKind.LeaveShop, leave.OwnedPopupActionKind);
        Assert.Equal(NetherSessionStatus.Play, leave.ExpectedAfterStatus);

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024, ReplaceCodeId = 40024 },
            out NetherPlannedAction code
        ));
        Assert.Equal(NetherActionKind.SelectCode, code.OwnedPopupActionKind);
        Assert.Equal(30024, code.CodeId);
        Assert.Equal(40024, code.ReplaceCodeId);
    }

    [Fact]
    public void Unknown_effect_or_mismatched_popup_refuses_to_create_a_reconcile_transaction()
    {
        var badEffect = new NetherPlannedAction(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[] { new NetherEffect(NetherEffectKind.Unknown, 0) { Known = false } },
        };

        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event },
            badEffect,
            out _
        ));
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Shop },
            badEffect,
            out _
        ));
    }

    [Fact]
    public void Same_floor_parent_keeps_event_effects_when_its_code_change_opens_a_second_owned_popup()
    {
        NetherPlannedAction parent = Parent();
        NetherPlannedAction eventChoice = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[]
            {
                new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
            },
        };
        NetherPlannedAction codeChoice = new(NetherActionKind.SelectCode)
        {
            CodeId = 30024,
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event, OwnerGeneration = 7, Sequence = 1 },
            eventChoice,
            out NetherPlannedAction afterEvent
        ));
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            afterEvent,
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer, OwnerGeneration = 7, Sequence = 2 },
            codeChoice,
            out NetherPlannedAction afterCode
        ));

        Assert.Contains(afterCode.ExpectedEffects, effect => effect.Kind == NetherEffectKind.NetherGoldGain && effect.Amount == 5);
        Assert.Contains(afterCode.ExpectedEffects, effect => effect.Kind == NetherEffectKind.AbyssCodeOffer);
        Assert.Equal(30024, afterCode.CodeId);
    }

    [Fact]
    public void Same_parent_rejects_duplicate_effect_keys_and_stale_or_conflicting_code_stage()
    {
        NetherPlannedAction duplicateEffect = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[]
            {
                new NetherEffect(NetherEffectKind.NetherGoldGain, 1),
                new NetherEffect(NetherEffectKind.NetherGoldGain, 2),
            },
        };
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event, OwnerGeneration = 7, Sequence = 1 },
            duplicateEffect,
            out _
        ));

        NetherPlannedAction eventChoice = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[]
            {
                new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
            },
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            Parent(),
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Event, OwnerGeneration = 7, Sequence = 1 },
            eventChoice,
            out NetherPlannedAction afterEvent
        ));

        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            afterEvent,
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer, OwnerGeneration = 7, Sequence = 1 },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
            out _
        ));
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            afterEvent,
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer, OwnerGeneration = 7, Sequence = 2 },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 40024 },
            out _
        ));
    }

    [Fact]
    public void Reload_stage_requires_a_new_epoch_terminal_code_selection_before_parent_may_reconcile()
    {
        NetherPlannedAction parent = Parent();
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 7,
                Sequence = 2,
                DecisionEpoch = 0,
            },
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out NetherPlannedAction afterReload
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterReload));

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterReload,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 7,
                Sequence = 2,
                DecisionEpoch = 1,
            },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
            out NetherPlannedAction afterSelect
        ));
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterSelect));
    }

    [Fact]
    public void Same_code_offer_allows_strictly_increasing_reload_epochs_until_one_final_select()
    {
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext epoch0 = new()
        {
            Kind = NetherRuntimePopupKind.CodeOffer,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = 7,
            Sequence = 2,
            DecisionEpoch = 0,
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            epoch0,
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out NetherPlannedAction afterFirstReload
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterFirstReload));

        // The same owner/sequence may reroll again only after the bridge has proven a newer
        // same-popup decision epoch.  A same-epoch replay remains stale and must not append.
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterFirstReload,
            epoch0,
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out _
        ));

        NetherRuntimePopupContext epoch1 = epoch0 with { DecisionEpoch = 1 };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterFirstReload,
            epoch1,
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out NetherPlannedAction afterSecondReload
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterSecondReload));

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterSecondReload,
            epoch0 with { DecisionEpoch = 2 },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
            out NetherPlannedAction afterSelect
        ));

        Assert.Equal(3, afterSelect.OwnedPopupStages.Count);
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterSelect));
    }

    [Fact]
    public void Direct_keep_is_a_complete_owned_code_terminal_without_spending_a_reload()
    {
        NetherPlannedAction parent = Parent();
        var popup = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.CodeOffer,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = 7,
            Sequence = 2,
            DecisionEpoch = 0,
        };

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            popup,
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out NetherPlannedAction kept
        ));
        Assert.Equal(NetherActionKind.KeepCode, kept.OwnedPopupActionKind);
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(kept));
    }

    [Fact]
    public void Strict_reload_epochs_may_end_in_one_keep_but_stale_or_duplicate_keep_is_rejected()
    {
        NetherPlannedAction parent = Parent();
        var epoch0 = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.CodeOffer,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = 7,
            Sequence = 2,
            DecisionEpoch = 0,
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            epoch0,
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out NetherPlannedAction afterFirstReload
        ));
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterFirstReload,
            epoch0 with { DecisionEpoch = 1 },
            new NetherPlannedAction(NetherActionKind.ReloadCode),
            out NetherPlannedAction afterSecondReload
        ));
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterSecondReload,
            epoch0 with { DecisionEpoch = 1 },
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out _
        ));

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterSecondReload,
            epoch0 with { DecisionEpoch = 2 },
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out NetherPlannedAction kept
        ));
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(kept));
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            kept,
            epoch0 with { DecisionEpoch = 3 },
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out _
        ));
    }

    [Fact]
    public void Code_child_of_a_battle_event_inherits_the_parent_battle_terminal()
    {
        NetherPlannedAction parent = Parent();
        NetherPlannedAction eventChoice = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = new[]
            {
                new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
                new NetherEffect(NetherEffectKind.Battle, 0),
            },
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Event,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 7,
                Sequence = 1,
            },
            eventChoice,
            out NetherPlannedAction afterEvent
        ));
        Assert.Equal(NetherSessionStatus.Battle, afterEvent.ExpectedAfterStatus);

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterEvent,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 7,
                Sequence = 2,
            },
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
            out NetherPlannedAction afterCode
        ));

        Assert.All(afterCode.OwnedPopupStages, stage =>
            Assert.Equal(NetherSessionStatus.Battle, stage.ExpectedAfterStatus));
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterCode));
    }

    [Fact]
    public void Native_code_offer_trigger_requires_one_terminal_code_offer_stage()
    {
        NetherPlannedAction parent = Parent();
        NetherPlannedAction eventChoice = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects = [new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1)],
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            Popup(NetherRuntimePopupKind.Event, 1),
            eventChoice,
            out NetherPlannedAction afterEvent
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterEvent));

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterEvent,
            Popup(NetherRuntimePopupKind.CodeOffer, 2),
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 51001 },
            out NetherPlannedAction complete
        ));
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(complete));
    }

    [Fact]
    public void Native_transform_then_offer_children_are_required_in_exact_order()
    {
        NetherPlannedAction parent = Parent();
        NetherPlannedAction eventChoice = new(NetherActionKind.SelectEventOption)
        {
            OptionNumber = 1,
            ExpectedEffects =
            [
                new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                new NetherEffect(NetherEffectKind.AbyssCodeTransform, 0),
                new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1),
            ],
        };
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            Popup(NetherRuntimePopupKind.Event, 1),
            eventChoice,
            out NetherPlannedAction afterEvent
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterEvent));
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterEvent,
            Popup(NetherRuntimePopupKind.CodeOffer, 2),
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out _
        ));

        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterEvent,
            Popup(NetherRuntimePopupKind.CodeTransform, 2),
            new NetherPlannedAction(NetherActionKind.TransformCode) { ReplaceCodeId = 40024 },
            out NetherPlannedAction afterTransform
        ));
        Assert.False(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(afterTransform));
        Assert.True(NetherFloorActionTransactionComposer.TryCompose(
            parent,
            afterTransform,
            Popup(NetherRuntimePopupKind.CodeOffer, 3),
            new NetherPlannedAction(NetherActionKind.KeepCode),
            out NetherPlannedAction complete
        ));
        Assert.True(NetherFloorActionTransactionComposer.IsCompleteForParentTerminal(complete));
        Assert.Equal(
            new[]
            {
                NetherActionKind.SelectEventOption,
                NetherActionKind.TransformCode,
                NetherActionKind.KeepCode,
            },
            complete.OwnedPopupStages.Select(stage => stage.ActionKind).ToArray()
        );
    }

    private static NetherPlannedAction Parent() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 11,
        FloorLevel = 3,
        FloorIndex = 2,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
        ExpectedAfterStatus = NetherSessionStatus.Wait,
    };

    private static NetherRuntimePopupContext Popup(NetherRuntimePopupKind kind, long sequence) => new()
    {
        Kind = kind,
        OwnerAction = NetherActionKind.SelectFloor,
        OwnerGeneration = 7,
        Sequence = sequence,
    };
}
