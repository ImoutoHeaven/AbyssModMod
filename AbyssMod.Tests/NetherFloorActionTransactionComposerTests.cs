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
                new NetherEffect(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
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
        Assert.Contains(afterCode.ExpectedEffects, effect => effect.Kind == NetherEffectKind.AbyssCodeChanged && effect.ReplacementCodeId == 30024);
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
                new NetherEffect(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
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
        Assert.False(NetherFloorActionTransactionComposer.TryCompose(
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

    private static NetherPlannedAction Parent() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 11,
        FloorLevel = 3,
        FloorIndex = 2,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
        ExpectedAfterStatus = NetherSessionStatus.Wait,
    };
}
