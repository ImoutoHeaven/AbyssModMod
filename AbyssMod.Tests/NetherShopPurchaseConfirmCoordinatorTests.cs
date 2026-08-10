#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherShopPurchaseConfirmCoordinatorTests
{
    [Fact]
    public void Waits_for_owned_confirm_popup_then_invokes_it_exactly_once()
    {
        var coordinator = new NetherShopPurchaseConfirmCoordinator(maximumPendingPumps: 2);
        var owner = new NetherShopPurchaseCloseOwner(
            NetherActionKind.SelectFloor,
            Generation: 5,
            Sequence: 9,
            ContentId: 42,
            ContentAmount: 1,
            GoldCost: 30
        );
        int calls = 0;

        Assert.True(coordinator.Begin(owner));
        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(() =>
            {
                calls++;
                return NetherNativeActionResult.Started("shop-purchase-confirm-awaiting-popup");
            }).Kind
        );
        Assert.Equal(NetherShopPurchaseConfirmStage.AwaitingPopup, coordinator.Stage);
        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(() =>
            {
                calls++;
                return NetherNativeActionResult.Completed("shop-purchase-confirm-invoked");
            }).Kind
        );
        Assert.Equal(NetherShopPurchaseConfirmStage.Confirmed, coordinator.Stage);
        Assert.Equal(2, calls);

        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(() =>
            {
                calls++;
                return NetherNativeActionResult.Completed("must-not-reinvoke");
            }).Kind
        );
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Missing_popup_is_bounded_and_fault_is_sticky_without_confirm_replay()
    {
        var coordinator = new NetherShopPurchaseConfirmCoordinator(maximumPendingPumps: 1);
        var owner = new NetherShopPurchaseCloseOwner(
            NetherActionKind.SelectFloor,
            Generation: 5,
            Sequence: 9,
            ContentId: 42,
            ContentAmount: 1,
            GoldCost: 30
        );
        int calls = 0;

        Assert.True(coordinator.Begin(owner));
        Assert.Equal(NetherNativeActionResultKind.Started, coordinator.Pump(Awaiting).Kind);
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, coordinator.Pump(Awaiting).Kind);
        Assert.Equal(NetherShopPurchaseConfirmStage.Faulted, coordinator.Stage);
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(() => throw new Xunit.Sdk.XunitException("must not retry")).Kind
        );
        Assert.Equal(2, calls);

        NetherNativeActionResult Awaiting()
        {
            calls++;
            return NetherNativeActionResult.Started("shop-purchase-confirm-awaiting-popup");
        }
    }
}
