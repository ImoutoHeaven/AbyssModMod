using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherActionReconcilePolicyTests
{
    [Fact]
    public void Select_floor_requires_an_authoritative_floor_or_status_postcondition()
    {
        NetherSnapshot before = Snapshot(floorId: 10, floorLevel: 10);
        NetherSnapshot after = Snapshot(floorId: 11, floorLevel: 11);

        Assert.Equal(
            NetherActionOutcome.Applied,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 11 }, before, after)
        );
    }

    [Fact]
    public void Reload_code_requires_code_or_reload_resource_change_not_an_unrelated_map_change()
    {
        NetherSnapshot before = Snapshot(codeReload: 2, mapHash: "map-a");
        NetherSnapshot unrelated = Snapshot(codeReload: 2, mapHash: "map-b");
        NetherSnapshot applied = Snapshot(codeReload: 1, mapHash: "map-a");

        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.ReloadCode), before, unrelated)
        );
        Assert.Equal(
            NetherActionOutcome.Applied,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.ReloadCode), before, applied)
        );
    }

    [Fact]
    public void Unknown_outcome_with_no_action_specific_postcondition_stays_ambiguous_and_is_never_replayed()
    {
        NetherSnapshot snapshot = Snapshot();

        Assert.Equal(
            NetherActionOutcome.Ambiguous,
            NetherActionReconcilePolicy.Evaluate(new NetherPlannedAction(NetherActionKind.BuyShopItem) { ContentId = 7 }, snapshot, snapshot)
        );
    }

    private static NetherSnapshot Snapshot(
        long floorId = 10,
        int floorLevel = 10,
        int codeReload = 2,
        string mapHash = "map-a"
    ) => new()
    {
        Status = NetherSessionStatus.Play,
        NetherId = 1,
        MapId = 2,
        CurrentFloorId = floorId,
        FloorLevel = floorLevel,
        FloorIndex = 0,
        ErosionPoint = 20,
        TicketCount = 3,
        TreasureKeyCount = 1,
        NetherGold = 100,
        CodeReloadCount = codeReload,
        LockReward = 1,
        CharacterHpHash = "1:1000:1",
        CodeHash = "30024:5:1",
        MapHash = mapHash,
    };
}
