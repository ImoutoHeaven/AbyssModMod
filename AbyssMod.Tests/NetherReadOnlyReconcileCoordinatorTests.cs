using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherReadOnlyReconcileCoordinatorTests
{
    [Fact]
    public void Not_played_uses_the_get_only_flow_and_never_a_start_or_mutation_path()
    {
        var driver = new FakeReadOnlyDriver(
            begin: NetherNativeActionResult.Started("native-nether-sync"),
            polls: new[] { NetherNativeActionResult.Completed("native-nether-sync-complete") },
            appliedSnapshot: Snapshot(NetherSessionStatus.NotPlayed)
        );
        var coordinator = new NetherReadOnlyReconcileCoordinator(driver);

        Assert.Equal(NetherReadOnlyReconcileStepKind.Pending, coordinator.Pump().Kind);
        NetherReadOnlyReconcileStep terminal = coordinator.Pump();

        Assert.Equal(NetherReadOnlyReconcileStepKind.Applied, terminal.Kind);
        Assert.Equal(NetherSessionStatus.NotPlayed, terminal.Snapshot!.Status);
        Assert.Equal(1, driver.GetOnlyBeginCalls);
        Assert.Equal(1, driver.GetOnlyPollCalls);
        Assert.Equal(0, driver.StartOrMutationCalls);
    }

    [Fact]
    public void Active_get_only_refresh_waits_for_native_terminal_before_exposing_the_applied_snapshot()
    {
        NetherSnapshot applied = Snapshot(NetherSessionStatus.Play, mapId: 8, floorId: 44);
        var driver = new FakeReadOnlyDriver(
            begin: NetherNativeActionResult.Started("native-nether-sync"),
            polls: new[]
            {
                NetherNativeActionResult.Started("awaiting-native-nether-sync"),
                NetherNativeActionResult.Completed("native-nether-sync-complete"),
            },
            appliedSnapshot: applied
        );
        var coordinator = new NetherReadOnlyReconcileCoordinator(driver);

        Assert.Equal(NetherReadOnlyReconcileStepKind.Pending, coordinator.Pump().Kind);
        Assert.Equal(NetherReadOnlyReconcileStepKind.Pending, coordinator.Pump().Kind);
        Assert.Equal(0, driver.AppliedSnapshotReads);

        NetherReadOnlyReconcileStep terminal = coordinator.Pump();

        Assert.Equal(NetherReadOnlyReconcileStepKind.Applied, terminal.Kind);
        Assert.Equal(8, terminal.Snapshot!.MapId);
        Assert.Equal(44, terminal.Snapshot.CurrentFloorId);
        Assert.Equal(1, driver.GetOnlyBeginCalls);
        Assert.Equal(2, driver.GetOnlyPollCalls);
        Assert.Equal(1, driver.AppliedSnapshotReads);
    }

    [Fact]
    public void Missing_live_store_is_binding_unavailable_without_a_native_request()
    {
        var driver = new FakeReadOnlyDriver(
            begin: NetherNativeActionResult.BindingUnavailable("missing-live-nether-data-store"),
            polls: Array.Empty<NetherNativeActionResult>(),
            appliedSnapshot: Snapshot(NetherSessionStatus.Play)
        );
        var coordinator = new NetherReadOnlyReconcileCoordinator(driver);

        NetherReadOnlyReconcileStep result = coordinator.Pump();

        Assert.Equal(NetherReadOnlyReconcileStepKind.BindingUnavailable, result.Kind);
        Assert.Equal(1, driver.GetOnlyBeginCalls);
        Assert.Equal(0, driver.GetOnlyPollCalls);
        Assert.Equal(0, driver.StartOrMutationCalls);
    }

    [Fact]
    public void Native_binding_is_the_public_store_sync_unitask_and_not_a_start_signature()
    {
        NetherNativeMethodDescriptor descriptor = NetherReadOnlyReconcileNativeBinding.SyncDescriptor;

        Assert.Equal("SyncNetherDataAsync", descriptor.Name);
        Assert.Equal("Cysharp.Threading.Tasks.UniTask", descriptor.ReturnTypeName);
        Assert.Equal(new[] { "Il2CppSystem.Threading.CancellationToken" }, descriptor.ParameterTypeNames);
    }

    private static NetherSnapshot Snapshot(NetherSessionStatus status, long mapId = 2, long floorId = 10) => new()
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

    private sealed class FakeReadOnlyDriver : INetherReadOnlyReconcileDriver
    {
        private readonly Queue<NetherNativeActionResult> _polls;
        private readonly NetherNativeActionResult _begin;
        private readonly NetherSnapshot _appliedSnapshot;

        public FakeReadOnlyDriver(
            NetherNativeActionResult begin,
            IEnumerable<NetherNativeActionResult> polls,
            NetherSnapshot appliedSnapshot
        )
        {
            _begin = begin;
            _polls = new Queue<NetherNativeActionResult>(polls);
            _appliedSnapshot = appliedSnapshot;
        }

        public int GetOnlyBeginCalls { get; private set; }
        public int GetOnlyPollCalls { get; private set; }
        public int AppliedSnapshotReads { get; private set; }
        public int StartOrMutationCalls { get; private set; }

        public NetherNativeActionResult BeginGetOnlyRefresh()
        {
            GetOnlyBeginCalls++;
            return _begin;
        }

        public NetherNativeActionResult PollGetOnlyRefresh()
        {
            GetOnlyPollCalls++;
            return _polls.Dequeue();
        }

        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot()
        {
            AppliedSnapshotReads++;
            return NetherReadOnlySnapshotResult.Success(_appliedSnapshot);
        }
    }
}
