#nullable enable

using System;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Interprets a completed read-only Nether refresh after a native controller invocation.
/// It deliberately recognizes only action-specific server-owned postconditions.  Anything
/// else is ambiguous, so the controller pauses instead of replaying a non-idempotent action.
/// </summary>
internal static class NetherActionReconcilePolicy
{
    public static NetherActionOutcome Evaluate(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (before == null)
            throw new ArgumentNullException(nameof(before));
        if (after == null)
            throw new ArgumentNullException(nameof(after));

        return action.Kind switch
        {
            NetherActionKind.SelectFloor => EvaluateFloor(action, before, after),
            NetherActionKind.SelectEventOption => EvaluateEvent(action, before, after),
            NetherActionKind.BuyShopItem => EvaluateShopBuy(action, before, after),
            NetherActionKind.LeaveShop => UnchangedOrAmbiguous(before, after),
            NetherActionKind.SelectCode => EvaluateCodeSelect(action, before, after),
            NetherActionKind.ReloadCode => EvaluateCodeReload(before, after),
            NetherActionKind.Continue => EvaluateContinue(action, before, after),
            NetherActionKind.BattleSettlement => EvaluateBattleSettlement(action, before, after),
            NetherActionKind.FinishAtCheckpoint => after.Status == NetherSessionStatus.Clear
                || after.Status == NetherSessionStatus.Lose
                    ? NetherActionOutcome.Applied
                    : UnchangedOrAmbiguous(before, after),
            NetherActionKind.SelectReturnItems => after.LockReward < before.LockReward
                || AcquiredItemsChanged(before, after)
                    ? NetherActionOutcome.Applied
                    : UnchangedOrAmbiguous(before, after),
            _ => NetherActionOutcome.Ambiguous,
        };
    }

    private static NetherActionOutcome EvaluateFloor(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.FloorId <= 0
            || action.ExpectedBeforeStatus == NetherSessionStatus.Unknown
            || action.ExpectedAfterStatus == NetherSessionStatus.Unknown
            || before.Status != action.ExpectedBeforeStatus)
        {
            return NetherActionOutcome.Ambiguous;
        }

        return after.CurrentFloorId == action.FloorId && after.Status == action.ExpectedAfterStatus
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateEvent(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.OptionNumber < 0 || action.ExpectedEffects.Count == 0 || action.ExpectedEffects.Any(effect => !effect.Known || !effect.ContentKnown))
            return NetherActionOutcome.Ambiguous;

        int erosionDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
        {
            NetherEffectKind.Erosion => effect.Amount,
            NetherEffectKind.ErosionHeal => -effect.Amount,
            _ => 0,
        });
        int goldDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
        {
            NetherEffectKind.NetherGoldUsed => -effect.Amount,
            NetherEffectKind.NetherGoldGain => effect.Amount,
            _ => 0,
        });
        int keyDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
        {
            NetherEffectKind.TreasureKeyUsed => -effect.Amount,
            NetherEffectKind.TreasureKeyGain => effect.Amount,
            _ => 0,
        });
        bool containsOnlyServerVisibleResources = action.ExpectedEffects.All(effect => effect.Kind is
            NetherEffectKind.Erosion or NetherEffectKind.ErosionHeal or
            NetherEffectKind.NetherGoldUsed or NetherEffectKind.NetherGoldGain or
            NetherEffectKind.TreasureKeyUsed or NetherEffectKind.TreasureKeyGain
        );
        if (!containsOnlyServerVisibleResources)
            return NetherActionOutcome.Ambiguous;

        return after.ErosionPoint == before.ErosionPoint + erosionDelta
            && after.NetherGold == before.NetherGold + goldDelta
            && after.TreasureKeyCount == before.TreasureKeyCount + keyDelta
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateShopBuy(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.ContentId <= 0 || action.ContentAmount <= 0 || action.GoldCost < 0)
            return NetherActionOutcome.Ambiguous;

        int itemDelta = GetAcquiredItemAmount(after, action.ContentId) - GetAcquiredItemAmount(before, action.ContentId);
        return itemDelta == action.ContentAmount
            && after.NetherGold == before.NetherGold - action.GoldCost
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateCodeSelect(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.CodeId <= 0 || ContainsCode(before, action.CodeId))
            return NetherActionOutcome.Ambiguous;
        if (action.ReplaceCodeId > 0 && !ContainsCode(before, action.ReplaceCodeId))
            return NetherActionOutcome.Ambiguous;

        bool selected = ContainsCode(after, action.CodeId);
        bool replacementRemoved = action.ReplaceCodeId <= 0 || !ContainsCode(after, action.ReplaceCodeId);
        return selected && replacementRemoved
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateCodeReload(NetherSnapshot before, NetherSnapshot after) =>
        after.CodeReloadCount == before.CodeReloadCount - 1
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);

    private static NetherActionOutcome EvaluateContinue(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.TicketCost <= 0
            || action.ExpectedMapId <= 0
            || action.ExpectedSegmentFloorLevel <= 0
            || before.TicketCount < action.TicketCost)
        {
            return NetherActionOutcome.Ambiguous;
        }

        return after.TicketCount == before.TicketCount - action.TicketCost
            && after.MapId == action.ExpectedMapId
            && after.FloorLevel == action.ExpectedSegmentFloorLevel
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateBattleSettlement(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        NetherBattleSettlementContract? contract = action.BattleSettlement;
        if (contract == null
            || contract.EntryStatus != NetherSessionStatus.Battle
            || contract.ExpectedStatus == NetherSessionStatus.Unknown
            || contract.EntryMapId <= 0
            || contract.EntryFloorId <= 0
            || contract.ExpectedMapId <= 0
            || contract.ExpectedFloorId <= 0
            || before.Status != contract.EntryStatus
            || before.MapId != contract.EntryMapId
            || before.CurrentFloorId != contract.EntryFloorId)
        {
            return NetherActionOutcome.Ambiguous;
        }

        return after.Status == contract.ExpectedStatus
            && after.MapId == contract.ExpectedMapId
            && after.CurrentFloorId == contract.ExpectedFloorId
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome UnchangedOrAmbiguous(NetherSnapshot before, NetherSnapshot after) =>
        IsAuthoritativelyUnchanged(before, after)
            ? NetherActionOutcome.NotApplied
            : NetherActionOutcome.Ambiguous;

    private static bool IsAuthoritativelyUnchanged(NetherSnapshot before, NetherSnapshot after) =>
        before.Fingerprint == after.Fingerprint
        && string.Equals(CreateItemIdentity(before), CreateItemIdentity(after), StringComparison.Ordinal)
        && string.Equals(CreateCodeIdentity(before), CreateCodeIdentity(after), StringComparison.Ordinal);

    private static bool ContainsCode(NetherSnapshot snapshot, long codeId) =>
        snapshot.Codes.Any(code => code.CodeId == codeId);

    private static int GetAcquiredItemAmount(NetherSnapshot snapshot, long contentId) =>
        snapshot.AcquiredItems.Where(item => item.ItemId == contentId).Sum(item => item.Amount);

    private static bool AcquiredItemsChanged(NetherSnapshot before, NetherSnapshot after) =>
        !string.Equals(CreateItemIdentity(before), CreateItemIdentity(after), StringComparison.Ordinal);

    private static string CreateItemIdentity(NetherSnapshot snapshot) => string.Join(
        ";",
        snapshot.AcquiredItems
    );

    private static string CreateCodeIdentity(NetherSnapshot snapshot) => string.Join(
        ";",
        snapshot.Codes.Select(code => code.CodeId + ":" + code.Level + ":" + (int)code.EffectKind)
    );
}
