#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherCodeTransformNativeFlowTests
{
    [Fact]
    public void Exact_transform_order_is_list_click_then_confirm_then_complete_then_task_terminal()
    {
        var port = new TransformPort();
        var runtime = new NetherOwnedPopupNativeStageRuntime(port, maximumPendingPumps: 3);
        port.ObserveTask = owner => runtime.ObserveCodeTransformTask(owner);
        NetherRuntimePopupContext popup = Popup();
        NetherPlannedAction action = new(NetherActionKind.TransformCode) { ReplaceCodeId = 40024 };

        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(Parent(), popup, action).Kind);
        Assert.Equal(new[] { "list:40024" }, port.Calls);

        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind);
        Assert.Equal(new[] { "list:40024", "confirm" }, port.Calls);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind);
        Assert.Equal(new[] { "list:40024", "confirm", "complete" }, port.Calls);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Completed, runtime.Pump().Kind);
        Assert.Equal(new[] { "list:40024", "confirm", "complete", "task" }, port.Calls);
    }

    [Fact]
    public void Missing_observer_stale_owner_timeout_or_repeat_never_replays_transform()
    {
        var port = new TransformPort { AutoObserveTask = false, ConfirmReady = false };
        var runtime = new NetherOwnedPopupNativeStageRuntime(port, maximumPendingPumps: 1);
        NetherRuntimePopupContext popup = Popup();
        NetherPlannedAction action = new(NetherActionKind.TransformCode) { ReplaceCodeId = 40024 };

        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(Parent(), popup, action).Kind);
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, runtime.Dispatch(Parent(), popup, action).Kind);
        Assert.Single(port.Calls);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Faulted, runtime.Pump().Kind);
        Assert.Single(port.Calls);

        runtime.Reset();
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            runtime.Dispatch(Parent(), popup with { Sequence = 99 }, action).Kind
        );
        Assert.Single(port.Calls);
    }

    private static NetherPlannedAction Parent() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 11,
        FloorLevel = 1,
        FloorIndex = 0,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
    };

    private static NetherRuntimePopupContext Popup() => new()
    {
        Kind = NetherRuntimePopupKind.CodeTransform,
        OwnerAction = NetherActionKind.SelectFloor,
        OwnerGeneration = 4,
        Sequence = 8,
    };

    private sealed class TransformPort : INetherOwnedPopupNativeStagePort
    {
        public List<string> Calls { get; } = new();
        public bool AutoObserveTask { get; init; } = true;
        public bool ConfirmReady { get; init; } = true;
        public Action<NetherCodeTransformOwner>? ObserveTask { get; set; }

        public bool IsCurrentOwnedPopup(NetherRuntimePopupKind kind, NetherOwnedPopupStageOwner owner) =>
            kind == NetherRuntimePopupKind.CodeTransform
            && owner.OwnerAction == NetherActionKind.SelectFloor
            && owner.Generation == 4
            && owner.Sequence == 8;

        public NetherNativeActionResult InvokeCodeTransform(NetherCodeTransformOwner owner)
        {
            Calls.Add("list:" + owner.ReplaceCodeId);
            if (AutoObserveTask)
                ObserveTask?.Invoke(owner);
            return NetherNativeActionResult.Started("transform-list-clicked");
        }

        public NetherNativeActionResult InvokeCodeTransformConfirm(NetherCodeTransformOwner owner)
        {
            if (!ConfirmReady)
                return NetherNativeActionResult.Started("await-transform-confirm");
            Calls.Add("confirm");
            return NetherNativeActionResult.Completed("transform-confirmed");
        }

        public NetherNativeActionResult InvokeCodeTransformCompleteClose(NetherCodeTransformOwner owner)
        {
            Calls.Add("complete");
            return NetherNativeActionResult.Completed("transform-complete-closed");
        }

        public NetherNativeActionResult PollCodeTransformTask(NetherCodeTransformOwner owner)
        {
            Calls.Add("task");
            return NetherNativeActionResult.Completed("transform-task-terminal");
        }

        public NetherNativeActionResult InvokeShopPurchase(NetherOwnedPopupStageOwner owner, NetherPlannedAction action) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherNativeActionResult PollShopPurchaseTask(NetherShopPurchaseCloseOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherNativeActionResult InvokeExactShopClose(NetherShopPurchaseCloseOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherOwnedPopupCodeReloadStart CaptureCodeReloadStart(NetherOwnedPopupStageOwner owner) =>
            NetherOwnedPopupCodeReloadStart.Failure("unused");
        public NetherNativeActionResult InvokeCodeReload(NetherCodeReloadEpochOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherNativeActionResult PollCodeReloadTask(NetherCodeReloadEpochOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherCodeReloadEpochRefresh CaptureFreshCodeReloadOffer(NetherCodeReloadEpochOwner owner) => default;
        public NetherNativeActionResult InvokeCodeKeepCancel(NetherCodeKeepCancelOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
        public NetherNativeActionResult PollCodeKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
            NetherNativeActionResult.BindingUnavailable("unused");
    }
}
