using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class ExplorationTreasureDropCompletionTests
{
    private const string SelectedTreasureStage = """
        {
          "floor_parts": [
            {
              "sid": 100,
              "role": 304,
              "role_option": { "seamless_battle": { "fork_group_no": 1 } },
              "fork_group_no": 0,
              "enemy_groups": [],
              "resources": []
            },
            {
              "sid": 101,
              "role": 201,
              "role_option": null,
              "fork_group_no": 1,
              "enemy_groups": [],
              "resources": [500]
            },
            {
              "sid": 102,
              "role": 301,
              "role_option": null,
              "fork_group_no": 0,
              "enemy_groups": [{ "enemies": [700] }],
              "resources": []
            }
          ],
          "enemies": [{ "sid": 700, "drops": [12] }],
          "resources": [{ "sid": 500, "asset_id": "BoxGold", "drops": [10, 11] }],
          "drops": [
            { "sid": 10, "content_type": 80, "content_id": 23010440, "amount": 1, "rarity_level": 4, "is_rare_drop": 1 },
            { "sid": 11, "content_type": 30, "content_id": 130001, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 },
            { "sid": 12, "content_type": 80, "content_id": 22010410, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
          ]
        }
        """;

    private const string TreasureRankStage = """
        {
          "floor_parts": [
            {
              "sid": 200,
              "role": 303,
              "role_option": {
                "treasure_battle": {
                  "ranks": [
                    { "rank": 1, "asset_id": "BoxBronze", "time_limit": 0, "drops": [20] },
                    { "rank": 2, "asset_id": "BoxSilver", "time_limit": 180, "drops": [21, 22] },
                    { "rank": 3, "asset_id": "BoxGold", "time_limit": 120, "drops": [23, 24] }
                  ]
                }
              },
              "fork_group_no": 0,
              "enemy_groups": [],
              "resources": []
            }
          ],
          "enemies": [],
          "resources": [],
          "drops": [
            { "sid": 20, "content_type": 30, "content_id": 20, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 },
            { "sid": 21, "content_type": 80, "content_id": 21010440, "amount": 1, "rarity_level": 4, "is_rare_drop": 1 },
            { "sid": 22, "content_type": 30, "content_id": 22, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 },
            { "sid": 23, "content_type": 80, "content_id": 23010440, "amount": 1, "rarity_level": 4, "is_rare_drop": 1 },
            { "sid": 24, "content_type": 30, "content_id": 24, "amount": 1, "rarity_level": 0, "is_rare_drop": 0 }
          ]
        }
        """;

    [Fact]
    public void Selected_treasure_fork_keeps_its_drops_and_builds_target_chest_plan()
    {
        BattleDropProbeReport report = BattleSessionDropProbe.Parse(SelectedTreasureStage);
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            SelectedTreasureStage
        );

        Assert.Equal([10, 11, 12], report.Items.Select(item => item.Sid));
        ExplorationTreasureChest chest = Assert.Single(
            analysis.FindActiveTargetChests([10])
        );
        Assert.Equal(101, chest.FloorSid);
        Assert.Equal(500, chest.ResourceSid);
        Assert.Equal("BoxGold", chest.AssetId);
        Assert.Equal([10, 11], chest.DropSids);
        Assert.Empty(analysis.FindActiveTargetChests([12]));
    }

    [Fact]
    public void Passed_target_chest_appends_every_missing_drop_exactly_once()
    {
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            SelectedTreasureStage
        );
        IReadOnlyList<ExplorationTreasureChest> plan =
            analysis.FindActiveTargetChests([10]);

        ExplorationTreasureDropCompletion completion =
            ExplorationTreasureDropCompleter.Complete(
                existingDropSids: [12, 10],
                passedFloorSids: [100, 101, 102],
                targetChests: plan
            );

        Assert.Equal([12, 10, 11], completion.DropSids);
        Assert.Equal([11], completion.AddedDropSids);
        Assert.Equal([500], completion.CompletedResourceSids);
    }

    [Fact]
    public void Unpassed_target_chest_does_not_modify_the_clear_payload()
    {
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            SelectedTreasureStage
        );
        IReadOnlyList<ExplorationTreasureChest> plan =
            analysis.FindActiveTargetChests([10]);

        ExplorationTreasureDropCompletion completion =
            ExplorationTreasureDropCompleter.Complete(
                existingDropSids: [12],
                passedFloorSids: [100, 102],
                targetChests: plan
            );

        Assert.Equal([12], completion.DropSids);
        Assert.Empty(completion.AddedDropSids);
        Assert.Empty(completion.CompletedResourceSids);
    }

    [Fact]
    public void Target_rank_plan_selects_one_rank_and_keeps_its_complete_server_drop_set()
    {
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            TreasureRankStage
        );

        ExplorationTreasureRankReward reward = Assert.Single(
            analysis.FindActiveTargetRankRewards([21, 23])
        );

        Assert.Equal(200, reward.FloorSid);
        Assert.Equal(3, reward.Rank);
        Assert.Equal("BoxGold", reward.AssetId);
        Assert.Equal(120, reward.TimeLimit);
        Assert.Equal([23, 24], reward.DropSids);
    }

    [Fact]
    public void Passed_target_rank_appends_the_rank_complete_drop_set_exactly_once()
    {
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            TreasureRankStage
        );
        IReadOnlyList<ExplorationTreasureRankReward> rankPlan =
            analysis.FindActiveTargetRankRewards([23]);

        ExplorationTreasureDropCompletion completion =
            ExplorationTreasureDropCompleter.Complete(
                existingDropSids: [23],
                passedFloorSids: [200],
                targetChests: [],
                targetRankRewards: rankPlan
            );

        Assert.Equal([23, 24], completion.DropSids);
        Assert.Equal([24], completion.AddedDropSids);
        Assert.Equal(3, Assert.Single(completion.CompletedRankRewards).Rank);
    }

    [Fact]
    public void Unpassed_target_rank_does_not_modify_the_clear_payload()
    {
        ExplorationStageDropAnalysis analysis = ExplorationStageDropReachability.Parse(
            TreasureRankStage
        );

        ExplorationTreasureDropCompletion completion =
            ExplorationTreasureDropCompleter.Complete(
                existingDropSids: [],
                passedFloorSids: [],
                targetChests: [],
                targetRankRewards: analysis.FindActiveTargetRankRewards([23])
            );

        Assert.Empty(completion.DropSids);
        Assert.Empty(completion.AddedDropSids);
        Assert.Empty(completion.CompletedRankRewards);
    }
}
