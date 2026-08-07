#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherShopPurchaseCloseCoordinatorTests
{
    [Fact]
    public void Completed_buy_closes_its_exact_popup_once_before_parent_may_be_polled()
    {
        var coordinator = new NetherShopPurchaseCloseCoordinator();
        var owner = new NetherShopPurchaseCloseOwner(
            NetherActionKind.SelectFloor,
            Generation: 5,
            Sequence: 9,
            ContentId: 42,
            ContentAmount: 1,
            GoldCost: 7
        );
        int closeCalls = 0;

        Assert.True(coordinator.Begin(owner));
        Assert.Equal(NetherShopPurchaseCloseStage.AwaitingPurchaseTask, coordinator.Stage);
        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Started("buy-pending"),
                () => { closeCalls++; return NetherNativeActionResult.Started("close"); }
            ).Kind
        );
        Assert.Equal(0, closeCalls);

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Completed("buy-complete"),
                () => { closeCalls++; return NetherNativeActionResult.Started("close"); }
            ).Kind
        );
        Assert.Equal(NetherShopPurchaseCloseStage.ClosePending, coordinator.Stage);
        Assert.Equal(0, closeCalls);

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Completed("must-not-repoll-buy"),
                () => { closeCalls++; return NetherNativeActionResult.Started("close"); }
            ).Kind
        );
        Assert.Equal(NetherShopPurchaseCloseStage.AwaitingParent, coordinator.Stage);
        Assert.Equal(1, closeCalls);

        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(
                () => NetherNativeActionResult.Completed("must-not-repoll-buy"),
                () => { closeCalls++; return NetherNativeActionResult.Started("must-not-reclose"); }
            ).Kind
        );
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public void Purchase_or_close_fault_never_closes_or_retries_a_second_time()
    {
        var coordinator = new NetherShopPurchaseCloseCoordinator();
        var owner = new NetherShopPurchaseCloseOwner(
            NetherActionKind.SelectFloor,
            Generation: 5,
            Sequence: 9,
            ContentId: 42,
            ContentAmount: 1,
            GoldCost: 7
        );
        int closeCalls = 0;

        Assert.True(coordinator.Begin(owner));
        NetherNativeActionResult childFault = coordinator.Pump(
            () => NetherNativeActionResult.UnknownOutcome("buy-fault"),
            () => { closeCalls++; return NetherNativeActionResult.Started("close"); }
        );
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, childFault.Kind);
        Assert.Equal(NetherShopPurchaseCloseStage.Faulted, coordinator.Stage);
        Assert.Equal(0, closeCalls);

        coordinator.Reset();
        Assert.True(coordinator.Begin(owner));
        coordinator.Pump(
            () => NetherNativeActionResult.Completed("buy-complete"),
            () => { closeCalls++; return NetherNativeActionResult.Started("close"); }
        );
        NetherNativeActionResult closeFault = coordinator.Pump(
            () => NetherNativeActionResult.Completed("must-not-repoll-buy"),
            () => { closeCalls++; return NetherNativeActionResult.UnknownOutcome("close-fault"); }
        );
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, closeFault.Kind);
        Assert.Equal(NetherShopPurchaseCloseStage.Faulted, coordinator.Stage);
        Assert.Equal(1, closeCalls);
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => NetherNativeActionResult.Completed("must-not-repoll-buy"),
                () => { closeCalls++; return NetherNativeActionResult.Started("must-not-reclose"); }
            ).Kind
        );
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public void Pending_purchase_is_bounded_and_never_closes_or_retries_after_timeout()
    {
        var coordinator = new NetherShopPurchaseCloseCoordinator(maximumPendingPumps: 1);
        var owner = new NetherShopPurchaseCloseOwner(
            NetherActionKind.SelectFloor,
            Generation: 5,
            Sequence: 9,
            ContentId: 42,
            ContentAmount: 1,
            GoldCost: 7
        );
        int closeCalls = 0;

        Assert.True(coordinator.Begin(owner));
        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Started("buy-still-pending"),
                () => { closeCalls++; return NetherNativeActionResult.Started("must-not-close"); }
            ).Kind
        );
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => NetherNativeActionResult.Started("buy-timeout"),
                () => { closeCalls++; return NetherNativeActionResult.Started("must-not-close"); }
            ).Kind
        );
        Assert.Equal(NetherShopPurchaseCloseStage.Faulted, coordinator.Stage);
        Assert.Equal(0, closeCalls);
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-repoll-after-timeout"),
                () => { closeCalls++; return NetherNativeActionResult.Started("must-not-close"); }
            ).Kind
        );
        Assert.Equal(0, closeCalls);
    }
}
