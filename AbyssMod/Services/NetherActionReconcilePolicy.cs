#nullable enable

using System;

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
            NetherActionKind.SelectFloor => FloorAdvancedOrBattleStarted(before, after)
                ? NetherActionOutcome.Applied
                : NetherActionOutcome.Ambiguous,
            NetherActionKind.SelectEventOption => FloorAdvancedOrBattleStarted(before, after)
                || ResourcesOrCodesChanged(before, after)
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.BuyShopItem => after.NetherGold < before.NetherGold
                || AcquiredItemsChanged(before, after)
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.LeaveShop => after.Status != before.Status
                || after.CurrentFloorId != before.CurrentFloorId
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.SelectCode => !string.Equals(after.CodeHash, before.CodeHash, StringComparison.Ordinal)
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.ReloadCode => after.CodeReloadCount < before.CodeReloadCount
                || !string.Equals(after.CodeHash, before.CodeHash, StringComparison.Ordinal)
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.Continue => after.TicketCount < before.TicketCount
                || after.MapId != before.MapId
                || after.FloorLevel > before.FloorLevel
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.FinishAtCheckpoint => after.Status == NetherSessionStatus.Clear
                || after.Status == NetherSessionStatus.Lose
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            NetherActionKind.SelectReturnItems => after.LockReward < before.LockReward
                || AcquiredItemsChanged(before, after)
                    ? NetherActionOutcome.Applied
                    : NetherActionOutcome.Ambiguous,
            _ => NetherActionOutcome.Ambiguous,
        };
    }

    private static bool FloorAdvancedOrBattleStarted(NetherSnapshot before, NetherSnapshot after) =>
        after.CurrentFloorId != before.CurrentFloorId
        || after.FloorLevel != before.FloorLevel
        || (before.Status != NetherSessionStatus.Battle && after.Status == NetherSessionStatus.Battle);

    private static bool ResourcesOrCodesChanged(NetherSnapshot before, NetherSnapshot after) =>
        after.ErosionPoint != before.ErosionPoint
        || after.NetherGold != before.NetherGold
        || after.TreasureKeyCount != before.TreasureKeyCount
        || !string.Equals(after.CodeHash, before.CodeHash, StringComparison.Ordinal)
        || !string.Equals(after.CharacterHpHash, before.CharacterHpHash, StringComparison.Ordinal);

    private static bool AcquiredItemsChanged(NetherSnapshot before, NetherSnapshot after) =>
        !string.Equals(CreateItemIdentity(before), CreateItemIdentity(after), StringComparison.Ordinal);

    private static string CreateItemIdentity(NetherSnapshot snapshot) => string.Join(
        ";",
        snapshot.AcquiredItems
    );
}
