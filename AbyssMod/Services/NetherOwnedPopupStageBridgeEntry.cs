#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Typed result for the only owned-modal boundary before an original SelectFloor parent is
/// observed.  <see cref="MayPollParent"/> is false for every child/close/refresh/fault state;
/// callers must preserve the same parent task rather than manufacture a settlement.
/// </summary>
internal readonly record struct NetherOwnedPopupStageParentGate(
    bool MayPollParent,
    NetherNativeActionResult Native
)
{
    public static NetherOwnedPopupStageParentGate Allow() => new(
        true,
        NetherNativeActionResult.Completed("owned-popup-stage-parent-gate-open")
    );
}

/// <summary>
/// Non-virtual production adapter boundary for an owned SelectFloor popup.  The real
/// reflection bridge inherits this type, so it cannot replace Buy/Reload/Keep dispatch or the
/// parent gate with a local switch.  Tests compile and exercise this inherited entrypoint with
/// a truthful native port.
/// </summary>
internal abstract class NetherOwnedPopupStageBridgeAdapter
{
    private NetherOwnedPopupStageBridgeEntry? _entry;

    private NetherOwnedPopupStageBridgeEntry Entry
    {
        get
        {
            if (_entry != null)
                return _entry;
            if (this is not INetherOwnedPopupNativeStagePort port)
            {
                throw new InvalidOperationException(
                    GetType().FullName + " must implement " + nameof(INetherOwnedPopupNativeStagePort)
                );
            }
            _entry = new NetherOwnedPopupStageBridgeEntry(port);
            return _entry;
        }
    }

    /// <summary>
    /// The only public owned-popup dispatch route.  It remains non-virtual deliberately: native
    /// adapters may supply exact UI callbacks through the protected hooks, but cannot bypass the
    /// typed shared dispatch table.
    /// </summary>
    public NetherNativeActionResult InvokeOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    )
    {
        bool floorOwner = parent.Kind == NetherActionKind.SelectFloor;
        bool resultCodeOwner = parent.Kind is (
                NetherActionKind.BattleSettlement or NetherActionKind.RecoveredCodeOffer
            )
            && popup?.Kind == NetherRuntimePopupKind.CodeOffer
            && action.Kind is (
                NetherActionKind.SelectCode
                or NetherActionKind.ReloadCode
                or NetherActionKind.KeepCode
            );
        if ((!floorOwner && !resultCodeOwner) || popup == null)
            return NetherNativeActionResult.BindingUnavailable("invalid-owned-popup-parent");
        if (!HasMatchingOwnedPopup(parent, popup))
            return NetherNativeActionResult.BindingUnavailable("missing-matching-owned-popup");

        return Entry.Dispatch(
            parent,
            popup,
            action,
            InvokeOwnedEventOption,
            InvokeOwnedLeaveShop,
            InvokeOwnedSelectCode
        );
    }

    protected long GetOwnedPopupDecisionEpoch(NetherOwnedPopupStageOwner owner) =>
        Entry.GetDecisionEpoch(owner);

    protected NetherCodeKeepCancelOwner? OwnedPopupKeepOwner => Entry.KeepOwner;

    protected NetherCodeTransformOwner? OwnedPopupTransformOwner => Entry.TransformOwner;

    protected bool ObserveOwnedPopupKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
        Entry.ObserveKeepCancelTask(owner);

    protected bool ObserveOwnedPopupCodeTransformTask(NetherCodeTransformOwner owner) =>
        Entry.ObserveCodeTransformTask(owner);

    protected NetherOwnedPopupStageParentGate PumpOwnedPopupStagesBeforeParent() =>
        Entry.PumpBeforeParent();

    protected void ResetOwnedPopupStages() => _entry?.Reset();

    protected abstract bool HasMatchingOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup
    );

    protected abstract NetherNativeActionResult InvokeOwnedEventOption(NetherPlannedAction action);

    protected abstract NetherNativeActionResult InvokeOwnedLeaveShop();

    protected abstract NetherNativeActionResult InvokeOwnedSelectCode(NetherPlannedAction action);
}

/// <summary>
/// Test-compilable production entrypoint shared by the reflection Bridge and its controller
/// characterization seam.  It is intentionally the sole holder of
/// <see cref="NetherOwnedPopupNativeStageRuntime"/>: adapters may supply exact native callbacks
/// and task handles, but cannot duplicate or bypass the Buy/Reload/Keep dispatch and parent-gate
/// rules.
/// </summary>
internal sealed class NetherOwnedPopupStageBridgeEntry
{
    private readonly NetherOwnedPopupNativeStageRuntime _runtime;

    public NetherOwnedPopupStageBridgeEntry(
        INetherOwnedPopupNativeStagePort port,
        int maximumPendingPumps = 600
    )
    {
        _runtime = new NetherOwnedPopupNativeStageRuntime(
            port ?? throw new ArgumentNullException(nameof(port)),
            maximumPendingPumps
        );
    }

    public long GetDecisionEpoch(NetherOwnedPopupStageOwner owner) =>
        _runtime.GetDecisionEpoch(owner);

    public NetherCodeKeepCancelOwner? KeepOwner => _runtime.KeepOwner;

    public NetherCodeTransformOwner? TransformOwner => _runtime.TransformOwner;

    public bool ObserveKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
        _runtime.ObserveKeepCancelTask(owner);

    public bool ObserveCodeTransformTask(NetherCodeTransformOwner owner) =>
        _runtime.ObserveCodeTransformTask(owner);

    public void Reset() => _runtime.Reset();

    /// <summary>
    /// The one typed dispatch table used by production adapters.  Event/Leave/Select remain
    /// adapter-owned because their exact native callbacks differ, while every non-idempotent
    /// staged operation must pass through the shared runtime.
    /// </summary>
    public NetherNativeActionResult Dispatch(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction action,
        Func<NetherPlannedAction, NetherNativeActionResult> selectEventOption,
        Func<NetherNativeActionResult> leaveShop,
        Func<NetherPlannedAction, NetherNativeActionResult> selectCode
    )
    {
        if (selectEventOption == null)
            throw new ArgumentNullException(nameof(selectEventOption));
        if (leaveShop == null)
            throw new ArgumentNullException(nameof(leaveShop));
        if (selectCode == null)
            throw new ArgumentNullException(nameof(selectCode));

        return action.Kind switch
        {
            NetherActionKind.SelectEventOption => selectEventOption(action),
            NetherActionKind.LeaveShop => leaveShop(),
            NetherActionKind.BuyShopItem or NetherActionKind.ReloadCode or NetherActionKind.KeepCode
                or NetherActionKind.TransformCode =>
                _runtime.Dispatch(parent, popup, action),
            NetherActionKind.SelectCode => _runtime.CanInvokeCodeTerminal(popup, action.Kind)
                ? selectCode(action)
                : NetherNativeActionResult.BindingUnavailable("stale-or-incomplete-owned-code-offer"),
            _ => NetherNativeActionResult.Rejected("unsupported-owned-popup-action:" + action.Kind),
        };
    }

    /// <summary>
    /// Pumps exactly the shared staged runtime before any adapter may poll the original parent.
    /// A fresh Reload offer remains a blocking result so the same popup must be redispatched at
    /// its new epoch first.
    /// </summary>
    public NetherOwnedPopupStageParentGate PumpBeforeParent()
    {
        NetherOwnedPopupNativeStagePumpResult stage = _runtime.Pump();
        return stage.Kind switch
        {
            NetherOwnedPopupNativeStagePumpKind.None or NetherOwnedPopupNativeStagePumpKind.Completed =>
                NetherOwnedPopupStageParentGate.Allow(),
            NetherOwnedPopupNativeStagePumpKind.ReloadReady => new(
                false,
                NetherNativeActionResult.Started("code-reload-fresh-offer-ready")
            ),
            NetherOwnedPopupNativeStagePumpKind.Pending or NetherOwnedPopupNativeStagePumpKind.Faulted => new(
                false,
                stage.Native
            ),
            _ => new(false, NetherNativeActionResult.BindingUnavailable("unknown-owned-popup-stage-gate")),
        };
    }
}
