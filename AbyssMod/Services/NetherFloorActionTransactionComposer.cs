#nullable enable

using System;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Folds an owned modal action into the original SelectFloor parent.  The server mutation is a
/// single native parent chain, so reconciliation must retain both its floor target and the
/// popup-specific option/content contract rather than reconcile an Unknown floor transition.
/// </summary>
internal static class NetherFloorActionTransactionComposer
{
    public static bool TryCompose(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        out NetherPlannedAction composed
    )
    {
        composed = default;
        if (parent.Kind != NetherActionKind.SelectFloor
            || parent.FloorId <= 0
            || parent.FloorLevel < 1
            || parent.FloorIndex < 0
            || parent.ExpectedBeforeStatus != NetherSessionStatus.Play
            || popup == null)
        {
            return false;
        }

        NetherSessionStatus terminal = popup.Kind switch
        {
            NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
                when child.Kind == NetherActionKind.SelectEventOption && IsExactEventAction(child) =>
                    child.ExpectedEffects.Any(effect => effect.Kind == NetherEffectKind.Battle)
                        ? NetherSessionStatus.Battle
                        : NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.LeaveShop => NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.BuyShopItem && IsExactShopBuy(child) =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.SelectCode && child.CodeId > 0 =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.ReloadCode =>
                NetherSessionStatus.Play,
            _ => NetherSessionStatus.Unknown,
        };
        if (terminal == NetherSessionStatus.Unknown)
            return false;

        composed = parent with
        {
            ExpectedAfterStatus = terminal,
            OwnedPopupKind = popup.Kind,
            OwnedPopupActionKind = child.Kind,
            OptionNumber = child.OptionNumber,
            ExpectedEffects = child.ExpectedEffects?.ToArray() ?? Array.Empty<NetherEffect>(),
            ContentId = child.ContentId,
            ContentAmount = child.ContentAmount,
            GoldCost = child.GoldCost,
            CodeId = child.CodeId,
            ReplaceCodeId = child.ReplaceCodeId,
        };
        return true;
    }

    private static bool IsExactEventAction(NetherPlannedAction action) =>
        action.OptionNumber > 0
        && action.ExpectedEffects != null
        && action.ExpectedEffects.Count > 0
        && action.ExpectedEffects.All(effect => effect != null
            && effect.Known
            && effect.ContentKnown
            && effect.Kind != NetherEffectKind.Unknown
            && effect.Amount >= 0
            && (effect.Kind != NetherEffectKind.AbyssCodeChanged || effect.ReplacementCodeId > 0)
            && (effect.Kind != NetherEffectKind.Item || effect.ContentId > 0));

    private static bool IsExactShopBuy(NetherPlannedAction action) =>
        action.ContentId > 0 && action.ContentAmount > 0 && action.GoldCost >= 0;
}
