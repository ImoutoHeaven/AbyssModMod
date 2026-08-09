#nullable enable

using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRouteSafetyContextBuilderTests
{
    [Fact]
    public void LinearRoute_AccumulatesEachFloorWorstCaseThroughNecessaryTerminal()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Battle, maximum: 5, previous: new long[] { 1 }),
            Floor(3, 3, NetherFloorNodeType.Boss, maximum: 10, previous: new long[] { 2 }),
        ];

        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 3 });

        Assert.Equal(15, context.MinimumWorstCaseErosionToTerminal[2]);
        Assert.Equal(10, context.MinimumWorstCaseErosionToTerminal[3]);
        Assert.True(context.HardSafeByFloorId[2]);
    }

    [Fact]
    public void BranchWithHigherTerminalWorstCase_IsRejectedByProductionPlanner()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery, currentErosion: 80),
            Floor(2, 2, NetherFloorNodeType.Battle, currentErosion: 80, maximum: 5, previous: new long[] { 1 }),
            Floor(3, 2, NetherFloorNodeType.Battle, currentErosion: 80, maximum: 5, previous: new long[] { 1 }),
            Floor(4, 3, NetherFloorNodeType.Boss, currentErosion: 80, maximum: 15, previous: new long[] { 2 }),
            Floor(5, 3, NetherFloorNodeType.Boss, currentErosion: 80, maximum: 5, previous: new long[] { 3 }),
        ];
        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 4, 5 });

        NetherRoutePlan plan = new NetherRoutePlanner().Plan(Snapshot(1, 80, floors), context);

        Assert.Equal(20, context.MinimumWorstCaseErosionToTerminal[2]);
        Assert.Equal(10, context.MinimumWorstCaseErosionToTerminal[3]);
        Assert.Equal(3, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
    }

    [Fact]
    public void DeadEndAndCycleNodes_AreExplicitlyUnsafeInsteadOfUsingFallbackSafety()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Battle, previous: new long[] { 1, 3 }),
            Floor(3, 3, NetherFloorNodeType.Battle, previous: new long[] { 2 }),
            Floor(4, 2, NetherFloorNodeType.Recovery, previous: new long[] { 1 }),
            Floor(5, 3, NetherFloorNodeType.Boss, previous: new long[] { 4 }),
            Floor(6, 2, NetherFloorNodeType.Recovery, previous: new long[] { 1 }),
        ];

        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 5 });

        Assert.False(context.HardSafeByFloorId[2]);
        Assert.False(context.HardSafeByFloorId[3]);
        Assert.Equal(int.MaxValue, context.MinimumWorstCaseErosionToTerminal[2]);
        Assert.False(context.HardSafeByFloorId[6]);
        Assert.Equal(int.MaxValue, context.MinimumWorstCaseErosionToTerminal[6]);
        Assert.True(context.HardSafeByFloorId[4]);
    }

    [Fact]
    public void Known_dead_end_is_reported_as_no_terminal_path_instead_of_missing_context()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Battle, previous: new long[] { 1 }),
            Floor(3, 2, NetherFloorNodeType.Recovery, previous: new long[] { 1 }),
            Floor(4, 3, NetherFloorNodeType.Boss, previous: new long[] { 3 }),
        ];

        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 4 });

        Assert.True(context.IsKnown(2));
        Assert.False(context.IsHardSafe(2));
        Assert.Equal("known-no-terminal-path", context.DiagnosticDetail(2));
        Assert.Equal("missing-context-entry", context.DiagnosticDetail(999));
    }

    [Fact]
    public void MissingSafeExitKey_ProducesAllExplicitUnsafeDictionaryEntriesForThatCandidate()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Event, previous: new long[] { 1 }),
            Floor(3, 3, NetherFloorNodeType.Boss, previous: new long[] { 2 }),
        ];
        var exits = new Dictionary<long, bool> { [1] = true, [3] = true };

        NetherRouteSafetyContext context = Build(floors, new HashSet<long> { 3 }, exits);

        Assert.All(floors, floor =>
        {
            Assert.True(context.KnownNodeByFloorId.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.HardSafeByFloorId.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.HpSafeByFloorId.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.ProjectedErosionDeltaByFloorId.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.ProjectedHpDeltaByFloorId.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.MinimumWorstCaseErosionToTerminal.ContainsKey(floor.ServerNode.FloorId));
            Assert.True(context.SafeCodeOpportunityByFloorId.ContainsKey(floor.ServerNode.FloorId));
        });
        Assert.False(context.KnownNodeByFloorId[2]);
        Assert.False(context.HardSafeByFloorId[2]);
        Assert.False(context.HpSafeByFloorId[2]);
        Assert.Equal(int.MaxValue, context.ProjectedErosionDeltaByFloorId[2]);
        Assert.Equal(int.MinValue, context.ProjectedHpDeltaByFloorId[2]);
        Assert.Equal(int.MinValue, context.SafeCodeOpportunityByFloorId[2]);
    }

    [Fact]
    public void UnsafePopupExit_IsNotOfferedWhenSafeAlternativeExists()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Event, previous: new long[] { 1 }),
            Floor(3, 2, NetherFloorNodeType.Recovery, previous: new long[] { 1 }),
            Floor(4, 3, NetherFloorNodeType.Boss, previous: new long[] { 2, 3 }),
        ];
        var exits = SafeExits(floors);
        exits[2] = false;
        NetherRouteSafetyContext context = Build(floors, new HashSet<long> { 4 }, exits);

        NetherRoutePlan plan = new NetherRoutePlanner().Plan(Snapshot(1, 40, floors), context);

        Assert.False(context.HardSafeByFloorId[2]);
        Assert.Equal(3, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
    }

    [Fact]
    public void MaximumDepth_IsRetainedForProductionPlannerGate()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 3, NetherFloorNodeType.Recovery, previous: new long[] { 1 }),
            Floor(3, 4, NetherFloorNodeType.Boss, previous: new long[] { 2 }),
        ];
        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 3 }, maximumDepth: 2);

        NetherRoutePlan plan = new NetherRoutePlanner().Plan(Snapshot(1, 40, floors), context);

        Assert.Equal(2, context.MaximumFloorLevel);
        Assert.Equal(NetherPauseReason.TargetReachedOutsideCheckpoint, plan.PauseReason);
    }

    [Fact]
    public void UnknownEvaluatorInput_IsUnsafeEvenWhenItsGraphPathReachesATerminal()
    {
        NetherRouteSafetyFloorInput[] floors =
        [
            Floor(1, 1, NetherFloorNodeType.Recovery),
            Floor(2, 2, NetherFloorNodeType.Battle, allInputsKnown: false, previous: new long[] { 1 }),
            Floor(3, 3, NetherFloorNodeType.Boss, previous: new long[] { 2 }),
        ];

        NetherRouteSafetyContext context = Build(floors, terminals: new HashSet<long> { 3 });

        Assert.False(context.KnownNodeByFloorId[2]);
        Assert.False(context.HardSafeByFloorId[2]);
        Assert.Equal(int.MaxValue, context.MinimumWorstCaseErosionToTerminal[2]);
    }

    private static NetherRouteSafetyContext Build(
        IReadOnlyList<NetherRouteSafetyFloorInput> floors,
        IReadOnlySet<long>? terminals = null,
        IReadOnlyDictionary<long, bool>? exits = null,
        int maximumDepth = 130
    ) => new NetherRouteSafetyContextBuilder().Build(new NetherRouteSafetyContextBuilderInput(
        Floors: floors,
        NecessaryTerminalFloorIds: terminals ?? floors
            .Where(floor => floor.ServerNode.NodeType == NetherFloorNodeType.Boss)
            .Select(floor => floor.ServerNode.FloorId)
            .ToHashSet(),
        SafeExitKnownByFloorId: exits ?? SafeExits(floors),
        MaximumFloorLevel: maximumDepth
    ));

    private static Dictionary<long, bool> SafeExits(IEnumerable<NetherRouteSafetyFloorInput> floors) =>
        floors.ToDictionary(floor => floor.ServerNode.FloorId, _ => true);

    private static NetherSnapshot Snapshot(
        long currentFloorId,
        int erosion,
        IEnumerable<NetherRouteSafetyFloorInput> floors
    ) => new()
    {
        Status = NetherSessionStatus.Play,
        CurrentFloorId = currentFloorId,
        ErosionPoint = erosion,
        Floors = floors.Select(floor => floor.ServerNode).ToArray(),
    };

    private static NetherRouteSafetyFloorInput Floor(
        long floorId,
        int floorLevel,
        NetherFloorNodeType nodeType,
        int currentErosion = 40,
        int minimum = 0,
        int maximum = 0,
        NetherFloorSafetyKind? kind = null,
        int? projectedHpDelta = 0,
        int? safeCodeOpportunity = 0,
        bool allInputsKnown = true,
        params long[] previous
    ) => new(
        ServerNode: new NetherFloorNode(floorId, floorLevel, (int)floorId, nodeType)
        {
            IsUnlocked = true,
            PreviousFloorIds = previous,
        },
        EvaluationInput: new NetherFloorSafetyInput(
            CurrentErosion: currentErosion,
            FloorMinimumErosion: minimum,
            FloorMaximumErosion: maximum,
            KnownModifierDelta: 0,
            Kind: kind ?? (nodeType == NetherFloorNodeType.Boss
                ? NetherFloorSafetyKind.NecessaryTerminal
                : NetherFloorSafetyKind.Optional),
            NodeType: nodeType,
            CurrentHpPermille: new[] { 500 },
            MinimumHpPermille: 300,
            SoftErosionLimit: 90,
            HardErosionLimit: 100,
            AllInputsKnown: allInputsKnown
        ),
        ProjectedHpDelta: projectedHpDelta,
        SafeCodeOpportunity: safeCodeOpportunity
    );
}
