#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Data-only representation of one native MNetherFloorShopContents row.  ContentId is the
/// shop-row ID sent back to OnPurchaseContentAsync; ItemId is the optional payload ID.  The
/// latter is legitimately zero for ID-less non-item products such as currencies/effects.
/// </summary>
internal readonly record struct NetherRawShopContent(
    long ContentId,
    int RawContentType,
    long ItemId,
    int Price,
    bool UsesNetherGold,
    int Amount
);

internal readonly record struct NetherShopItemMaster(
    long ItemId,
    int ItemType,
    NetherRewardRarity Rarity
);

internal readonly record struct NetherShopContentMapResult(
    IReadOnlyList<NetherShopContent> Contents,
    string Detail
)
{
    public bool IsSuccess => Detail.Length == 0;

    public static NetherShopContentMapResult Success(IReadOnlyList<NetherShopContent> contents) =>
        new(contents, string.Empty);

    public static NetherShopContentMapResult Failure(string detail) =>
        new(Array.Empty<NetherShopContent>(), detail);
}

/// <summary>
/// Converts the heterogeneous native shop catalogue into the narrow EquipmentBags policy
/// model.  Raw content types 30/31 are MItems-backed.  Other product families are fully valid
/// shop rows but are deliberately marked as known/ineligible so they cannot poison the whole
/// popup and cannot accidentally become a purchase candidate.
/// </summary>
internal static class NetherShopContentMapper
{
    private const int ItemContentType = 30;
    private const int LimitedItemContentType = 31;

    public static NetherShopContentMapResult Map(
        IReadOnlyList<NetherRawShopContent> rows,
        IReadOnlyDictionary<long, NetherShopItemMaster> itemById
    )
    {
        if (rows == null)
            throw new ArgumentNullException(nameof(rows));
        if (itemById == null)
            throw new ArgumentNullException(nameof(itemById));

        var mapped = new List<NetherShopContent>(rows.Count);
        var seenContentIds = new HashSet<long>();
        foreach (NetherRawShopContent row in rows)
        {
            if (row.ContentId <= 0 || row.Amount <= 0 || row.Price < 0)
                return NetherShopContentMapResult.Failure("invalid-shop-row:" + row.ContentId);
            if (!seenContentIds.Add(row.ContentId))
                return NetherShopContentMapResult.Failure("duplicate-shop-row:" + row.ContentId);

            bool isItem = row.RawContentType is ItemContentType or LimitedItemContentType;
            if (!isItem)
            {
                mapped.Add(new NetherShopContent(
                    row.ContentId,
                    row.ItemId,
                    0,
                    NetherRewardRarity.NoEffect,
                    row.Price,
                    row.UsesNetherGold,
                    row.Amount,
                    known: true
                ));
                continue;
            }

            if (row.ItemId <= 0)
                return NetherShopContentMapResult.Failure("invalid-shop-item-id:" + row.ContentId);
            if (!itemById.TryGetValue(row.ItemId, out NetherShopItemMaster item))
                return NetherShopContentMapResult.Failure("missing-shop-item-master:" + row.ItemId);

            mapped.Add(new NetherShopContent(
                row.ContentId,
                row.ItemId,
                item.ItemType,
                item.Rarity,
                row.Price,
                row.UsesNetherGold,
                row.Amount,
                known: true
            ));
        }

        return NetherShopContentMapResult.Success(mapped);
    }
}
