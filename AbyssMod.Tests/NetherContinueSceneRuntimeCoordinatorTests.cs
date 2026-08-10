using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherContinueSceneRuntimeCoordinatorTests
{
    [Fact]
    public void Controller_seam_drains_continue_parent_through_teardown_rebind_one_get_and_stable()
    {
        var driver = new FakeDriver(new[]
        {
            NetherNativeActionResult.Completed("continue-parent-terminal"),
        }, AppliedSnapshot())
        {
            CurrentRuntimeGeneration = 7,
        };
        NetherAutoClimbStateMachine state = StableState();
        NetherPlannedAction action = ContinueAction();
        Assert.True(state.TryBegin(action, BeforeSnapshot()));
        var runtime = new NetherContinueSceneRuntimeCoordinator(state, driver);

        Assert.True(runtime.Begin(action, BeforeSnapshot(), ownerGeneration: 7));
        Assert.Equal(NetherContinueSceneStepKind.WaitForTeardown, runtime.Pump().Kind);
        Assert.Equal(NetherAutoClimbPhase.AwaitingContinueSceneHandoff, state.Phase);

        driver.FloorOwnerTerminated = true;
        Assert.Equal(NetherContinueSceneStepKind.WaitForRebind, runtime.Pump().Kind);
        driver.CurrentRuntimeGeneration = 8;
        driver.IsExpectedNetherTopScene = true;
        Assert.Equal(NetherContinueSceneStepKind.Reconcile, runtime.Pump().Kind);
        Assert.Equal(NetherContinueSceneStepKind.Reconcile, runtime.Pump().Kind);

        Assert.Equal(NetherContinueSceneStepKind.Complete, runtime.Pump().Kind);
        Assert.Equal(NetherAutoClimbPhase.Stable, state.Phase);
        Assert.Null(state.PendingAction);
        Assert.Equal(1, driver.GetOnlyBeginCalls);
        Assert.Equal(1, driver.GetOnlyPollCalls);
    }

    [Fact]
    public void Wrong_rebind_scene_is_a_named_terminal_pause_before_any_get()
    {
        var driver = new FakeDriver(new[]
        {
            NetherNativeActionResult.Completed("continue-parent-terminal"),
        }, AppliedSnapshot())
        {
            CurrentRuntimeGeneration = 7,
        };
        NetherAutoClimbStateMachine state = StableState();
        NetherPlannedAction action = ContinueAction();
        Assert.True(state.TryBegin(action, BeforeSnapshot()));
        var runtime = new NetherContinueSceneRuntimeCoordinator(state, driver);

        Assert.True(runtime.Begin(action, BeforeSnapshot(), ownerGeneration: 7));
        runtime.Pump();
        driver.FloorOwnerTerminated = true;
        runtime.Pump();
        driver.CurrentRuntimeGeneration = 8;
        driver.IsExpectedNetherTopScene = false;

        NetherContinueSceneStep terminal = runtime.Pump();

        Assert.Equal(NetherContinueSceneStepKind.Pause, terminal.Kind);
        Assert.Equal(NetherAutoClimbPhase.Paused, state.Phase);
        Assert.Equal(NetherPauseReason.ContinueRebindWrongScene, state.PauseReason);
        Assert.Equal(0, driver.GetOnlyBeginCalls);
    }

    [Fact]
    public void Missing_teardown_is_bounded_then_a_named_terminal_pause()
    {
        var driver = new FakeDriver(new[]
        {
            NetherNativeActionResult.Completed("continue-parent-terminal"),
        }, AppliedSnapshot())
        {
            CurrentRuntimeGeneration = 7,
        };
        NetherAutoClimbStateMachine state = StableState();
        NetherPlannedAction action = ContinueAction();
        Assert.True(state.TryBegin(action, BeforeSnapshot()));
        var runtime = new NetherContinueSceneRuntimeCoordinator(state, driver, maximumMissingTicks: 1);

        Assert.True(runtime.Begin(action, BeforeSnapshot(), ownerGeneration: 7));
        runtime.Pump();
        Assert.Equal(NetherContinueSceneStepKind.WaitForTeardown, runtime.Pump().Kind);

        NetherContinueSceneStep terminal = runtime.Pump();

        Assert.Equal(NetherContinueSceneStepKind.Pause, terminal.Kind);
        Assert.Equal(NetherAutoClimbPhase.Paused, state.Phase);
        Assert.Equal(NetherPauseReason.ContinueTeardownTimeout, state.PauseReason);
        Assert.Equal(0, driver.GetOnlyBeginCalls);
    }

    [Fact]
    public void Off_then_reenable_cannot_replace_continue_handoff_until_its_terminal_observation()
    {
        var driver = new FakeDriver(new[]
        {
            NetherNativeActionResult.Completed("continue-parent-terminal"),
        }, AppliedSnapshot())
        {
            CurrentRuntimeGeneration = 7,
        };
        NetherAutoClimbStateMachine state = StableState();
        NetherPlannedAction action = ContinueAction();
        Assert.True(state.TryBegin(action, BeforeSnapshot()));
        var runtime = new NetherContinueSceneRuntimeCoordinator(state, driver);

        Assert.True(runtime.Begin(action, BeforeSnapshot(), ownerGeneration: 7));
        runtime.Pump();
        state.Toggle(isInNether: true);
        state.Toggle(isInNether: true);

        Assert.False(state.IsEnabled);
        Assert.Equal(NetherAutoClimbPhase.AwaitingContinueSceneHandoff, state.Phase);
        Assert.Equal(NetherActionKind.Continue, state.PendingAction!.Value.Kind);
    }

    [Fact]
    public void Finish_action_never_begins_continue_handoff()
    {
        var driver = new FakeDriver(Array.Empty<NetherNativeActionResult>(), AppliedSnapshot())
        {
            CurrentRuntimeGeneration = 7,
        };
        NetherAutoClimbStateMachine state = StableState();
        NetherPlannedAction finish = new(NetherActionKind.FinishAtCheckpoint);
        Assert.True(state.TryBegin(finish, BeforeSnapshot()));
        var runtime = new NetherContinueSceneRuntimeCoordinator(state, driver);

        Assert.False(runtime.Begin(finish, BeforeSnapshot(), ownerGeneration: 7));
        Assert.False(runtime.IsActive);
        Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, state.Phase);
    }

    private static NetherAutoClimbStateMachine StableState()
    {
        var state = new NetherAutoClimbStateMachine();
        state.Toggle(isInNether: true);
        state.ObserveStable((BeforeSnapshot() with { Status = NetherSessionStatus.Play }).Fingerprint);
        return state;
    }

    private static NetherPlannedAction ContinueAction() => new(NetherActionKind.Continue)
    {
        TicketCount = 1,
        TicketCost = 1,
        ExpectedMapId = 3,
        ExpectedFloorId = 33,
        ExpectedSegmentFloorLevel = 11,
    };

    private static NetherSnapshot BeforeSnapshot() => Snapshot(
        NetherSessionStatus.Sleep, mapId: 2, floorId: 23, floorLevel: 10, ticketCount: 3, mapHash: "map-2");

    private static NetherSnapshot AppliedSnapshot() => Snapshot(
        NetherSessionStatus.Play, mapId: 3, floorId: 33, floorLevel: 11, ticketCount: 2, mapHash: "map-3");

    private static NetherSnapshot Snapshot(
        NetherSessionStatus status,
        long mapId,
        long floorId,
        int floorLevel,
        int ticketCount,
        string mapHash
    ) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = mapId,
        CurrentFloorId = floorId,
        FloorLevel = floorLevel,
        FloorIndex = 0,
        ErosionPoint = 20,
        TicketCount = ticketCount,
        TreasureKeyCount = 1,
        NetherGold = 100,
        CodeReloadCount = 2,
        LockReward = 1,
        CharacterHpHash = "1:1000:1",
        CodeHash = "30024:5:1",
        MapHash = mapHash,
    };

    private sealed class FakeDriver : INetherContinueSceneDriver
    {
        private readonly Queue<NetherNativeActionResult> _parent;
        private readonly NetherSnapshot _after;

        public FakeDriver(IEnumerable<NetherNativeActionResult> parent, NetherSnapshot after)
        {
            _parent = new Queue<NetherNativeActionResult>(parent);
            _after = after;
        }

        public bool FloorOwnerTerminated { get; set; }
        public long CurrentRuntimeGeneration { get; set; }
        public bool IsExpectedNetherTopScene { get; set; } = true;
        public int GetOnlyBeginCalls { get; private set; }
        public int GetOnlyPollCalls { get; private set; }

        public NetherNativeActionResult PollContinueParent() => _parent.Count > 0
            ? _parent.Dequeue()
            : NetherNativeActionResult.Started("continue-parent-pending");

        public NetherNativeActionResult BeginGetOnlyRefresh()
        {
            GetOnlyBeginCalls++;
            return NetherNativeActionResult.Started("native-nether-sync");
        }

        public NetherNativeActionResult PollGetOnlyRefresh()
        {
            GetOnlyPollCalls++;
            return NetherNativeActionResult.Completed("native-nether-sync-complete");
        }

        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot() =>
            NetherReadOnlySnapshotResult.Success(_after);
    }
}
