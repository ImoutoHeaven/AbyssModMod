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
            masterItems,
            NetherSlTarget.Gold
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
            masterItems,
            NetherSlTarget.Gold
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
            masterItems,
            NetherSlTarget.Gold
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            NetherSlTarget.Gold,
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
            masterItems,
            NetherSlTarget.Gold
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
            NetherSlTarget.UniqueWeapon,
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
            masterItems,
            NetherSlTarget.Gold
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Single(evaluation.Targets);
    }

    [Theory]
    [InlineData(NetherSlTarget.Gold, 3, 3, true)]
    [InlineData(NetherSlTarget.Gold, 4, 4, true)]
    [InlineData(NetherSlTarget.Gold, 5, 4, true)]
    [InlineData(NetherSlTarget.Gold, 2, 2, false)]
    [InlineData(NetherSlTarget.Red, 4, 4, true)]
    [InlineData(NetherSlTarget.Red, 5, 4, true)]
    [InlineData(NetherSlTarget.Red, 3, 3, false)]
    [InlineData(NetherSlTarget.UniqueWeapon, 5, 4, true)]
    [InlineData(NetherSlTarget.UniqueWeapon, 4, 4, false)]
    [InlineData(NetherSlTarget.Silver, 0, 1, true)]
    [InlineData(NetherSlTarget.Purple, 0, 2, true)]
    [InlineData(NetherSlTarget.Gold, 0, 2, false)]
    public void Policy_applies_minimum_targets_and_effective_master_rarity(
        NetherSlTarget target,
        int rawRarity,
        int masterRarity,
        bool expectedMatch
    )
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(rawRarity));
        var masterItems = new Dictionary<long, NetherItemMasterInfo> { [210021] = new(91, masterRarity) };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, target, equipmentOnly: true
        );

        Assert.Equal(expectedMatch, evaluation.StopConditionMatched);
        Assert.Equal(string.Empty, evaluation.Error);
        if (expectedMatch)
            Assert.Equal(rawRarity == 0 ? masterRarity : rawRarity, Assert.Single(evaluation.Targets).EffectiveRarity);
    }

    [Fact]
    public void Policy_does_not_promote_raw_zero_from_master_when_equipment_only_is_false()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(0));
        var masterItems = new Dictionary<long, NetherItemMasterInfo> { [210021] = new(91, 2) };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, NetherSlTarget.Purple, equipmentOnly: false
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Policy_fails_open_for_gold_or_red_master_rarity_mismatch(int rawRarity, int masterRarity)
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(rawRarity));
        var masterItems = new Dictionary<long, NetherItemMasterInfo> { [210021] = new(91, masterRarity) };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, NetherSlTarget.Gold, equipmentOnly: true
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.StartsWith("rarity-mismatch:", evaluation.Error);
    }

    [Fact]
    public void Policy_rejects_off_target_as_a_runtime_bypass_error()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(5));

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, new Dictionary<long, NetherItemMasterInfo> { [210021] = new(91, 4) }, NetherSlTarget.Off
        );

        Assert.Equal("invalid-nether-sl-target:-1", evaluation.Error);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(6, true)]
    [InlineData(-1, false)]
    [InlineData(6, false)]
    public void Policy_fails_open_for_out_of_domain_equipment_rarity(int rawRarity, bool equipmentOnly)
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(rawRarity));
        IReadOnlyDictionary<long, NetherItemMasterInfo> masterItems = equipmentOnly
            ? new Dictionary<long, NetherItemMasterInfo> { [210021] = new(91, 4) }
            : new Dictionary<long, NetherItemMasterInfo>();

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, NetherSlTarget.UniqueWeapon, equipmentOnly
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal($"invalid-nether-rarity:210021:{rawRarity}", evaluation.Error);
    }

    [Fact]
    public void Policy_preserve_branch_remains_rarity_agnostic_for_out_of_domain_raw_value()
    {
        const string preserveDrop = """
            { "enemies": [{ "sid": 1, "drops": [11] }], "drops": [
              { "sid": 11, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 6, "is_rare_drop": 0 }
            ] }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(preserveDrop);
        var masterItems = new Dictionary<long, NetherItemMasterInfo> { [200003] = new(90, 0) };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, NetherSlTarget.Gold, equipmentOnly: false,
            preserveMode: NetherPreserveMode.OR, preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.True(evaluation.StopConditionMatched);
        Assert.Equal(string.Empty, evaluation.Error);
    }

    [Fact]
    public void Policy_fails_open_for_invalid_preserve_mode_when_rules_are_configured()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(3));
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
            [200003] = new(90, 0),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report, masterItems, NetherSlTarget.Gold, preserveMode: (NetherPreserveMode)99,
            preservedItemIds: new HashSet<long> { 200003 }
        );

        Assert.Equal("unsupported-preserve-mode:99", evaluation.Error);
        Assert.False(evaluation.ShouldRetry);
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
            NetherSlTarget.Gold,
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
            new Dictionary<long, NetherItemMasterInfo>(),
            NetherSlTarget.Gold
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
            mismatch,
            NetherSlTarget.Gold
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
            masterItems,
            NetherSlTarget.Gold
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("unresolved-nether-item-master:210021", evaluation.Error);
    }

    [Fact]
    public void Policy_retries_when_gold_equipment_is_not_a_selected_weapon_type()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(3));
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3, NetherWeaponType.Gun),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            NetherSlTarget.Gold,
            weaponTypes: NetherWeaponTypeFilter.Staff
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Targets);
        Assert.Equal(string.Empty, evaluation.Error);
    }

    [Fact]
    public void Policy_any_weapon_type_filter_preserves_existing_matching()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(3));
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            NetherSlTarget.Gold,
            weaponTypes: NetherWeaponTypeFilter.Any
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal(string.Empty, evaluation.Error);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void Policy_accepts_a_weapon_type_from_multi_selection()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(3));
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3, NetherWeaponType.Staff),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            NetherSlTarget.Gold,
            weaponTypes: NetherWeaponTypeFilter.Gun | NetherWeaponTypeFilter.Staff
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal(string.Empty, evaluation.Error);
        Assert.Single(evaluation.Targets);
    }

    [Fact]
    public void Policy_fails_open_when_selected_weapon_type_cannot_be_resolved()
    {
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(OneEnemyDrop(3));
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [210021] = new(91, 3),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            NetherSlTarget.Gold,
            weaponTypes: NetherWeaponTypeFilter.Staff
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal("unresolved-nether-equipment-type:210021", evaluation.Error);
    }

    [Fact]
    public void Policy_preserve_branch_bypasses_weapon_type_filter()
    {
        const string preserveDrop = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 31, "content_id": 200003, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
              ]
            }
            """;
        NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(preserveDrop);
        var masterItems = new Dictionary<long, NetherItemMasterInfo>
        {
            [200003] = new(90, 0),
        };

        NetherBattleDropEvaluation evaluation = NetherBattleAutoSLPolicy.Evaluate(
            report,
            masterItems,
            NetherSlTarget.Gold,
            preserveMode: NetherPreserveMode.OR,
            preservedItemIds: new HashSet<long> { 200003 },
            weaponTypes: NetherWeaponTypeFilter.Staff
        );

        Assert.True(evaluation.StopConditionMatched);
        Assert.Equal(string.Empty, evaluation.Error);
        Assert.Equal(NetherTargetReason.PreservedNetherItemId, Assert.Single(evaluation.Targets).Reason);
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

    [Fact]
    public void Bypass_trace_input_preserves_root_drops_without_policy_evaluation()
    {
        NetherBypassTraceInput trace = NetherBypassTraceInput.FromStageDetail(StageDetail);

        Assert.Equal(string.Empty, trace.Error);
        Assert.Equal(3, trace.RootDrops.Count);
        Assert.Contains(trace.RootDrops, drop => drop.Sid == 3000000001);
    }

    [Fact]
    public void Bypass_trace_input_tolerates_malformed_stage_detail_without_creating_a_retry_gate()
    {
        NetherBypassTraceInput trace = NetherBypassTraceInput.FromStageDetail("{ invalid");

        Assert.NotEmpty(trace.Error);
        Assert.Empty(trace.RootDrops);
    }

    private static string OneEnemyDrop(int rarity) => $$"""
        {
          "enemies": [{ "sid": 1, "drops": [11] }],
          "drops": [
            { "sid": 11, "content_type": 31, "content_id": 210021, "amount": 1, "rarity_level": {{rarity}}, "is_rare_drop": 0 }
          ]
        }
        """;
}
