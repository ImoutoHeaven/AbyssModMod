using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRuntimeFlowCoordinatorTests
{
    [Fact]
    public void Floor_parent_stays_pending_while_its_owned_event_modal_is_driven()
    {
        var driver = new FakeDriver();
        var coordinator = new NetherRuntimeFlowCoordinator(driver);
        var floor = new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 42, FloorLevel = 2, FloorIndex = 1 };

        Assert.True(coordinator.BeginFloorParent(floor));
        driver.Popup = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.Event,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = coordinator.Generation,
            Sequence = 7,
        };
        driver.ParentPoll = NetherNativeActionResult.Started("parent-pending");

        int dispatches = 0;
        NetherRuntimeParentPollResult first = coordinator.Poll(
            popup =>
            {
                dispatches++;
                return new NetherNativeActionResult(NetherNativeActionResultKind.Started, "event-dispatched");
            }
        );

        Assert.Equal(NetherRuntimeParentPollKind.Pending, first.Kind);
        Assert.Equal(1, dispatches);
        Assert.True(coordinator.HasPendingParent);

        driver.Popup = null;
        driver.ParentPoll = NetherNativeActionResult.Completed("parent-terminal");
        NetherRuntimeParentPollResult terminal = coordinator.Poll(_ => throw new Xunit.Sdk.XunitException("must not dispatch twice"));

        Assert.Equal(NetherRuntimeParentPollKind.Completed, terminal.Kind);
        Assert.False(coordinator.HasPendingParent);
    }

    [Fact]
    public void Stale_popup_from_prior_generation_is_never_dispatched()
    {
        var driver = new FakeDriver();
        var coordinator = new NetherRuntimeFlowCoordinator(driver);
        var first = new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 1, FloorLevel = 1 };
        var second = new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 2, FloorLevel = 2 };

        Assert.True(coordinator.BeginFloorParent(first));
        long staleGeneration = coordinator.Generation;
        coordinator.TerminateParent();
        Assert.True(coordinator.BeginFloorParent(second));

        driver.Popup = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.CodeOffer,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = staleGeneration,
            Sequence = 1,
        };
        driver.ParentPoll = NetherNativeActionResult.Started("parent-pending");

        int dispatches = 0;
        NetherRuntimeParentPollResult result = coordinator.Poll(_ =>
        {
            dispatches++;
            return new NetherNativeActionResult(NetherNativeActionResultKind.Started, "must-not-run");
        });

        Assert.Equal(NetherRuntimeParentPollKind.Pending, result.Kind);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void Parent_terminal_is_not_consumed_on_the_same_tick_as_owned_modal_dispatch()
    {
        var driver = new FakeDriver();
        var coordinator = new NetherRuntimeFlowCoordinator(driver);
        var floor = new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 4, FloorLevel = 4 };
        Assert.True(coordinator.BeginFloorParent(floor));
        driver.Popup = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.Treasure,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = coordinator.Generation,
            Sequence = 2,
        };
        driver.ParentPoll = NetherNativeActionResult.Completed("premature-parent-terminal");

        NetherRuntimeParentPollResult first = coordinator.Poll(_ => NetherNativeActionResult.Started("treasure-click"));

        Assert.Equal(NetherRuntimeParentPollKind.Pending, first.Kind);
        Assert.True(coordinator.HasPendingParent);

        driver.Popup = null;
        NetherRuntimeParentPollResult second = coordinator.Poll(_ => throw new Xunit.Sdk.XunitException("no second popup"));

        Assert.Equal(NetherRuntimeParentPollKind.Completed, second.Kind);
    }

    [Fact]
    public void Same_live_code_offer_is_redispatched_only_after_a_monotonic_reload_epoch()
    {
        var driver = new FakeDriver();
        var coordinator = new NetherRuntimeFlowCoordinator(driver);
        var floor = new NetherPlannedAction(NetherActionKind.SelectFloor) { FloorId = 4, FloorLevel = 4 };
        Assert.True(coordinator.BeginFloorParent(floor));
        driver.Popup = new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.CodeOffer,
            OwnerAction = NetherActionKind.SelectFloor,
            OwnerGeneration = coordinator.Generation,
            Sequence = 8,
            DecisionEpoch = 0,
        };

        int dispatches = 0;
        Assert.Equal(
            NetherRuntimeParentPollKind.Pending,
            coordinator.Poll(_ =>
            {
                dispatches++;
                return NetherNativeActionResult.Started("reload");
            }).Kind
        );

        // The popup instance/sequence deliberately remains live while RerollAsync rebuilds
        // its model.  A new decision epoch is the only allowed re-dispatch identity.
        driver.Popup = driver.Popup with { DecisionEpoch = 1 };
        Assert.Equal(
            NetherRuntimeParentPollKind.Pending,
            coordinator.Poll(_ =>
            {
                dispatches++;
                return NetherNativeActionResult.Started("select-after-reload");
            }).Kind
        );
        Assert.Equal(2, dispatches);

        Assert.Equal(
            NetherRuntimeParentPollKind.Pending,
            coordinator.Poll(_ => throw new Xunit.Sdk.XunitException("same epoch must not replay")) .Kind
        );
        Assert.Equal(2, dispatches);
    }

    private sealed class FakeDriver : INetherRuntimeParentDriver
    {
        public NetherRuntimePopupContext? Popup { get; set; }
        public NetherNativeActionResult ParentPoll { get; set; } = NetherNativeActionResult.Started("pending");
        public int DispatchCount { get; private set; }

        public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent) => Popup == null
            ? NetherRuntimePopupResult.Failure("no-popup")
            : NetherRuntimePopupResult.Success(Popup);

        public NetherNativeActionResult PollFloorParent() => ParentPoll;

        public void ObserveDispatch() => DispatchCount++;
    }
}
