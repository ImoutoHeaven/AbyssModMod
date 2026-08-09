#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRouteSafetyProductionCoordinatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void Map_generation_ranges_do_not_change_the_battle_base_cost(int maximumErosion)
    {
        NetherProductionRouteSafetyPlan plan = Plan(
            erosion: 40,
            bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = Bounds(2, 0, maximumErosion),
                [3] = Bounds(3, 0, 0),
            }
        );

        Assert.Equal(2, Assert.IsType<NetherFloorNode>(plan.Route.SelectedNode).FloorId);
        Assert.Equal(45, plan.BattleProjectionByFloorId[2].ProjectedMaximumErosion);
    }

    [Fact]
    public void Map_generation_erosion_range_does_not_replace_the_battle_base_cost()
    {
        NetherProductionRouteSafetyPlan plan = Plan(
            erosion: 0,
            bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = Bounds(2, 0, 100),
                [3] = Bounds(3, 0, 100),
            }
        );

        Assert.True(plan.Route.HasSelection, plan.Route.PauseReason + ":" + plan.Route.PauseDetail);
        Assert.Equal(2, Assert.IsType<NetherFloorNode>(plan.Route.SelectedNode).FloorId);
        NetherBattleProjectionPayload payload = plan.BattleProjectionByFloorId[2];
        Assert.Equal(5, payload.FloorMinimumErosion);
        Assert.Equal(5, payload.FloorMaximumErosion);
        Assert.Equal(5, payload.ProjectedMinimumErosion);
        Assert.Equal(5, payload.ProjectedMaximumErosion);
    }

    [Fact]
    public void OptionalBattle_ProjectingEightyNineToNinety_IsRejectedByProductionChain()
    {
        NetherProductionRouteSafetyPlan plan = Plan(
            erosion: 89,
            bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = Bounds(2, 1, 1),
                [3] = Bounds(3, 0, 0),
            }
        );

        Assert.False(plan.Route.HasSelection);
        Assert.DoesNotContain(plan.BattleProjectionByFloorId.Keys, id => id == 2);
    }

    [Fact]
    public void NecessaryBoss_ProjectingNinetyNineToOneHundred_IsRejectedByProductionChain()
    {
        NetherSnapshot snapshot = Snapshot(
            erosion: 99,
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Boss, 1)
        );
        NetherProductionRouteSafetyPlan plan = new NetherRouteSafetyProductionCoordinator().Plan(
            snapshot,
            130,
            Settings(),
            Runtime(
                hpPermille: 500,
                bounds: new Dictionary<long, NetherFloorMasterBounds> { [2] = Bounds(2, 1, 1) }
            )
        );

        Assert.False(plan.Route.HasSelection);
    }

    [Fact]
    public void ActivePartyMinimumOfTwoHundredNinetyNine_RejectsOptionalBattle()
    {
        NetherProductionRouteSafetyPlan plan = Plan(
            hpPermille: 299,
            bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = Bounds(2, 0, 0),
                [3] = Bounds(3, 0, 0),
            }
        );

        Assert.False(plan.Route.HasSelection);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public void NecessaryBoss_UsesHpBoundaryThroughTheProductionCoordinator(int hpPermille, bool expectedSelection)
    {
        NetherSnapshot snapshot = Snapshot(
            erosion: 20,
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Boss, 1)
        );
        NetherProductionRouteSafetyPlan plan = new NetherRouteSafetyProductionCoordinator().Plan(
            snapshot,
            130,
            Settings(),
            Runtime(
                hpPermille: hpPermille,
                bounds: new Dictionary<long, NetherFloorMasterBounds> { [2] = Bounds(2, 0, 1) }
            )
        );

        Assert.Equal(expectedSelection, plan.Route.HasSelection);
        if (expectedSelection)
            Assert.Equal(2, Assert.IsType<NetherFloorNode>(plan.Route.SelectedNode).FloorId);
    }

    [Fact]
    public void UnknownMasterCodeOrHp_IsNeverPromotedToTheOldPermissiveSafetyMaps()
    {
        NetherProductionRouteSafetyPlan missingMaster = Plan(
            bounds: new Dictionary<long, NetherFloorMasterBounds> { [3] = Bounds(3, 0, 0) }
        );
        NetherProductionRouteSafetyPlan unknownCode = Plan(
            code: new NetherActiveCodeErosionProjection { ErosionProjectionKnown = false, Detail = "unknown" }
        );
        NetherProductionRouteSafetyPlan unknownHp = Plan(
            hp: new NetherActivePartyHpSafety(false, null, "unknown")
        );

        Assert.False(missingMaster.Route.HasSelection);
        Assert.False(unknownCode.Route.HasSelection);
        Assert.False(unknownHp.Route.HasSelection);
        Assert.Contains("bounds:missing-runtime-node", UnknownCandidateDetail(missingMaster));
        Assert.Contains("codes:unknown", UnknownCandidateDetail(unknownCode));
        Assert.Contains("hp:unknown", UnknownCandidateDetail(unknownHp));
    }

    [Fact]
    public void NecessaryBoss_CanUseHeadroomBelowTheHundredHardStop()
    {
        NetherSnapshot snapshot = Snapshot(
            erosion: 94,
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Boss, 1)
        );
        NetherProductionRouteSafetyPlan plan = new NetherRouteSafetyProductionCoordinator().Plan(
            snapshot,
            130,
            Settings(),
            Runtime(
                hpPermille: 500,
                bounds: new Dictionary<long, NetherFloorMasterBounds> { [2] = Bounds(2, 1, 1) }
            )
        );

        Assert.Equal(2, Assert.IsType<NetherFloorNode>(plan.Route.SelectedNode).FloorId);
        Assert.Equal(99, plan.BattleProjectionByFloorId[2].ProjectedMaximumErosion);
    }

    [Fact]
    public void MaximumDepthGate_RemainsInTheProductionPlanningChain()
    {
        NetherSnapshot snapshot = Snapshot(
            erosion: 40,
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 3, NetherFloorNodeType.Battle, 1),
            Floor(3, 4, NetherFloorNodeType.Boss, 2, previous: new long[] { 2 })
        );
        NetherProductionRouteSafetyPlan plan = new NetherRouteSafetyProductionCoordinator().Plan(
            snapshot,
            2,
            Settings(),
            Runtime(
                hpPermille: 500,
                bounds: new Dictionary<long, NetherFloorMasterBounds>
                {
                    [2] = Bounds(2, 0, 0),
                    [3] = Bounds(3, 0, 0),
                }
            )
        );

        Assert.False(plan.Route.HasSelection);
        Assert.Equal(NetherPauseReason.TargetReachedOutsideCheckpoint, plan.Route.PauseReason);
    }

    [Fact]
    public void SelectedBattle_StoresBuilderDerivedProjectionPayloadBeforeNativeFloorAction()
    {
        NetherActiveCodeErosionProjection code = new()
        {
            ErosionProjectionKnown = true,
            CodeHash = "active:60001:6:2",
            ErosionEffects = new[]
            {
                new NetherCodeEffect(60001, NetherCodeEffectKind.ErosionAdditionUp, 2),
            },
        };
        NetherProductionRouteSafetyPlan plan = Plan(
            erosion: 40,
            bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = Bounds(2, 5, 10),
                [3] = Bounds(3, 0, 0),
            },
            code: code
        );

        NetherBattleProjectionPayload payload = plan.BattleProjectionByFloorId[2];
        Assert.Equal(2, payload.FloorId);
        Assert.Equal(40, payload.PreBattleErosion);
        Assert.Equal(5, payload.FloorMinimumErosion);
        Assert.Equal(5, payload.FloorMaximumErosion);
        Assert.Equal(47, payload.ProjectedMinimumErosion);
        Assert.Equal(47, payload.ProjectedMaximumErosion);
        Assert.Equal("active:60001:6:2", payload.CodeHash);
        Assert.Equal("route-battle:2:1:40:5:5:active:60001:6:2", payload.ProjectionIdentity);
    }

    [Fact]
    public void Production_safety_maps_are_keyed_by_runtime_node_when_master_id_is_reused()
    {
        NetherFloorNode current = Floor(3, 3, NetherFloorNodeType.Recovery, previous: Array.Empty<long>()) with { NodeId = 100 };
        NetherFloorNode next = Floor(3, 4, NetherFloorNodeType.Battle, previous: new long[] { 100 }) with { NodeId = 200 };
        NetherFloorNode terminal = Floor(9, 5, NetherFloorNodeType.Boss, previous: new long[] { 200 }) with { NodeId = 300 };
        NetherSnapshot snapshot = Snapshot(40, current, next, terminal) with
        {
            CurrentFloorId = 3,
            CurrentNodeId = 100,
        };

        NetherProductionRouteSafetyPlan plan = new NetherRouteSafetyProductionCoordinator().Plan(
            snapshot,
            130,
            Settings(),
            Runtime(bounds: new Dictionary<long, NetherFloorMasterBounds>
            {
                [200] = Bounds(3, 0, 0),
                [300] = Bounds(9, 0, 0),
            })
        );

        Assert.True(
            plan.Route.HasSelection,
            plan.Route.PauseReason + ":" + plan.Route.PauseDetail + ":"
                + string.Join("|", plan.Route.Audit.Select(item => item.FloorId + ":" + item.Reason))
        );
        NetherFloorNode selected = Assert.IsType<NetherFloorNode>(plan.Route.SelectedNode);
        Assert.Equal(3, selected.FloorId);
        Assert.Equal(200, selected.NodeId);
        Assert.True(plan.BattleProjectionByFloorId.ContainsKey(200));
        Assert.False(plan.BattleProjectionByFloorId.ContainsKey(3));
    }

    private static NetherProductionRouteSafetyPlan Plan(
        int erosion = 40,
        int hpPermille = 500,
        NetherActivePartyHpSafety? hp = null,
        IReadOnlyDictionary<long, NetherFloorMasterBounds>? bounds = null,
        NetherActiveCodeErosionProjection? code = null
    ) => new NetherRouteSafetyProductionCoordinator().Plan(
        Snapshot(
            erosion,
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Battle, 1),
            Floor(3, 3, NetherFloorNodeType.Boss, 2, previous: new long[] { 2 })
        ),
        130,
        Settings(),
        Runtime(hpPermille, hp, bounds, code)
    );

    private static string UnknownCandidateDetail(NetherProductionRouteSafetyPlan plan) =>
        Assert.Single(plan.Route.Audit.Where(audit => audit.Reason == "unknown-node")).Detail;

    private static NetherRuntimeRouteSafetyData Runtime(
        int hpPermille = 500,
        NetherActivePartyHpSafety? hp = null,
        IReadOnlyDictionary<long, NetherFloorMasterBounds>? bounds = null,
        NetherActiveCodeErosionProjection? code = null
    ) => new()
    {
        FloorBoundsByFloorId = bounds ?? new Dictionary<long, NetherFloorMasterBounds>
        {
            [2] = Bounds(2, 0, 0),
            [3] = Bounds(3, 0, 0),
        },
        ActivePartyHp = hp ?? new NetherActivePartyHpSafety(true, hpPermille, string.Empty),
        ActiveCodeErosion = code ?? new NetherActiveCodeErosionProjection
        {
            ErosionProjectionKnown = true,
            CodeHash = "nether-codes:none",
            ErosionEffects = Array.Empty<NetherCodeEffect>(),
        },
    };

    private static NetherFloorMasterBounds Bounds(long floorId, int min, int max) =>
        new(floorId, min, max, IsKnown: true, Detail: string.Empty);

    private static NetherAutoClimbSettings Settings() => new()
    {
        MaxDepth = 130,
        SoftErosionLimit = 90,
        MinimumCharacterHpPermille = 300,
    };

    private static NetherSnapshot Snapshot(int erosion, params NetherFloorNode[] floors) => new()
    {
        Status = NetherSessionStatus.Play,
        MapId = 1,
        CurrentFloorId = 1,
        ErosionPoint = erosion,
        Floors = floors,
    };

    private static NetherFloorNode Floor(
        long id,
        int level,
        NetherFloorNodeType type,
        int index = 0,
        long[]? previous = null
    ) => new(id, level, index, type)
    {
        IsUnlocked = true,
        PreviousFloorIds = previous ?? (id == 1 ? Array.Empty<long>() : new[] { 1L }),
    };
}
