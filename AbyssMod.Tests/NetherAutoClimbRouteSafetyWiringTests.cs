#nullable enable

using System;
using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherAutoClimbRouteSafetyWiringTests
{
    [Fact]
    public void ControllerRouteWiring_ForwardsCoordinatorContextAuditAndPreClickBattlePayload()
    {
        NetherAutoClimbRouteSafetyDecision decision = Decide(
            erosion: 40,
            bounds: Bounds((2, 5, 10), (3, 0, 0)),
            code: KnownCode("active:60001:6:2", new NetherCodeEffect(
                60001,
                NetherCodeEffectKind.ErosionAdditionUp,
                2
            ))
        );

        Assert.Equal(2, Assert.IsType<NetherFloorNode>(decision.Route.SelectedNode).FloorId);
        Assert.NotNull(decision.SelectedBattleProjection);
        Assert.NotNull(decision.SelectFloorAction);
        NetherPlannedAction action = decision.SelectFloorAction!.Value;
        Assert.Equal(NetherSessionStatus.Play, action.ExpectedBeforeStatus);
        Assert.Equal(NetherSessionStatus.Battle, action.ExpectedAfterStatus);
        Assert.Same(decision.SelectedBattleProjection, action.BattleProjection);
        Assert.Equal("route-battle:2:1:40:5:5:active:60001:6:2", decision.SelectedBattleProjection!.ProjectionIdentity);
        Assert.True(decision.Context.KnownNodeByFloorId[2]);
        Assert.Contains(decision.Route.Audit, item => item.FloorId == 2 && item.Reason == "selected");
    }

    [Fact]
    public void SoftLimitHardLimitHpAndUnknownInputs_CannotBypassProductionCoordinator()
    {
        NetherAutoClimbRouteSafetyDecision soft90 = Decide(
            erosion: 89,
            bounds: Bounds((2, 1, 1), (3, 0, 0))
        );
        NetherAutoClimbRouteSafetyDecision hp299 = Decide(
            hp: new NetherActivePartyHpSafety(true, 299, string.Empty)
        );
        NetherAutoClimbRouteSafetyDecision unknownMaster = Decide(
            bounds: Bounds((3, 0, 0))
        );
        NetherAutoClimbRouteSafetyDecision unknownCode = Decide(
            code: new NetherActiveCodeErosionProjection { ErosionProjectionKnown = false, Detail = "unknown" }
        );

        Assert.False(soft90.Route.HasSelection);
        Assert.False(hp299.Route.HasSelection);
        Assert.False(unknownMaster.Route.HasSelection);
        Assert.False(unknownCode.Route.HasSelection);
        Assert.Null(soft90.SelectedBattleProjection);
        Assert.Null(soft90.SelectFloorAction);
    }

    [Fact]
    public void NecessaryBossUsesHardLimitButNinetyFiveToOneHundredRemainsRejected()
    {
        NetherAutoClimbRouteSafetyDecision allowedBoss = DecideBoss(94, Bounds((2, 0, 100)));
        NetherAutoClimbRouteSafetyDecision rejectedBoss = DecideBoss(95, Bounds((2, 0, 100)));

        Assert.Equal(2, Assert.IsType<NetherFloorNode>(allowedBoss.Route.SelectedNode).FloorId);
        Assert.NotNull(allowedBoss.SelectedBattleProjection);
        Assert.Equal(99, allowedBoss.SelectedBattleProjection!.ProjectedMaximumErosion);
        Assert.False(rejectedBoss.Route.HasSelection);
        Assert.Null(rejectedBoss.SelectedBattleProjection);
    }

    private static NetherAutoClimbRouteSafetyDecision Decide(
        int erosion = 40,
        NetherActivePartyHpSafety? hp = null,
        IReadOnlyDictionary<long, NetherFloorMasterBounds>? bounds = null,
        NetherActiveCodeErosionProjection? code = null
    ) => new NetherAutoClimbRouteSafetyWiring().Plan(
        new NetherSnapshot
        {
            Status = NetherSessionStatus.Play,
            MapId = 1,
            CurrentFloorId = 1,
            ErosionPoint = erosion,
            Floors = new[]
            {
                Floor(1, 1, NetherFloorNodeType.Recovery),
                Floor(2, 2, NetherFloorNodeType.Battle, previous: new[] { 1L }),
                Floor(3, 3, NetherFloorNodeType.Boss, previous: new[] { 2L }),
            },
        },
        settings: Settings(),
        effectiveMaximumDepth: 130,
        runtime: Runtime(hp, bounds, code)
    );

    private static NetherAutoClimbRouteSafetyDecision DecideBoss(
        int erosion,
        IReadOnlyDictionary<long, NetherFloorMasterBounds> bounds
    ) => new NetherAutoClimbRouteSafetyWiring().Plan(
        new NetherSnapshot
        {
            Status = NetherSessionStatus.Play,
            MapId = 1,
            CurrentFloorId = 1,
            ErosionPoint = erosion,
            Floors = new[]
            {
                Floor(1, 1, NetherFloorNodeType.Recovery),
                Floor(2, 2, NetherFloorNodeType.Boss, previous: new[] { 1L }),
            },
        },
        settings: Settings(),
        effectiveMaximumDepth: 130,
        runtime: Runtime(bounds: bounds)
    );

    private static NetherRuntimeRouteSafetyData Runtime(
        NetherActivePartyHpSafety? hp = null,
        IReadOnlyDictionary<long, NetherFloorMasterBounds>? bounds = null,
        NetherActiveCodeErosionProjection? code = null
    ) => new()
    {
        FloorBoundsByFloorId = bounds ?? Bounds((2, 0, 0), (3, 0, 0)),
        ActivePartyHp = hp ?? new NetherActivePartyHpSafety(true, 500, string.Empty),
        ActiveCodeErosion = code ?? KnownCode("nether-codes:none"),
    };

    private static NetherActiveCodeErosionProjection KnownCode(
        string hash,
        params NetherCodeEffect[] effects
    ) => new()
    {
        ErosionProjectionKnown = true,
        CodeHash = hash,
        ErosionEffects = effects,
    };

    private static Dictionary<long, NetherFloorMasterBounds> Bounds(params (long Id, int Min, int Max)[] rows)
    {
        var bounds = new Dictionary<long, NetherFloorMasterBounds>();
        foreach ((long id, int min, int max) in rows)
            bounds.Add(id, new NetherFloorMasterBounds(id, min, max, IsKnown: true, Detail: string.Empty));
        return bounds;
    }

    private static NetherAutoClimbSettings Settings() => new()
    {
        SoftErosionLimit = 90,
        MinimumCharacterHpPermille = 300,
    };

    private static NetherFloorNode Floor(
        long id,
        int level,
        NetherFloorNodeType type,
        long[]? previous = null
    ) => new(id, level, (int)id, type)
    {
        IsUnlocked = true,
        PreviousFloorIds = previous ?? Array.Empty<long>(),
    };
}
