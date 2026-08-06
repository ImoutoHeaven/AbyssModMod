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
              "rarity_level": 2,
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
    public void Policy_rejects_type90_rarity3_items_as_equipment_targets()
    {
        const string lostSignal = """
            {
              "enemies": [{ "sid": 1, "drops": [11] }],
              "drops": [
                { "sid": 11, "content_type": 30, "content_id": 200001, "amount": 1, "rarity_level": 3, "is_rare_drop": 0 }
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
