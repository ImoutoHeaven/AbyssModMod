#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherFloorSafetyEvaluatorTests
{
    [Theory]
    [InlineData(0, 40)]
    [InlineData(5, 45)]
    [InlineData(10, 50)]
    public void OptionalBattle_UsesAuthoritativeMaximumProjectionForZeroFiveAndTen(
        int floorMaximum,
        int expectedMaximum
    )
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 40,
            floorMinimum: 0,
            floorMaximum: floorMaximum,
            kind: NetherFloorSafetyKind.Optional,
            nodeType: NetherFloorNodeType.Battle
        );

        Assert.True(evaluation.IsSafe);
        Assert.Equal(40, evaluation.ProjectedMinimumErosion);
        Assert.Equal(expectedMaximum, evaluation.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.None, evaluation.PauseReason);
    }

    [Fact]
    public void KnownModifierDelta_IsIncludedInBothAuthoritativeBounds()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 40,
            floorMinimum: 2,
            floorMaximum: 5,
            knownModifierDelta: 3,
            kind: NetherFloorSafetyKind.Optional,
            nodeType: NetherFloorNodeType.Battle
        );

        Assert.True(evaluation.IsSafe);
        Assert.Equal(45, evaluation.ProjectedMinimumErosion);
        Assert.Equal(48, evaluation.ProjectedMaximumErosion);
    }

    [Fact]
    public void OptionalFloor_ProjectingEightyNineToNinety_IsRejectedAtStrictSoftLimit()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 89,
            floorMinimum: 1,
            floorMaximum: 1,
            kind: NetherFloorSafetyKind.Optional,
            nodeType: NetherFloorNodeType.Battle
        );

        Assert.False(evaluation.IsSafe);
        Assert.Equal(90, evaluation.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnsafeErosion, evaluation.PauseReason);
    }

    [Fact]
    public void NecessaryTerminal_ProjectingNinetyNineToOneHundred_IsRejectedAtHardLimit()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 99,
            floorMinimum: 1,
            floorMaximum: 1,
            kind: NetherFloorSafetyKind.NecessaryTerminal,
            nodeType: NetherFloorNodeType.Boss
        );

        Assert.False(evaluation.IsSafe);
        Assert.Equal(100, evaluation.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnsafeErosion, evaluation.PauseReason);
    }

    [Fact]
    public void NecessaryTerminal_BelowHardLimit_IsAllowedAboveSoftLimit()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 90,
            floorMinimum: 9,
            floorMaximum: 9,
            kind: NetherFloorSafetyKind.NecessaryTerminal,
            nodeType: NetherFloorNodeType.Boss
        );

        Assert.True(evaluation.IsSafe);
        Assert.Equal(99, evaluation.ProjectedMaximumErosion);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public void OptionalBattle_UsesConfiguredHpFloor(int hpPermille, bool expectedSafe)
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 20,
            floorMinimum: 0,
            floorMaximum: 0,
            kind: NetherFloorSafetyKind.Optional,
            nodeType: NetherFloorNodeType.Battle,
            currentHpPermille: new[] { hpPermille }
        );

        Assert.Equal(expectedSafe, evaluation.IsSafe);
        Assert.Equal(expectedSafe ? NetherPauseReason.None : NetherPauseReason.UnsafeHp, evaluation.PauseReason);
    }

    [Fact]
    public void UnknownAuthoritativeInput_NeverDefaultsToSafeOrZeroProjection()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 20,
            floorMinimum: 0,
            floorMaximum: 0,
            kind: NetherFloorSafetyKind.Optional,
            nodeType: NetherFloorNodeType.Battle,
            allInputsKnown: false
        );

        Assert.False(evaluation.IsSafe);
        Assert.Null(evaluation.ProjectedMinimumErosion);
        Assert.Null(evaluation.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnknownMasterData, evaluation.PauseReason);
    }

    [Fact]
    public void CheckedOverflow_NeverWrapsIntoASafeProjection()
    {
        NetherFloorSafetyEvaluation evaluation = Evaluate(
            currentErosion: 1,
            floorMinimum: 1,
            floorMaximum: int.MaxValue,
            kind: NetherFloorSafetyKind.NecessaryTerminal,
            nodeType: NetherFloorNodeType.Boss
        );

        Assert.False(evaluation.IsSafe);
        Assert.Null(evaluation.ProjectedMinimumErosion);
        Assert.Null(evaluation.ProjectedMaximumErosion);
        Assert.Equal(NetherPauseReason.UnknownEffect, evaluation.PauseReason);
    }

    private static NetherFloorSafetyEvaluation Evaluate(
        int currentErosion,
        int floorMinimum,
        int floorMaximum,
        NetherFloorSafetyKind kind,
        NetherFloorNodeType nodeType,
        int knownModifierDelta = 0,
        int[]? currentHpPermille = null,
        bool allInputsKnown = true
    ) => new NetherFloorSafetyEvaluator().Evaluate(new NetherFloorSafetyInput(
        CurrentErosion: currentErosion,
        FloorMinimumErosion: floorMinimum,
        FloorMaximumErosion: floorMaximum,
        KnownModifierDelta: knownModifierDelta,
        Kind: kind,
        NodeType: nodeType,
        CurrentHpPermille: currentHpPermille ?? new[] { 300 },
        MinimumHpPermille: 300,
        SoftErosionLimit: 90,
        HardErosionLimit: 100,
        AllInputsKnown: allInputsKnown
    ));
}
