#nullable enable

using System;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// Truthful production-core characterization: child completion deliberately leaves both the
/// popup and original SelectFloor parent pending.  Only the same production runtime may issue
/// a Shop close, advance a reload epoch, or release the parent gate.
/// </summary>
public class NetherOwnedPopupNativeStageRuntimeTests
{
    [Fact]
    public void Shop_buy_runs_one_child_then_one_exact_close_before_parent_gate_is_released()
    {
        var port = new FakePort(NetherRuntimePopupKind.Shop);
        var runtime = new NetherOwnedPopupNativeStageRuntime(port, maximumPendingPumps: 2);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.Shop);
        NetherPlannedAction buy = new(NetherActionKind.BuyShopItem)
        {
            ContentId = 42,
            ContentAmount = 1,
            GoldCost = 7,
        };

        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(parent, popup, buy).Kind);
        Assert.Equal(1, port.BuyInvocations);
        Assert.True(port.PopupLive);
        Assert.Equal(0, port.ParentPolls);

        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind); // child terminal → close pending
        Assert.True(port.PopupLive);
        Assert.Equal(0, port.CloseInvocations);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind); // exactly one close
        Assert.Equal(1, port.CloseInvocations);
        Assert.False(port.PopupLive);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Completed, runtime.Pump().Kind);
        Assert.Equal(0, port.ParentPolls);
    }

    [Fact]
    public void Reload_refreshes_the_same_owner_epoch_before_select_or_keep_and_never_replays_reload()
    {
        var port = new FakePort(NetherRuntimePopupKind.CodeOffer)
        {
            ReloadStart = new NetherOwnedPopupCodeReloadStart(
                3,
                Candidates(40024),
                string.Empty
            ),
            FreshReload = new NetherCodeReloadEpochRefresh(
                new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 4, 8),
                2,
                Candidates(40025)
            ),
        };
        var runtime = new NetherOwnedPopupNativeStageRuntime(port, maximumPendingPumps: 2);
        port.KeepObserver = owner => runtime.ObserveKeepCancelTask(owner);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext epoch0 = Popup(NetherRuntimePopupKind.CodeOffer);

        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(
            parent,
            epoch0,
            new NetherPlannedAction(NetherActionKind.ReloadCode)
        ).Kind);
        Assert.Equal(1, port.ReloadInvocations);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.ReloadReady, runtime.Pump().Kind);

        NetherRuntimePopupContext epoch1 = epoch0 with { DecisionEpoch = 1 };
        Assert.True(runtime.CanInvokeCodeTerminal(epoch1, NetherActionKind.SelectCode));
        Assert.True(runtime.CanInvokeCodeTerminal(epoch1, NetherActionKind.KeepCode));
        Assert.False(runtime.CanInvokeCodeTerminal(epoch0, NetherActionKind.KeepCode));
        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(
            parent,
            epoch1,
            new NetherPlannedAction(NetherActionKind.KeepCode)
        ).Kind);
        Assert.Equal(1, port.KeepInvocations);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Completed, runtime.Pump().Kind);
        Assert.Equal(1, port.ReloadInvocations);
        Assert.Equal(0, port.ParentPolls);
    }

    [Fact]
    public void Fault_timeout_stale_or_off_repeat_cannot_replay_an_owned_mutation()
    {
        var port = new FakePort(NetherRuntimePopupKind.CodeOffer)
        {
            AutoObserveKeep = false,
        };
        var runtime = new NetherOwnedPopupNativeStageRuntime(port, maximumPendingPumps: 1);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.CodeOffer);
        NetherPlannedAction keep = new(NetherActionKind.KeepCode);

        Assert.Equal(NetherNativeActionResultKind.Started, runtime.Dispatch(parent, popup, keep).Kind);
        Assert.Equal(1, port.KeepInvocations);
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, runtime.Dispatch(parent, popup, keep).Kind); // off/re-enable repeat
        Assert.Equal(1, port.KeepInvocations);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Pending, runtime.Pump().Kind);
        Assert.Equal(NetherOwnedPopupNativeStagePumpKind.Faulted, runtime.Pump().Kind); // bounded missing observer
        Assert.Equal(1, port.KeepInvocations);
        Assert.Equal(0, port.ParentPolls);

        runtime.Reset();
        port.PopupLive = true;
        NetherRuntimePopupContext stale = popup with { Sequence = 9 };
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, runtime.Dispatch(parent, stale, keep).Kind);
        Assert.Equal(1, port.KeepInvocations);
    }

    private static NetherPlannedAction Parent() => new(NetherActionKind.SelectFloor)
    {
        FloorId = 12,
        FloorLevel = 2,
        FloorIndex = 1,
        ExpectedBeforeStatus = NetherSessionStatus.Play,
    };

    private static NetherRuntimePopupContext Popup(NetherRuntimePopupKind kind) => new()
    {
        Kind = kind,
        OwnerAction = NetherActionKind.SelectFloor,
        OwnerGeneration = 4,
        Sequence = 8,
        DecisionEpoch = 0,
    };

    private static NetherRuntimeCodeCandidatesResult Candidates(long id) => new(
        new[]
        {
            new NetherCodeCandidate(id, NetherCodeEffectKind.Risk, 1)
            {
                Category = NetherCodeCategory.ErosionEnhancement,
            },
        },
        IsMasterComplete: true,
        Detail: string.Empty
    );

    private sealed class FakePort : INetherOwnedPopupNativeStagePort
    {
        private readonly NetherRuntimePopupKind _kind;

        public FakePort(NetherRuntimePopupKind kind)
        {
            _kind = kind;
        }

        public bool PopupLive { get; set; } = true;
        public bool AutoObserveKeep { get; set; } = true;
        public int BuyInvocations { get; private set; }
        public int CloseInvocations { get; private set; }
        public int ReloadInvocations { get; private set; }
        public int KeepInvocations { get; private set; }
        public int ParentPolls { get; private set; }
        public Action<NetherCodeKeepCancelOwner>? KeepObserver { get; set; }
        public NetherOwnedPopupCodeReloadStart ReloadStart { get; set; } = new(
            0,
            NetherRuntimeCodeCandidatesResult.Failure("unset"),
            "unset"
        );
        public NetherCodeReloadEpochRefresh FreshReload { get; set; }

        public bool IsCurrentOwnedPopup(NetherRuntimePopupKind kind, NetherOwnedPopupStageOwner owner) =>
            PopupLive
            && kind == _kind
            && owner.OwnerAction == NetherActionKind.SelectFloor
            && owner.Generation == 4
            && owner.Sequence == 8;

        public NetherNativeActionResult InvokeShopPurchase(
            NetherOwnedPopupStageOwner owner,
            NetherPlannedAction action
        )
        {
            BuyInvocations++;
            return NetherNativeActionResult.Started("fake-shop-buy");
        }

        public NetherNativeActionResult PollShopPurchaseTask(NetherShopPurchaseCloseOwner owner) =>
            NetherNativeActionResult.Completed("fake-shop-child-terminal");

        public NetherNativeActionResult InvokeExactShopClose(NetherShopPurchaseCloseOwner owner)
        {
            CloseInvocations++;
            PopupLive = false;
            return NetherNativeActionResult.Started("fake-shop-close");
        }

        public NetherOwnedPopupCodeReloadStart CaptureCodeReloadStart(NetherOwnedPopupStageOwner owner) => ReloadStart;

        public NetherNativeActionResult InvokeCodeReload(NetherCodeReloadEpochOwner owner)
        {
            ReloadInvocations++;
            return NetherNativeActionResult.Started("fake-reroll");
        }

        public NetherNativeActionResult PollCodeReloadTask(NetherCodeReloadEpochOwner owner) =>
            NetherNativeActionResult.Completed("fake-reroll-terminal");

        public NetherCodeReloadEpochRefresh CaptureFreshCodeReloadOffer(NetherCodeReloadEpochOwner owner) => FreshReload;

        public NetherNativeActionResult InvokeCodeKeepCancel(NetherCodeKeepCancelOwner owner)
        {
            KeepInvocations++;
            if (AutoObserveKeep)
                KeepObserver?.Invoke(owner);
            return NetherNativeActionResult.Started("fake-keep-cancel");
        }

        public NetherNativeActionResult PollCodeKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
            NetherNativeActionResult.Completed("fake-keep-terminal");
    }
}
