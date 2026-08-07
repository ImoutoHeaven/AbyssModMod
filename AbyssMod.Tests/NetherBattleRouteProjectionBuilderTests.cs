#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherBattleRouteProjectionBuilderTests
{
    [Theory]
    [InlineData(0, 40)]
    [InlineData(5, 45)]
    [InlineData(10, 50)]
    public void BattleProjection_UsesAuthoritativeMasterMaximumForZeroFiveAndTen(
        int maximumErosion,
        int expectedMaximum
    )
    {
        NetherBattleRouteProjection projection = Build(
            floorMinimum: 0,
            floorMaximum: maximumErosion
        );

        Assert.True(projection.IsSafe);
        Assert.Equal(40, projection.ProjectedMinimumErosion);
        Assert.Equal(expectedMaximum, projection.ProjectedMaximumErosion);
        Assert.NotNull(projection.EvaluatorInput);
        Assert.Equal(NetherFloorSafetyKind.Optional, projection.EvaluatorInput!.Value.Kind);
    }

    [Fact]
    public void KnownErosionCodeEffects_AreAppliedByPolicyBeforeEvaluatorGate()
    {
        NetherBattleRouteProjection projection = Build(
            floorMinimum: 5,
            floorMaximum: 10,
            effects:
            [
                new NetherCodeEffect(60001, NetherCodeEffectKind.ErosionAdditionUp, 2),
                new NetherCodeEffect(80001, NetherCodeEffectKind.ErosionRateUp, 100),
            ]
        );

        Assert.True(projection.IsSafe);
        Assert.Equal(47, projection.ProjectedMinimumErosion);
        Assert.Equal(53, projection.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.None, projection.PauseReason);
    }

    [Fact]
    public void UnknownMasterOrCodeEffect_IsFailClosedWithoutEvaluatorInput()
    {
        NetherBattleRouteProjection unknownMaster = Build(floorMinimum: null, floorMaximum: null);
        NetherBattleRouteProjection unknownCode = Build(
            effects: [new NetherCodeEffect(70001, NetherCodeEffectKind.Unknown, 0) { IsKnown = false }]
        );

        Assert.False(unknownMaster.IsSafe);
        Assert.Null(unknownMaster.EvaluatorInput);
        Assert.Equal(NetherPauseReason.UnknownMasterData, unknownMaster.PauseReason);
        Assert.False(unknownCode.IsSafe);
        Assert.Null(unknownCode.EvaluatorInput);
        Assert.Equal(NetherPauseReason.UnknownEffect, unknownCode.PauseReason);
    }

    [Fact]
    public void OptionalBattle_HpBelowConfiguredFloorIsRejected()
    {
        NetherBattleRouteProjection projection = Build(activeHp: new[] { 299 });

        Assert.False(projection.IsSafe);
        Assert.Equal(NetherPauseReason.UnsafeHp, projection.PauseReason);
    }

    [Fact]
    public void OptionalBattle_ProjectingEightyNineToNinetyIsRejectedAtSoftLimit()
    {
        NetherBattleRouteProjection projection = Build(
            currentErosion: 89,
            floorMinimum: 1,
            floorMaximum: 1
        );

        Assert.False(projection.IsSafe);
        Assert.Equal(90, projection.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnsafeErosion, projection.PauseReason);
    }

    [Fact]
    public void NecessaryBoss_ProjectingNinetyNineToOneHundredIsRejectedAtHardLimit()
    {
        NetherBattleRouteProjection projection = Build(
            floorKind: NetherFloorNodeType.Boss,
            currentErosion: 99,
            floorMinimum: 1,
            floorMaximum: 1
        );

        Assert.False(projection.IsSafe);
        Assert.Equal(100, projection.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnsafeErosion, projection.PauseReason);
    }

    [Fact]
    public void ProjectionIdentity_BindsFloorBoundsAndExactCodeHash()
    {
        NetherBattleRouteProjection projection = Build(
            floorId: 88,
            floorMinimum: 5,
            floorMaximum: 10,
            codeHash: "codes:30024:6:2"
        );

        Assert.True(projection.IsSafe);
        Assert.Equal("route-battle:88:1:40:5:10:codes:30024:6:2", projection.ProjectionIdentity);
    }

    private static NetherBattleRouteProjection Build(
        long floorId = 20,
        NetherFloorNodeType floorKind = NetherFloorNodeType.Battle,
        int currentErosion = 40,
        int? floorMinimum = 0,
        int? floorMaximum = 0,
        int[]? activeHp = null,
        NetherCodeEffect[]? effects = null,
        string codeHash = "codes:none"
    ) => new NetherBattleRouteProjectionBuilder().Build(new NetherBattleRouteProjectionInput(
        FloorId: floorId,
        FloorKind: floorKind,
        MinimumErosionPoint: floorMinimum,
        MaximumErosionPoint: floorMaximum,
        CurrentErosion: currentErosion,
        ActiveHpPermille: activeHp ?? new[] { 500 },
        ActiveCodeEffects: effects ?? System.Array.Empty<NetherCodeEffect>(),
        CodeHash: codeHash,
        Settings: new NetherAutoClimbSettings
        {
            SoftErosionLimit = 90,
            MinimumCharacterHpPermille = 300,
        },
        HardErosionLimit: 100
    ));
}
