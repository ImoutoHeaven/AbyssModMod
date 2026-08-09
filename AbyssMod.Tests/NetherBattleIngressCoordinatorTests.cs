using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherBattleIngressCoordinatorTests
{
    [Fact]
    public void Pending_exact_start_task_never_begins_authority_get()
    {
        var driver = new Driver(
            start: new[]
            {
                NetherNativeActionResult.Started("await-start-registration"),
                NetherNativeActionResult.Started("start-task-pending"),
            },
            getBegin: NetherNativeActionResult.Started("get"),
            getPoll: Array.Empty<NetherNativeActionResult>(),
            after: Snapshot(NetherSessionStatus.Battle, floorId: 2)
        );
        var coordinator = new NetherBattleIngressCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), Snapshot(NetherSessionStatus.Play, floorId: 46)));
        Assert.Equal(NetherBattleIngressStepKind.AwaitingStart, coordinator.Pump().Kind);
        Assert.Equal(NetherBattleIngressStepKind.AwaitingStart, coordinator.Pump().Kind);
        Assert.Equal(0, driver.GetBeginCalls);
        Assert.Equal(0, driver.GetPollCalls);
    }

    [Fact]
    public void Successful_start_task_performs_one_get_and_accepts_exact_battle_target()
    {
        var driver = new Driver(
            start: new[] { NetherNativeActionResult.Completed("start-task-succeeded") },
            getBegin: NetherNativeActionResult.Started("get-begin"),
            getPoll: new[] { NetherNativeActionResult.Completed("get-complete") },
            after: Snapshot(NetherSessionStatus.Battle, floorId: 2)
        );
        var coordinator = new NetherBattleIngressCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), Snapshot(NetherSessionStatus.Play, floorId: 46)));
        Assert.Equal(NetherBattleIngressStepKind.Reconciling, coordinator.Pump().Kind);
        NetherBattleIngressStep terminal = coordinator.Pump();

        Assert.Equal(NetherBattleIngressStepKind.Entered, terminal.Kind);
        Assert.Equal(NetherSessionStatus.Battle, terminal.Snapshot!.Status);
        Assert.Equal(2, terminal.Snapshot.CurrentFloorId);
        Assert.Equal(1, driver.GetBeginCalls);
        Assert.Equal(1, driver.GetPollCalls);
        Assert.Equal(1, driver.StartPollCalls);
        Assert.Equal(NetherBattleIngressStepKind.BindingUnavailable, coordinator.Pump().Kind);
        Assert.Equal(1, driver.GetBeginCalls);
    }

    [Theory]
    [InlineData("native-start-canceled", 5)]
    [InlineData("native-start-faulted", 6)]
    public void Failed_start_task_is_terminal_and_never_begins_get(
        string detail,
        int expectedRaw
    )
    {
        var driver = new Driver(
            start: new[] { NetherNativeActionResult.UnknownOutcome(detail) },
            getBegin: NetherNativeActionResult.Started("must-not-run"),
            getPoll: Array.Empty<NetherNativeActionResult>(),
            after: Snapshot(NetherSessionStatus.Battle, floorId: 2)
        );
        var coordinator = new NetherBattleIngressCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), Snapshot(NetherSessionStatus.Play, floorId: 46)));
        Assert.Equal((NetherBattleIngressStepKind)expectedRaw, coordinator.Pump().Kind);
        Assert.Equal(0, driver.GetBeginCalls);
        Assert.Equal(NetherBattleIngressStepKind.BindingUnavailable, coordinator.Pump().Kind);
        Assert.Equal(1, driver.StartPollCalls);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    public void Wrong_authority_target_is_terminal_and_does_not_retry(
        int statusRaw,
        long floorId
    )
    {
        NetherSessionStatus status = (NetherSessionStatus)statusRaw;
        var driver = new Driver(
            start: new[] { NetherNativeActionResult.Completed("start-task-succeeded") },
            getBegin: NetherNativeActionResult.Started("get-begin"),
            getPoll: new[] { NetherNativeActionResult.Completed("get-complete") },
            after: Snapshot(status, floorId)
        );
        var coordinator = new NetherBattleIngressCoordinator(driver, driver);

        Assert.True(coordinator.Begin(Action(), Snapshot(NetherSessionStatus.Play, floorId: 46)));
        Assert.Equal(NetherBattleIngressStepKind.Reconciling, coordinator.Pump().Kind);
        Assert.Equal(NetherBattleIngressStepKind.WrongTarget, coordinator.Pump().Kind);
        Assert.Equal(1, driver.GetBeginCalls);
        Assert.Equal(1, driver.GetPollCalls);
        Assert.Equal(NetherBattleIngressStepKind.BindingUnavailable, coordinator.Pump().Kind);
    }

    private static NetherPlannedAction Action() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 2,
        FloorLevel = 1,
        FloorIndex = 0,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
        ExpectedAfterStatus = NetherSessionStatus.Battle,
        BattleProjection = new NetherBattleProjectionPayload(
            MapId: 1,
            FloorId: 2,
            PreBattleErosion: 0,
            FloorMinimumErosion: 5,
            FloorMaximumErosion: 5,
            ProjectedMinimumErosion: 5,
            ProjectedMaximumErosion: 5,
            CodeHash: "codes:none",
            ProjectionIdentity: "battle:1:2:0:5:5:codes:none"
        ),
    };

    private static NetherSnapshot Snapshot(NetherSessionStatus status, long floorId) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = 1,
        CurrentFloorId = floorId,
        CurrentNodeId = floorId,
        FloorLevel = status == NetherSessionStatus.Battle ? 1 : 0,
        FloorIndex = 0,
        ErosionPoint = 0,
        TicketCount = 12,
        CharacterHpHash = "1:1000:1",
        CodeHash = "codes:none",
        MapHash = status + ":" + floorId,
    };

    private sealed class Driver : INetherBattleIngressDriver, INetherReadOnlyReconcileDriver
    {
        private readonly Queue<NetherNativeActionResult> _start;
        private readonly Queue<NetherNativeActionResult> _getPoll;
        private readonly NetherNativeActionResult _getBegin;
        private readonly NetherSnapshot _after;

        public Driver(
            IEnumerable<NetherNativeActionResult> start,
            NetherNativeActionResult getBegin,
            IEnumerable<NetherNativeActionResult> getPoll,
            NetherSnapshot after
        )
        {
            _start = new Queue<NetherNativeActionResult>(start);
            _getBegin = getBegin;
            _getPoll = new Queue<NetherNativeActionResult>(getPoll);
            _after = after;
        }

        public int StartPollCalls { get; private set; }
        public int GetBeginCalls { get; private set; }
        public int GetPollCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public NetherNativeActionResult PollBattleStart()
        {
            StartPollCalls++;
            return _start.Dequeue();
        }

        public void CancelBattleStartObservation() => CancelCalls++;

        public NetherNativeActionResult BeginGetOnlyRefresh()
        {
            GetBeginCalls++;
            return _getBegin;
        }

        public NetherNativeActionResult PollGetOnlyRefresh()
        {
            GetPollCalls++;
            return _getPoll.Dequeue();
        }

        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot() =>
            NetherReadOnlySnapshotResult.Success(_after);
    }
}
