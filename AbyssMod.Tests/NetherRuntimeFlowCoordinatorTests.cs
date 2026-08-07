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

        NetherRuntimeParentPollResult result = coordinator.Poll(_ => new NetherNativeActionResult(NetherNativeActionResultKind.Started, "must-not-run"));

        Assert.Equal(NetherRuntimeParentPollKind.Pending, result.Kind);
        Assert.Equal(0, driver.DispatchCount);
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
