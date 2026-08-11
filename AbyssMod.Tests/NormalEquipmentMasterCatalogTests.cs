using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NormalEquipmentMasterCatalogTests
{
    [Fact]
    public void Forest_cloak_family_accepts_rank_four_and_five_but_not_rank_three()
    {
        var index = new NormalEquipmentMasterIndex(
            [
                Armor(23010340, 3004, 3, 4, "森林披风"),
                Armor(23010440, 3004, 4, 4, "森林披风"),
                Armor(23010540, 3004, 5, 4, "森林披风"),
            ]
        );

        Assert.True(index.TryGet(80, 23010440, out NormalEquipmentMasterInfo anchor));
        Assert.Equal(
            [23010440L, 23010540L],
            index.FindFamilyAtOrAbove(anchor).Select(item => item.ContentId)
        );
        Assert.False(index.IsSameFamilyAtOrAbove(anchor, Armor(23010340, 3004, 3, 4)));
        Assert.True(index.IsSameFamilyAtOrAbove(anchor, Armor(23010540, 3004, 5, 4)));
    }

    [Fact]
    public void Same_group_and_rank_with_a_different_master_rarity_is_not_the_same_family()
    {
        NormalEquipmentMasterInfo anchor = Armor(21010430, 1001, 4, 3, "轻甲");
        var index = new NormalEquipmentMasterIndex(
            [
                anchor,
                Armor(21010410, 1001, 4, 1, "轻甲"),
                Armor(21010420, 1001, 4, 2, "轻甲"),
                Armor(21010530, 1001, 5, 3, "轻甲"),
            ]
        );

        Assert.Equal(
            [21010430L, 21010530L],
            index.FindFamilyAtOrAbove(anchor).Select(item => item.ContentId)
        );
    }

    [Fact]
    public void Content_type_is_part_of_family_identity_even_when_group_numbers_overlap()
    {
        NormalEquipmentMasterInfo anchor = Armor(23010440, 3004, 4, 4);
        var weapon = new NormalEquipmentMasterInfo(70, 13010440, 3004, 5, 4, "Weapon");
        var index = new NormalEquipmentMasterIndex([anchor, weapon]);

        Assert.False(index.IsSameFamilyAtOrAbove(anchor, weapon));
    }

    [Fact]
    public void Conflicting_duplicate_content_keys_are_rejected()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new NormalEquipmentMasterIndex(
                [
                    Armor(23010440, 3004, 4, 4),
                    Armor(23010440, 9999, 4, 4),
                ]
            )
        );

        Assert.Contains("duplicate-normal-equipment-master", error.Message);
    }

    [Theory]
    [InlineData(0, 4, 4)]
    [InlineData(3004, 0, 4)]
    [InlineData(3004, 4, 0)]
    public void Invalid_family_metadata_is_rejected(long groupNo, int rank, int rarity)
    {
        Assert.Throws<ArgumentException>(
            () => new NormalEquipmentMasterIndex([Armor(23010440, groupNo, rank, rarity)])
        );
    }

    private static NormalEquipmentMasterInfo Armor(
        long id,
        long groupNo,
        int rank,
        int rarity,
        string name = ""
    ) => new(80, id, groupNo, rank, rarity, name);
}
