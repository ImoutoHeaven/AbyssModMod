using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherBattleDropProbeTests
{
    private const string StageDetail = """
        {
          "enemies": [
            { "sid": 1, "drops": [3000000001, 12] }
          ],
          "resources": [
            { "sid": 2, "drops": [13] }
          ],
          "drops": [
            {
              "sid": 3000000001,
              "content_type": 31,
              "content_id": 210021,
              "amount": 1,
              "rarity_level": 3,
              "is_rare_drop": 0
            },
            {
              "sid": 12,
              "content_type": 31,
              "content_id": 210011,
              "amount": 1,
              "rarity_level": 0,
              "is_rare_drop": 0
            },
            {
              "sid": 13,
              "content_type": 31,
              "content_id": 210031,
              "amount": 1,
              "rarity_level": 4,
              "is_rare_drop": 1
            }
          ]
        }
        """;

    [Fact]
    public void Parse_joins_only_enemy_drop_sids_and_supports_int64_sids()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);

        Assert.Equal("", report.Error);
        Assert.Equal(3, report.DropCount);
        Assert.Equal(2, report.EnemyDropCount);
        Assert.Equal(3000000001, report.EnemyItems[0].Sid);
        Assert.Equal(210021, report.EnemyItems[0].ContentId);
        Assert.DoesNotContain(report.EnemyItems, item => item.Sid == 13);
    }

    [Fact]
    public void Policy_accepts_enemy_gold_equipment_even_when_is_rare_is_false()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [210011] = new(91, 2),
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("", evaluation.Error);
        NetherTargetDrop target = Assert.Single(evaluation.Targets);
        Assert.Equal(210021, target.Drop.ContentId);
        Assert.False(target.Drop.IsRare);
        Assert.Equal(NetherTargetReason.EquipmentStopCondition, target.Reason);
    }

    [Fact]
    public void Policy_ignores_gold_or_red_drops_not_linked_to_an_enemy()
    {
        const string resourceOnlyGold = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 210011, "amount": 1, "rarity_level": 2, "is_rare_drop": 0 },
                { "sid": 12, "content_type": 31, "content_id": 210031, "amount": 1, "rarity_level": 4, "is_rare_drop": 1 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(resourceOnlyGold);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210011] = new(91, 2),
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Policy_rejects_unpreserved_type90_items_as_equipment_targets()
    {
        const string lostSignal = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 200001, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(lostSignal);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200001] = new(90, 3),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Preserve_item_id_parser_accepts_supported_separators_and_deduplicates()
    {
        bool parsed = NetherPreserveItemIdParser.TryParse(
            "200003, 200004;200003\n200006",
            out HashSet<long> itemIds,
            out string error
        );

        Assert.True(parsed);
        Assert.Equal("", error);
        Assert.Equal(new long[] { 200003, 200004, 200006 }, itemIds.Order());
        Assert.Equal("200003,200004,200006", NetherPreserveItemIdParser.Format(itemIds));
    }

    [Theory]
    [InlineData("abc", "invalid-preserve-item-id:abc")]
    [InlineData("0", "invalid-preserve-item-id:0")]
    [InlineData("-200003", "invalid-preserve-item-id:-200003")]
    public void Preserve_item_id_parser_rejects_invalid_ids(string value, string expectedError)
    {
        bool parsed = NetherPreserveItemIdParser.TryParse(
            value,
            out HashSet<long> itemIds,
            out string error
        );

        Assert.False(parsed);
        Assert.Empty(itemIds);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Policy_or_accepts_configured_type90_item_without_drop_rarity_signals()
    {
        const string researchMaterial = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(researchMaterial);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preserveMode: NetherPreserveMode.OR,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("", evaluation.Error);
        NetherTargetDrop target = Assert.Single(evaluation.Targets);
        Assert.Equal(200003, target.Drop.ContentId);
        Assert.Equal(NetherTargetReason.PreservedNetherItemId, target.Reason);
        Assert.True(evaluation.StopConditionMatched);
        Assert.Equal(0, evaluation.EquipmentTargetCount);
        Assert.Equal(1, evaluation.PreservedItemTargetCount);
    }

    [Fact]
    public void Policy_and_retries_when_only_preserved_item_matches()
    {
        const string researchMaterial = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(researchMaterial);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preserveMode: NetherPreserveMode.AND,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.False(evaluation.StopConditionMatched);
        Assert.Equal(0, evaluation.EquipmentTargetCount);
        Assert.Equal(1, evaluation.PreservedItemTargetCount);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void Policy_and_retries_when_only_equipment_stop_condition_matches()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 4),
            [210021] = new(91, 3),
            [210011] = new(91, 2),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preserveMode: NetherPreserveMode.AND,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.False(evaluation.StopConditionMatched);
        Assert.Equal(1, evaluation.EquipmentTargetCount);
        Assert.Equal(0, evaluation.PreservedItemTargetCount);
        Assert.Equal(210021, Assert.Single(evaluation.Targets).Drop.ContentId);
    }

    [Fact]
    public void Policy_and_accepts_only_when_equipment_and_preserved_item_match_together()
    {
        const string equipmentAndResearchMaterial = """
            {
              "enemies": [{ "sid": 1, "drops": [11, 12] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 210021, "amount": 1, "rarity_level": 3, "is_rare_drop": 0 },
                { "sid": 12, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(
            equipmentAndResearchMaterial
        );
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 4),
            [210021] = new(91, 3),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preserveMode: NetherPreserveMode.AND,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.True(evaluation.StopConditionMatched);
        Assert.Equal(1, evaluation.EquipmentTargetCount);
        Assert.Equal(1, evaluation.PreservedItemTargetCount);
        Assert.Equal(2, evaluation.Targets.Count);
    }

    [Fact]
    public void Policy_disables_preserve_combination_when_item_id_set_is_empty()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [210011] = new(91, 2),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preserveMode: NetherPreserveMode.AND,
            preservedItemIds: new HashSet<long>()
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.True(evaluation.StopConditionMatched);
        Assert.Equal(1, evaluation.EquipmentTargetCount);
        Assert.Equal(0, evaluation.PreservedItemTargetCount);
    }

    [Fact]
    public void Policy_ignores_configured_type90_item_not_linked_to_an_enemy()
    {
        const string nonEnemyResearchMaterial = """
            {
              "enemies": [{ "sid": 1, "drops": [] }],
              "resources": [{ "sid": 2, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(
            nonEnemyResearchMaterial
        );
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Policy_fails_open_when_preserve_id_is_not_a_type90_master_item()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [210011] = new(91, 2),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true,
            preservedItemIds: new HashSet<long> { 210021 }
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("preserve-item-type-mismatch:210021:91", evaluation.Error);
    }

    [Fact]
    public void Policy_ignores_ordinary_bag_master_rarity_mismatch_before_target_match()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [210011] = new(91, 2),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("", evaluation.Error);
        Assert.Equal(210021, Assert.Single(evaluation.Targets).Drop.ContentId);
    }

    [Fact]
    public void Policy_accepts_unique_weapon_marker_without_nonexistent_master_rarity5()
    {
        const string uniqueEquipment = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 210031, "amount": 1, "rarity_level": 5, "is_rare_drop": 1 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(uniqueEquipment);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.UniqueWeapon,
            equipmentOnly: true
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("", evaluation.Error);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void Policy_accepts_red_enemy_equipment_as_better_than_gold()
    {
        const string redEquipment = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 210031, "amount": 1, "rarity_level": 4, "is_rare_drop": 1 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(redEquipment);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void Policy_can_require_is_rare_instead_of_gold_rarity()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [210011] = new(91, 2),
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold,
            equipmentOnly: true
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Fact]
    public void Policy_can_disable_the_nether_equipment_master_filter()
    {
        const string nonEquipmentGold = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 30, "content_id": 200001, "amount": 1, "rarity_level": 3, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(nonEquipmentGold);

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            new Dictionary<long, NetherItemMasterInfo>(),
            BattleSessionAutoSLStopMode.Rarity,
            BattleSessionDropRarity.Gold,
            equipmentOnly: false
        );

        Assert.False(evaluation.ShouldRetry);
        NetherTargetDrop target = Assert.Single(evaluation.Targets);
        Assert.False(target.HasMaster);
    }

    [Fact]
    public void Policy_fails_open_on_protocol_or_master_errors()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);

        NetherBattleDropEvaluation missingMaster = NetherBattleAutoSLPolicy.Evaluate(
            report,
            new Dictionary<long, NetherItemMasterInfo>()
        );
        Assert.False(missingMaster.ShouldRetry);
        Assert.Equal("missing-item-master", missingMaster.Error);

        var mismatch = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 4),
            [210011] = new(91, 2),
        };
        NetherBattleDropEvaluation mismatchedRarity = NetherBattleAutoSLPolicy.Evaluate(
            report,
            mismatch
        );
        Assert.False(mismatchedRarity.ShouldRetry);
        Assert.StartsWith("rarity-mismatch:", mismatchedRarity.Error);
    }

    [Fact]
    public void Policy_fails_open_when_a_nether_item_is_absent_from_master_data()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(StageDetail);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210011] = new(91, 2),
            [210031] = new(91, 4),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("unresolved-nether-item-master:210021", evaluation.Error);
    }

    [Fact]
    public void Parse_fails_open_when_enemy_references_an_unknown_drop_sid()
    {
        const string invalid = """
            {
              "enemies": [{ "sid": 1, "drops": [99] }],
              "drops": []
            }
            """;

        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(invalid);

        Assert.Equal("unresolved-enemy-drop-sid:99", report.Error);
    }
}
