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
    [InlineData(0, 40)]
    [InlineData(5, 45)]
    [InlineData(10, 50)]
    public void CombatRoute_UsesExactMasterBoundsForZeroFiveAndTen(int maximumErosion, int expectedProjectedMaximum)
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
        Assert.Equal(expectedProjectedMaximum, plan.BattleProjectionByFloorId[2].ProjectedMaximumErosion);
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
    }

    [Fact]
    public void NecessaryBoss_CanUseHardLimitWithoutRelaxingTheHundredHardStop()
    {
        NetherSnapshot snapshot = Snapshot(
            erosion: 95,
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
        Assert.Equal(96, plan.BattleProjectionByFloorId[2].ProjectedMaximumErosion);
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
        Assert.Equal(10, payload.FloorMaximumErosion);
        Assert.Equal(47, payload.ProjectedMinimumErosion);
        Assert.Equal(52, payload.ProjectedMaximumErosion);
        Assert.Equal("active:60001:6:2", payload.CodeHash);
        Assert.Equal("route-battle:2:1:40:5:10:active:60001:6:2", payload.ProjectionIdentity);
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
