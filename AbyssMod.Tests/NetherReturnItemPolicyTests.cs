using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherReturnItemPolicyTests
{
    [Fact]
    public void Preserve_id_beats_equipment_rarity()
    {
        NetherReturnItemSelection selection = Select(
            [Item(10, type: 90, rarity: NetherRewardRarity.NoEffect), Item(20, type: 91, rarity: NetherRewardRarity.UniqueWeapon)],
            lockReward: 1,
            preserveIds: new HashSet<long> { 10 }
        );

        Assert.Equal(new long[] { 10L }, selection.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void Type_91_equipment_beats_non_equipment()
    {
        NetherReturnItemSelection selection = Select(
            [Item(10, type: 90, rarity: NetherRewardRarity.UniqueWeapon), Item(20, type: 91, rarity: NetherRewardRarity.Silver)],
            lockReward: 1
        );

        Assert.Equal(new long[] { 20L }, selection.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void Unique_red_gold_purple_silver_order_is_stable()
    {
        NetherReturnItemSelection selection = Select(
            [
                Item(1, rarity: NetherRewardRarity.Silver),
                Item(2, rarity: NetherRewardRarity.Purple),
                Item(3, rarity: NetherRewardRarity.Gold),
                Item(4, rarity: NetherRewardRarity.Red),
                Item(5, rarity: NetherRewardRarity.UniqueWeapon),
            ],
            lockReward: 5
        );

        Assert.Equal(new long[] { 5L, 4L, 3L, 2L, 1L }, selection.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void Master_rarity_then_item_id_breaks_ties()
    {
        NetherReturnItemSelection selection = Select(
            [Item(30, masterRarity: 1), Item(20, masterRarity: 5), Item(10, masterRarity: 5)],
            lockReward: 3
        );

        Assert.Equal(new long[] { 10L, 20L, 30L }, selection.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void Selection_never_exceeds_lock_reward()
    {
        NetherReturnItemSelection selection = Select([Item(1), Item(2), Item(3)], lockReward: 2);

        Assert.Equal(2, selection.Items.Count);
    }

    [Fact]
    public void Whole_amount_is_preserved_without_splitting_stack()
    {
        NetherReturnItemSelection selection = Select([Item(1, amount: 7)], lockReward: 1);

        NetherRewardItem item = Assert.Single(selection.Items);
        Assert.Equal(7, item.Amount);
    }

    [Fact]
    public void Missing_master_for_positive_lock_reward_pauses_before_continue()
    {
        NetherReturnItemSelection selection = Select([Item(1) with { HasMasterData = false }], lockReward: 1);

        Assert.Equal(NetherReturnItemSelectionKind.Pause, selection.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, selection.PauseReason);
    }

    [Fact]
    public void Unverified_drop_rarity_from_pre_continue_snapshot_cannot_drive_return_selection()
    {
        NetherReturnItemSelection selection = Select(
            [Item(1, rarity: NetherRewardRarity.NoEffect) with { HasVerifiedDropRarity = false }],
            lockReward: 1
        );

        Assert.Equal(NetherReturnItemSelectionKind.Pause, selection.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, selection.PauseReason);
    }

    private static NetherReturnItemSelection Select(
        IReadOnlyList<NetherRewardItem> items,
        int lockReward,
        IReadOnlySet<long>? preserveIds = null
    ) => new NetherReturnItemPolicy().Select(items, lockReward, preserveIds ?? new HashSet<long>());

    private static NetherRewardItem Item(
        long id,
        int amount = 1,
        int type = 90,
        NetherRewardRarity rarity = NetherRewardRarity.NoEffect,
        int masterRarity = 0
    ) => new(id, amount)
    {
        ItemType = type,
        DropRarity = rarity,
        MasterRarity = masterRarity,
    };
}
