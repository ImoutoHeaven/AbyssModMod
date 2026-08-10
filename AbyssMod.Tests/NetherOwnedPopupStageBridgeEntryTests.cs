#nullable enable

using System;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// These tests execute the shared entrypoint used by both the IL2CPP Bridge and the production
/// controller seam.  The fake native port intentionally leaves a child and its SelectFloor
/// parent pending, so this cannot pass by treating a child callback as a parent terminal.
/// </summary>
public class NetherOwnedPopupStageBridgeEntryTests
{
    [Fact]
    public void Entry_routes_shop_buy_through_shared_core_before_releasing_parent_gate()
    {
        var port = new EntryPort(NetherRuntimePopupKind.Shop);
        var entry = new NetherOwnedPopupStageBridgeEntry(port, maximumPendingPumps: 2);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.Shop);
        NetherPlannedAction buy = new(NetherActionKind.BuyShopItem)
        {
            ContentId = 42,
            ContentAmount = 1,
            GoldCost = 7,
        };

        NetherNativeActionResult started = entry.Dispatch(parent, popup, buy, RejectEvent, RejectLeave, RejectCode);
        Assert.Equal(NetherNativeActionResultKind.Started, started.Kind);
        Assert.Equal(1, port.BuyInvocations);

        Assert.False(entry.PumpBeforeParent().MayPollParent); // exact purchase confirmation
        Assert.False(entry.PumpBeforeParent().MayPollParent); // child terminal -> close pending
        Assert.False(entry.PumpBeforeParent().MayPollParent); // exact close once
        Assert.Equal(1, port.CloseInvocations);
        Assert.True(entry.PumpBeforeParent().MayPollParent);  // only parent may now be observed
        Assert.Equal(0, port.ParentPolls);
    }

    [Fact]
    public void Adapter_inherited_entrypoint_routes_shop_through_the_same_shared_parent_gate()
    {
        var port = new EntryPort(NetherRuntimePopupKind.Shop);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.Shop);
        NetherPlannedAction buy = new(NetherActionKind.BuyShopItem)
        {
            ContentId = 42,
            ContentAmount = 1,
            GoldCost = 7,
        };

        Assert.Equal(NetherNativeActionResultKind.Started, port.DispatchViaAdapter(parent, popup, buy).Kind);
        Assert.Equal(1, port.BuyInvocations);
        Assert.False(port.PumpViaAdapter().MayPollParent);
        Assert.False(port.PumpViaAdapter().MayPollParent);
        Assert.False(port.PumpViaAdapter().MayPollParent);
        Assert.True(port.PumpViaAdapter().MayPollParent);
        Assert.Equal(1, port.CloseInvocations);
    }

    [Fact]
    public void Entry_preserves_same_popup_reload_epoch_then_allows_exact_terminal_choice()
    {
        var port = new EntryPort(NetherRuntimePopupKind.CodeOffer)
        {
            ReloadStart = new NetherOwnedPopupCodeReloadStart(2, Candidates(40024), string.Empty),
            FreshReload = new NetherCodeReloadEpochRefresh(
                new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 4, 8),
                1,
                Candidates(30024)
            ),
        };
        var entry = new NetherOwnedPopupStageBridgeEntry(port, maximumPendingPumps: 2);
        port.ObserveKeep = owner => entry.ObserveKeepCancelTask(owner);
        NetherPlannedAction parent = Parent();
        NetherRuntimePopupContext epoch0 = Popup(NetherRuntimePopupKind.CodeOffer);

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            entry.Dispatch(parent, epoch0, new NetherPlannedAction(NetherActionKind.ReloadCode), RejectEvent, RejectLeave, RejectCode).Kind
        );
        Assert.False(entry.PumpBeforeParent().MayPollParent);
        NetherOwnedPopupStageParentGate fresh = entry.PumpBeforeParent();
        Assert.False(fresh.MayPollParent);
        Assert.Equal("code-reload-fresh-offer-ready", fresh.Native.Detail);
        Assert.Equal(1, port.ReloadInvocations);

        NetherRuntimePopupContext epoch1 = epoch0 with { DecisionEpoch = 1 };
        int keeps = 0;
        Assert.Equal(
            NetherNativeActionResultKind.Started,
            entry.Dispatch(
                parent,
                epoch1,
                new NetherPlannedAction(NetherActionKind.KeepCode),
                RejectEvent,
                RejectLeave,
                _ =>
                {
                    keeps++;
                    return NetherNativeActionResult.Started("unexpected-select");
                }
            ).Kind
        );
        Assert.Equal(0, keeps);
        Assert.Equal(1, port.KeepInvocations);
        Assert.True(entry.PumpBeforeParent().MayPollParent);
    }

    [Fact]
    public void Entry_routes_select_code_only_after_shared_terminal_gate()
    {
        var port = new EntryPort(NetherRuntimePopupKind.CodeOffer);
        var entry = new NetherOwnedPopupStageBridgeEntry(port, maximumPendingPumps: 2);
        int selects = 0;

        NetherNativeActionResult result = entry.Dispatch(
            Parent(),
            Popup(NetherRuntimePopupKind.CodeOffer),
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 },
            RejectEvent,
            RejectLeave,
            _ =>
            {
                selects++;
                return NetherNativeActionResult.Started("exact-code-select");
            }
        );

        Assert.Equal(NetherNativeActionResultKind.Started, result.Kind);
        Assert.Equal(1, selects);
        Assert.Equal(0, port.ReloadInvocations);
        Assert.Equal(0, port.KeepInvocations);
    }

    [Fact]
    public void Battle_result_owner_can_select_code_through_the_same_terminal_gate()
    {
        var port = new EntryPort(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.BattleSettlement
        );
        NetherPlannedAction parent = new(NetherActionKind.BattleSettlement);
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.CodeOffer) with
        {
            OwnerAction = NetherActionKind.BattleSettlement,
        };

        NetherNativeActionResult result = port.DispatchViaAdapter(
            parent,
            popup,
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 }
        );

        Assert.Equal(NetherNativeActionResultKind.Started, result.Kind);
    }

    [Fact]
    public void Recovered_start_status_owner_can_select_code_through_the_same_terminal_gate()
    {
        var port = new EntryPort(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.RecoveredCodeOffer
        );
        NetherPlannedAction parent = new(NetherActionKind.RecoveredCodeOffer);
        NetherRuntimePopupContext popup = Popup(NetherRuntimePopupKind.CodeOffer) with
        {
            OwnerAction = NetherActionKind.RecoveredCodeOffer,
        };

        NetherNativeActionResult result = port.DispatchViaAdapter(
            parent,
            popup,
            new NetherPlannedAction(NetherActionKind.SelectCode) { CodeId = 30024 }
        );

        Assert.Equal(NetherNativeActionResultKind.Started, result.Kind);
    }

    [Fact]
    public void Battle_result_owner_can_reroll_then_keep_without_a_floor_parent()
    {
        var port = new EntryPort(
            NetherRuntimePopupKind.CodeOffer,
            NetherActionKind.BattleSettlement
        )
        {
            ReloadStart = new NetherOwnedPopupCodeReloadStart(2, Candidates(40024), string.Empty),
            FreshReload = new NetherCodeReloadEpochRefresh(
                new NetherCodeReloadEpochOwner(NetherActionKind.BattleSettlement, 4, 8),
                1,
                Candidates(30024)
            ),
        };
        var entry = new NetherOwnedPopupStageBridgeEntry(port, maximumPendingPumps: 2);
        port.ObserveKeep = owner => entry.ObserveKeepCancelTask(owner);
        NetherPlannedAction parent = new(NetherActionKind.BattleSettlement);
        NetherRuntimePopupContext epoch0 = Popup(NetherRuntimePopupKind.CodeOffer) with
        {
            OwnerAction = NetherActionKind.BattleSettlement,
        };

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            entry.Dispatch(
                parent,
                epoch0,
                new NetherPlannedAction(NetherActionKind.ReloadCode),
                RejectEvent,
                RejectLeave,
                RejectCode
            ).Kind
        );
        Assert.False(entry.PumpBeforeParent().MayPollParent);
        Assert.Equal(
            "code-reload-fresh-offer-ready",
            entry.PumpBeforeParent().Native.Detail
        );

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            entry.Dispatch(
                parent,
                epoch0 with { DecisionEpoch = 1 },
                new NetherPlannedAction(NetherActionKind.KeepCode),
                RejectEvent,
                RejectLeave,
                RejectCode
            ).Kind
        );
        Assert.True(entry.PumpBeforeParent().MayPollParent);
        Assert.Equal(1, port.ReloadInvocations);
        Assert.Equal(1, port.KeepInvocations);
    }

    private static NetherNativeActionResult RejectEvent(NetherPlannedAction action) =>
        NetherNativeActionResult.BindingUnavailable("unexpected-event:" + action.Kind);

    private static NetherNativeActionResult RejectLeave() =>
        NetherNativeActionResult.BindingUnavailable("unexpected-leave");

    private static NetherNativeActionResult RejectCode(NetherPlannedAction action) =>
        NetherNativeActionResult.BindingUnavailable("unexpected-code:" + action.Kind);

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

    private sealed class EntryPort : NetherOwnedPopupStageBridgeAdapter, INetherOwnedPopupNativeStagePort
    {
        private readonly NetherRuntimePopupKind _kind;

        private readonly NetherActionKind _ownerAction;

        public EntryPort(
            NetherRuntimePopupKind kind,
            NetherActionKind ownerAction = NetherActionKind.SelectFloor
        )
        {
            _kind = kind;
            _ownerAction = ownerAction;
        }

        public int BuyInvocations { get; private set; }
        public int CloseInvocations { get; private set; }
        public int ReloadInvocations { get; private set; }
        public int KeepInvocations { get; private set; }
        public int ParentPolls { get; private set; }
        public Action<NetherCodeKeepCancelOwner>? ObserveKeep { get; set; }
        public NetherOwnedPopupCodeReloadStart ReloadStart { get; set; } = new(
            0,
            NetherRuntimeCodeCandidatesResult.Failure("unset"),
            "unset"
        );
        public NetherCodeReloadEpochRefresh FreshReload { get; set; }

        public NetherNativeActionResult DispatchViaAdapter(
            NetherPlannedAction parent,
            NetherRuntimePopupContext popup,
            NetherPlannedAction action
        ) => InvokeOwnedPopup(parent, popup, action);

        public NetherOwnedPopupStageParentGate PumpViaAdapter() => PumpOwnedPopupStagesBeforeParent();

        protected override bool HasMatchingOwnedPopup(
            NetherPlannedAction parent,
            NetherRuntimePopupContext popup
        ) => parent.Kind == _ownerAction
            && IsCurrentOwnedPopup(popup.Kind, new NetherOwnedPopupStageOwner(
                popup.OwnerAction,
                popup.OwnerGeneration,
                popup.Sequence,
                popup.DecisionEpoch
            ));

        protected override NetherNativeActionResult InvokeOwnedEventOption(NetherPlannedAction action) =>
            NetherNativeActionResult.BindingUnavailable("entry-port-event-not-configured");

        protected override NetherNativeActionResult InvokeOwnedLeaveShop() =>
            NetherNativeActionResult.BindingUnavailable("entry-port-leave-not-configured");

        protected override NetherNativeActionResult InvokeOwnedSelectCode(NetherPlannedAction action) =>
            NetherNativeActionResult.Started("entry-port-select-code");

        public bool IsCurrentOwnedPopup(NetherRuntimePopupKind kind, NetherOwnedPopupStageOwner owner) =>
            kind == _kind
            && owner.OwnerAction == _ownerAction
            && owner.Generation == 4
            && owner.Sequence == 8;

        public NetherNativeActionResult InvokeShopPurchase(NetherOwnedPopupStageOwner owner, NetherPlannedAction action)
        {
            BuyInvocations++;
            return NetherNativeActionResult.Started("entry-shop-buy");
        }

        public NetherNativeActionResult PollShopPurchaseTask(NetherShopPurchaseCloseOwner owner) =>
            NetherNativeActionResult.Completed("entry-shop-child");

        public NetherNativeActionResult InvokeShopPurchaseConfirm(
            NetherShopPurchaseCloseOwner owner
        ) => NetherNativeActionResult.Completed("entry-shop-confirm");

        public NetherNativeActionResult InvokeExactShopClose(NetherShopPurchaseCloseOwner owner)
        {
            CloseInvocations++;
            return NetherNativeActionResult.Started("entry-shop-close");
        }

        public NetherOwnedPopupCodeReloadStart CaptureCodeReloadStart(NetherOwnedPopupStageOwner owner) => ReloadStart;

        public NetherNativeActionResult InvokeCodeReload(NetherCodeReloadEpochOwner owner)
        {
            ReloadInvocations++;
            return NetherNativeActionResult.Started("entry-reload");
        }

        public NetherNativeActionResult PollCodeReloadTask(NetherCodeReloadEpochOwner owner) =>
            NetherNativeActionResult.Completed("entry-reload-task");

        public NetherCodeReloadEpochRefresh CaptureFreshCodeReloadOffer(NetherCodeReloadEpochOwner owner) => FreshReload;

        public NetherNativeActionResult InvokeCodeKeepCancel(NetherCodeKeepCancelOwner owner)
        {
            KeepInvocations++;
            ObserveKeep?.Invoke(owner);
            return NetherNativeActionResult.Started("entry-keep");
        }

        public NetherNativeActionResult PollCodeKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
            NetherNativeActionResult.Completed("entry-keep-task");
    }
}
