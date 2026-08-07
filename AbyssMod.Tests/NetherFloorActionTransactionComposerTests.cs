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

    private static NetherPlannedAction Parent() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 11,
        FloorLevel = 3,
        FloorIndex = 2,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
        ExpectedAfterStatus = NetherSessionStatus.Wait,
    };
}
