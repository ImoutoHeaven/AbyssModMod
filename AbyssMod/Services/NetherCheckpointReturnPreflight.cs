#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AbyssMod.Services;

/// <summary>
/// The authoritative portion of a carry-out candidate before the native return popup exists.
/// The caller must set every knowledge flag from the live NetherDataStore/master mapping; a
/// missing flag is never interpreted as a low-value item.
/// </summary>
internal sealed record NetherCheckpointReturnPreflightItem(long ItemId, int Amount)
{
    public bool HasMasterData { get; init; } = true;
    public bool HasContentData { get; init; } = true;
    public bool HasRarityData { get; init; } = true;
    public int ContentType { get; init; }
    public int MasterRarity { get; init; }
}

internal enum NetherCheckpointReturnPreflightKind
{
    NoReturn,
    Ready,
    Pause,
}

/// <summary>
/// A fail-closed preflight result.  The hash deliberately covers each whole item entry and its
/// mapped master semantics, while the selection retains the original entry amount unchanged.
/// </summary>
internal sealed record NetherCheckpointReturnPreflightDecision
{
    public NetherCheckpointReturnPreflightKind Kind { get; init; }
    public int SelectionLimit { get; init; }
    public string ExpectedPristineHash { get; init; } = string.Empty;
    public IReadOnlyList<NetherCheckpointReturnPreflightItem> WholeEntrySelection { get; init; }
        = Array.Empty<NetherCheckpointReturnPreflightItem>();
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Computes a pre-Continue carry-out contract from the live datastore.  It intentionally does
/// not invoke a native callback: the bridge must obtain this result before starting the native
/// Continue parent, then compare it with the fresh popup model before OnConfirmAsync.
/// </summary>
internal sealed class NetherCheckpointReturnPreflight
{
    private const int EquipmentContentType = 91;

    /// <summary>
    /// This is the only preflight result that authorizes starting the native Continue parent.
    /// A pause result means no native callback, and therefore no RequestNetherContinueAsync,
    /// may be invoked by the caller.
    /// </summary>
    public bool CanStartNativeContinueParent(NetherCheckpointReturnPreflightDecision decision) =>
        decision != null
        && decision.Kind is NetherCheckpointReturnPreflightKind.NoReturn or NetherCheckpointReturnPreflightKind.Ready;

    /// <summary>
    /// Return confirmation is deliberately narrower than parent authorization: the no-return
    /// branch has no popup to confirm, while Ready must first be revalidated against the fresh
    /// native ContentModel list.
    /// </summary>
    public bool CanConfirmReturnPopup(NetherCheckpointReturnPreflightDecision decision) =>
        decision != null && decision.Kind == NetherCheckpointReturnPreflightKind.Ready;

    public NetherCheckpointReturnPreflightDecision Evaluate(
        int lockReward,
        IReadOnlyList<NetherCheckpointReturnPreflightItem> acquiredItems,
        IReadOnlySet<long> preserveItemIds
    )
    {
        if (preserveItemIds == null)
            throw new ArgumentNullException(nameof(preserveItemIds));
        if (lockReward < 0)
            return Pause(NetherPauseReason.InvalidConfiguration, "negative-lock-reward");

        // No lock means native HandleGameClearedIfNeededAsync takes the no-return branch.  The
        // candidate list is irrelevant and must not make that safe path fail merely because a
        // future popup would need a master row.
        if (lockReward == 0)
        {
            return new NetherCheckpointReturnPreflightDecision
            {
                Kind = NetherCheckpointReturnPreflightKind.NoReturn,
                SelectionLimit = 0,
            };
        }

        if (acquiredItems == null)
            return Pause(NetherPauseReason.UnknownMasterData, "missing-acquired-item-list");
        if (acquiredItems.Count < lockReward)
        {
            return Pause(
                NetherPauseReason.UnknownMasterData,
                "lock-reward-exceeds-whole-acquired-entry-count:" + lockReward + ":" + acquiredItems.Count
            );
        }

        foreach (NetherCheckpointReturnPreflightItem item in acquiredItems)
        {
            if (item == null)
                return Pause(NetherPauseReason.UnknownMasterData, "null-acquired-item");
            if (item.ItemId <= 0 || item.Amount <= 0 || item.ContentType < 0 || item.MasterRarity < 0)
            {
                return Pause(
                    NetherPauseReason.UnknownMasterData,
                    "invalid-acquired-item:" + item.ItemId + ":" + item.Amount
                );
            }
            if (!item.HasMasterData || !item.HasContentData || !item.HasRarityData)
            {
                return Pause(
                    NetherPauseReason.UnknownMasterData,
                    "incomplete-acquired-item-master:" + item.ItemId
                );
            }
        }

        NetherCheckpointReturnPreflightItem[] selection = acquiredItems
            .OrderByDescending(item => preserveItemIds.Contains(item.ItemId))
            .ThenByDescending(item => item.ContentType == EquipmentContentType)
            .ThenByDescending(item => item.MasterRarity)
            .ThenBy(item => item.ItemId)
            .ThenBy(item => item.Amount)
            .Take(lockReward)
            .ToArray();

        return new NetherCheckpointReturnPreflightDecision
        {
            Kind = NetherCheckpointReturnPreflightKind.Ready,
            SelectionLimit = lockReward,
            ExpectedPristineHash = CreatePristineHash(acquiredItems),
            WholeEntrySelection = selection,
        };
    }

    /// <summary>
    /// Re-evaluates the exact native popup list before invoking its OnConfirmAsync chain.  A
    /// difference in the server-owned LockReward, any whole entry, or the configured ranking
    /// contract is ambiguous and must never fall through to a confirm callback.
    /// </summary>
    public NetherCheckpointReturnPreflightDecision VerifyFreshPopup(
        NetherCheckpointReturnPreflightDecision planned,
        int popupSelectionLimit,
        IReadOnlyList<NetherCheckpointReturnPreflightItem> freshItems,
        IReadOnlySet<long> preserveItemIds
    )
    {
        if (planned == null || planned.Kind != NetherCheckpointReturnPreflightKind.Ready)
            return Pause(NetherPauseReason.BindingUnavailable, "return-popup-preflight-not-ready");

        NetherCheckpointReturnPreflightDecision fresh = Evaluate(
            popupSelectionLimit,
            freshItems,
            preserveItemIds
        );
        if (fresh.Kind != NetherCheckpointReturnPreflightKind.Ready)
        {
            return Pause(
                fresh.PauseReason == NetherPauseReason.None ? NetherPauseReason.UnknownMasterData : fresh.PauseReason,
                "return-popup-fresh-preflight:" + fresh.Detail
            );
        }
        if (fresh.SelectionLimit != planned.SelectionLimit)
        {
            return Pause(
                NetherPauseReason.UnknownMasterData,
                "return-popup-selection-limit-mismatch:" + planned.SelectionLimit + ":" + fresh.SelectionLimit
            );
        }
        if (!string.Equals(fresh.ExpectedPristineHash, planned.ExpectedPristineHash, StringComparison.Ordinal))
            return Pause(NetherPauseReason.UnknownMasterData, "return-popup-pristine-hash-mismatch");
        if (!fresh.WholeEntrySelection.SequenceEqual(planned.WholeEntrySelection))
            return Pause(NetherPauseReason.UnknownMasterData, "return-popup-whole-entry-selection-mismatch");

        return fresh;
    }

    private static string CreatePristineHash(IEnumerable<NetherCheckpointReturnPreflightItem> items)
    {
        string canonical = string.Join(
            ";",
            items
                .OrderBy(item => item.ItemId)
                .ThenBy(item => item.Amount)
                .ThenBy(item => item.ContentType)
                .ThenBy(item => item.MasterRarity)
                .Select(item => string.Join(
                    ":",
                    item.ItemId,
                    item.Amount,
                    item.ContentType,
                    item.MasterRarity
                ))
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static NetherCheckpointReturnPreflightDecision Pause(NetherPauseReason reason, string detail) => new()
    {
        Kind = NetherCheckpointReturnPreflightKind.Pause,
        PauseReason = reason,
        Detail = detail,
    };
}
