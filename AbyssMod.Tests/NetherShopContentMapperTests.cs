#nullable enable

using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherShopContentMapperTests
{
    [Fact]
    public void Idless_non_item_rows_are_known_but_ineligible_while_equipment_bags_keep_item_metadata()
    {
        NetherShopContentMapResult result = NetherShopContentMapper.Map(
            [
                new NetherRawShopContent(10, 160, 0, 30, true, 1),
                new NetherRawShopContent(11, 31, 210001, 100, true, 1),
            ],
            new Dictionary<long, NetherShopItemMaster>
            {
                [210001] = new NetherShopItemMaster(210001, 91, NetherRewardRarity.Gold),
            }
        );

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(2, result.Contents.Count);
        NetherShopContent nonItem = result.Contents[0];
        Assert.True(nonItem.Known);
        Assert.Equal(0, nonItem.ItemId);
        Assert.Equal(0, nonItem.ItemType);
        NetherShopContent equipmentBag = result.Contents[1];
        Assert.True(equipmentBag.Known);
        Assert.Equal(210001, equipmentBag.ItemId);
        Assert.Equal(91, equipmentBag.ItemType);
        Assert.Equal(NetherRewardRarity.Gold, equipmentBag.Rarity);
    }

    [Fact]
    public void Missing_item_master_remains_a_named_failure_instead_of_becoming_an_ignored_product()
    {
        NetherShopContentMapResult result = NetherShopContentMapper.Map(
            [new NetherRawShopContent(12, 31, 999999, 30, true, 1)],
            new Dictionary<long, NetherShopItemMaster>()
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("missing-shop-item-master:999999", result.Detail);
    }
}
