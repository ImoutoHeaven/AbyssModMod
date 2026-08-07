using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherBattleSettlementCoordinatorTests
{
    [Fact]
    public void Clear_terminal_then_get_only_reconcile_with_exact_status_map_and_floor_settles()
    {
        NetherSnapshot before = BattleSnapshot();
        NetherSnapshot after = Snapshot(NetherSessionStatus.Play, mapId: 2, floorId: 10);
        var driver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.Completed("battle-clear-parent-terminal") },
            clearObserved: true,
            closeObserved: false,
            appliedSnapshot: after
        );
        var coordinator = new NetherBattleSettlementCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), before));
        Assert.Equal(NetherBattleSettlementStepKind.AwaitingSettlement, coordinator.Pump().Kind);
        Assert.Equal(NetherBattleSettlementStepKind.AwaitingSettlement, coordinator.Pump().Kind);

        NetherBattleSettlementStep settled = coordinator.Pump();

        Assert.Equal(NetherBattleSettlementStepKind.Settled, settled.Kind);
        Assert.Equal(NetherActionOutcome.Applied, settled.Outcome);
        Assert.Equal(1, driver.GetOnlyBeginCalls);
        Assert.Equal(1, driver.GetOnlyPollCalls);
        Assert.Equal(0, driver.StartOrMutationCalls);
    }

    [Fact]
    public void Unchanged_battle_snapshot_is_named_unsettled_not_applied()
    {
        NetherSnapshot before = BattleSnapshot();
        var driver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.Completed("battle-close-parent-terminal") },
            clearObserved: false,
            closeObserved: true,
            appliedSnapshot: before
        );
        var coordinator = new NetherBattleSettlementCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), before));
        coordinator.Pump();
        coordinator.Pump();

        NetherBattleSettlementStep result = coordinator.Pump();

        Assert.Equal(NetherBattleSettlementStepKind.Unchanged, result.Kind);
        Assert.Equal(NetherActionOutcome.NotApplied, result.Outcome);
    }

    [Fact]
    public void Wrong_settlement_map_or_floor_is_named_wrong_target_not_applied()
    {
        NetherSnapshot before = BattleSnapshot();
        NetherSnapshot wrong = Snapshot(NetherSessionStatus.Play, mapId: 3, floorId: 11);
        var driver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.Completed("battle-clear-parent-terminal") },
            clearObserved: true,
            closeObserved: false,
            appliedSnapshot: wrong
        );
        var coordinator = new NetherBattleSettlementCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), before));
        coordinator.Pump();
        coordinator.Pump();

        NetherBattleSettlementStep result = coordinator.Pump();

        Assert.Equal(NetherBattleSettlementStepKind.WrongTarget, result.Kind);
        Assert.Equal(NetherActionOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public void Fault_and_cancel_have_distinct_terminal_steps()
    {
        var faultDriver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.UnknownOutcome("native-result-faulted") },
            clearObserved: false,
            closeObserved: false,
            appliedSnapshot: BattleSnapshot()
        );
        var cancelDriver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.UnknownOutcome("native-result-canceled") },
            clearObserved: false,
            closeObserved: false,
            appliedSnapshot: BattleSnapshot()
        );
        var fault = new NetherBattleSettlementCoordinator(faultDriver, faultDriver);
        var cancel = new NetherBattleSettlementCoordinator(cancelDriver, cancelDriver);

        Assert.True(fault.Begin(Action(), BattleSnapshot()));
        Assert.True(cancel.Begin(Action(), BattleSnapshot()));

        Assert.Equal(NetherBattleSettlementStepKind.Faulted, fault.Pump().Kind);
        Assert.Equal(NetherBattleSettlementStepKind.Canceled, cancel.Pump().Kind);
    }

    [Fact]
    public void F11_busy_holds_battle_without_issuing_get_until_it_releases()
    {
        var driver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.Started("battle-running") },
            clearObserved: false,
            closeObserved: false,
            appliedSnapshot: BattleSnapshot()
        ) { IsF11Busy = true };
        var coordinator = new NetherBattleSettlementCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), BattleSnapshot()));
        Assert.Equal(NetherBattleSettlementStepKind.AwaitingF11, coordinator.Pump().Kind);
        Assert.Equal(0, driver.GetOnlyBeginCalls);

        driver.IsF11Busy = false;

        Assert.Equal(NetherBattleSettlementStepKind.AwaitingBattle, coordinator.Pump().Kind);
    }

    [Fact]
    public void Scene_loss_is_terminal_and_clears_the_runtime_settlement()
    {
        var driver = new FakeDriver(
            lifecycle: new[] { NetherNativeActionResult.Started("battle-running") },
            clearObserved: false,
            closeObserved: false,
            appliedSnapshot: BattleSnapshot()
        );
        var coordinator = new NetherBattleSettlementCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), BattleSnapshot()));
        NetherBattleSettlementStep result = coordinator.TerminateForSceneLoss();

        Assert.Equal(NetherBattleSettlementStepKind.SceneLost, result.Kind);
        Assert.False(coordinator.IsActive);
    }

    private static NetherPlannedAction Action() => new(NetherActionKind.BattleSettlement)
    {
        BattleSettlement = new NetherBattleSettlementContract(
            EntryMapId: 2,
            EntryFloorId: 10,
            EntryStatus: NetherSessionStatus.Battle,
            ExpectedMapId: 2,
            ExpectedFloorId: 10,
            ExpectedStatus: NetherSessionStatus.Play,
            ProjectionIdentity: "battle-2-10"
        ),
    };

    private static NetherSnapshot BattleSnapshot() => Snapshot(NetherSessionStatus.Battle, mapId: 2, floorId: 10);

    private static NetherSnapshot Snapshot(NetherSessionStatus status, long mapId, long floorId) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = mapId,
        CurrentFloorId = floorId,
        FloorLevel = 10,
        FloorIndex = 0,
        ErosionPoint = 20,
        TicketCount = 3,
        TreasureKeyCount = 1,
        NetherGold = 100,
        CodeReloadCount = 2,
        LockReward = 1,
        CharacterHpHash = "1:1000:1",
        CodeHash = "30024:5:1",
        MapHash = "map-a",
    };

    private sealed class FakeDriver : INetherBattleSettlementDriver, INetherReadOnlyReconcileDriver
    {
        private readonly Queue<NetherNativeActionResult> _lifecycle;
        private readonly NetherSnapshot _appliedSnapshot;
        private bool _clearObserved;
        private bool _closeObserved;

        public FakeDriver(
            IEnumerable<NetherNativeActionResult> lifecycle,
            bool clearObserved,
            bool closeObserved,
            NetherSnapshot appliedSnapshot
        )
        {
            _lifecycle = new Queue<NetherNativeActionResult>(lifecycle);
            _clearObserved = clearObserved;
            _closeObserved = closeObserved;
            _appliedSnapshot = appliedSnapshot;
        }

        public bool IsF11Busy { get; set; }
        public int GetOnlyBeginCalls { get; private set; }
        public int GetOnlyPollCalls { get; private set; }
        public int StartOrMutationCalls { get; private set; }

        public NetherNativeActionResult PollBattleLifecycle() => _lifecycle.Dequeue();

        public bool TryConsumeBattleClear()
        {
            bool observed = _clearObserved;
            _clearObserved = false;
            return observed;
        }

        public bool TryConsumeBattleClose()
        {
            bool observed = _closeObserved;
            _closeObserved = false;
            return observed;
        }

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

        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot() => NetherReadOnlySnapshotResult.Success(_appliedSnapshot);
    }
}
