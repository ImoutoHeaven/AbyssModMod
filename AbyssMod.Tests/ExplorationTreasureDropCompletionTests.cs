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
}
