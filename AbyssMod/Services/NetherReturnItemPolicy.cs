#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal enum NetherReturnItemSelectionKind
{
    Select,
    Pause,
}

internal sealed record NetherReturnItemSelection
{
    public NetherReturnItemSelectionKind Kind { get; init; }
    public IReadOnlyList<NetherRewardItem> Items { get; init; } = Array.Empty<NetherRewardItem>();
    public IReadOnlyList<string> Audit { get; init; } = Array.Empty<string>();
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal sealed class NetherReturnItemPolicy
{
    public NetherReturnItemSelection Select(
        IReadOnlyList<NetherRewardItem> items,
        int lockReward,
        IReadOnlySet<long> preserveIds
    )
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));
        if (preserveIds == null)
            throw new ArgumentNullException(nameof(preserveIds));
        if (lockReward < 0)
            return Pause(NetherPauseReason.InvalidConfiguration, "negative-lock-reward");
        if (lockReward == 0)
            return new NetherReturnItemSelection { Kind = NetherReturnItemSelectionKind.Select };
        if (items.Any(item => !item.HasMasterData || !item.HasVerifiedDropRarity || item.ItemId <= 0 || item.Amount <= 0))
            return Pause(NetherPauseReason.UnknownMasterData, "missing-or-invalid-return-item-master");

        NetherRewardItem[] selected = items
            .OrderByDescending(item => preserveIds.Contains(item.ItemId))
            .ThenByDescending(item => item.ItemType == 91)
            .ThenByDescending(item => item.DropRarity)
            .ThenByDescending(item => item.MasterRarity)
            .ThenBy(item => item.ItemId)
            .Take(lockReward)
            .ToArray();
        string[] audit = selected
            .Select(item => $"select:{item.ItemId}:amount={item.Amount}")
            .ToArray();
        return new NetherReturnItemSelection
        {
            Kind = NetherReturnItemSelectionKind.Select,
            Items = selected,
            Audit = audit,
        };
    }

    private static NetherReturnItemSelection Pause(NetherPauseReason reason, string detail) => new()
    {
        Kind = NetherReturnItemSelectionKind.Pause,
        PauseReason = reason,
        Detail = detail,
    };
}
