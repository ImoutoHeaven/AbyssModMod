#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Common immutable identity for the three native child flows that may run beneath one
/// SelectFloor parent or battle-result settlement owner.  It intentionally contains no Unity/IL2CPP objects, so the production
/// ordering and owner validation can be characterized without reimplementing it in a fake
/// bridge.
/// </summary>
internal readonly record struct NetherOwnedPopupStageOwner(
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence,
    long DecisionEpoch
)
{
    public bool IsValid => OwnerAction is (
            NetherActionKind.SelectFloor
            or NetherActionKind.BattleSettlement
            or NetherActionKind.RecoveredCodeOffer
        )
        && Generation > 0
        && Sequence > 0
        && DecisionEpoch >= 0;

    public NetherCodeReloadEpochOwner ReloadOwner => new(OwnerAction, Generation, Sequence);

    public NetherCodeKeepCancelOwner KeepOwner => new(OwnerAction, Generation, Sequence, DecisionEpoch);
}

/// <summary>
/// Read-only evidence captured immediately before the exact native RerollAsync invocation.
/// A failed capture is distinct from zero reloads: it means the bridge cannot prove a safe
/// owner/candidate state and must not send a native action.
/// </summary>
internal readonly record struct NetherOwnedPopupCodeReloadStart(
    int ReloadCount,
    NetherRuntimeCodeCandidatesResult Candidates,
    string Detail
)
{
    public bool IsSuccess => Detail.Length == 0 && Candidates.IsSuccess;

    public static NetherOwnedPopupCodeReloadStart Failure(string detail) => new(
        0,
        NetherRuntimeCodeCandidatesResult.Failure(detail),
        detail
    );
}

/// <summary>
/// The thin native adapter implemented by <see cref="NetherRuntimeBridge"/>.  It owns exact
/// reflection, boxed UniTask handles, and popup instances; this core owns the action switch,
/// owner tuple, sequencing, and bounded no-replay decisions.
/// </summary>
internal interface INetherOwnedPopupNativeStagePort
{
    bool IsCurrentOwnedPopup(NetherRuntimePopupKind kind, NetherOwnedPopupStageOwner owner);

    NetherNativeActionResult InvokeShopPurchase(NetherOwnedPopupStageOwner owner, NetherPlannedAction action);

    NetherNativeActionResult InvokeShopPurchaseConfirm(NetherShopPurchaseCloseOwner owner) =>
        NetherNativeActionResult.BindingUnavailable("shop-purchase-confirm-unavailable");

    NetherNativeActionResult PollShopPurchaseTask(NetherShopPurchaseCloseOwner owner);

    NetherNativeActionResult InvokeExactShopClose(NetherShopPurchaseCloseOwner owner);

    NetherOwnedPopupCodeReloadStart CaptureCodeReloadStart(NetherOwnedPopupStageOwner owner);

    NetherNativeActionResult InvokeCodeReload(NetherCodeReloadEpochOwner owner);

    NetherNativeActionResult PollCodeReloadTask(NetherCodeReloadEpochOwner owner);

    NetherCodeReloadEpochRefresh CaptureFreshCodeReloadOffer(NetherCodeReloadEpochOwner owner);

    NetherNativeActionResult InvokeCodeKeepCancel(NetherCodeKeepCancelOwner owner);

    NetherNativeActionResult PollCodeKeepCancelTask(NetherCodeKeepCancelOwner owner);

    NetherNativeActionResult InvokeCodeTransform(NetherCodeTransformOwner owner) =>
        NetherNativeActionResult.BindingUnavailable("code-transform-start-unavailable");

    NetherNativeActionResult InvokeCodeTransformConfirm(NetherCodeTransformOwner owner) =>
        NetherNativeActionResult.BindingUnavailable("code-transform-confirm-unavailable");

    NetherNativeActionResult InvokeCodeTransformCompleteClose(NetherCodeTransformOwner owner) =>
        NetherNativeActionResult.BindingUnavailable("code-transform-complete-unavailable");

    NetherNativeActionResult PollCodeTransformTask(NetherCodeTransformOwner owner) =>
        NetherNativeActionResult.BindingUnavailable("code-transform-task-unavailable");
}

internal enum NetherOwnedPopupNativeStagePumpKind
{
    None,
    Pending,
    Completed,
    ReloadReady,
    Faulted,
}

internal readonly record struct NetherOwnedPopupNativeStagePumpResult(
    NetherOwnedPopupNativeStagePumpKind Kind,
    NetherNativeActionResult Native
)
{
    public static NetherOwnedPopupNativeStagePumpResult None() => new(
        NetherOwnedPopupNativeStagePumpKind.None,
        NetherNativeActionResult.Completed("owned-popup-stage-idle")
    );
}

/// <summary>
/// Production state machine for Shop Buy, Code Reload and Code Keep.  It never polls a floor
/// parent itself: a caller can do so only after <see cref="Pump"/> reports Completed/None.
/// Consequently a child completion, popup disappearance, or off/re-enable cannot fabricate a
/// SelectFloor terminal or repeat a non-idempotent mutation.
/// </summary>
internal sealed class NetherOwnedPopupNativeStageRuntime
{
    private readonly INetherOwnedPopupNativeStagePort _port;
    private readonly NetherShopPurchaseConfirmCoordinator _shopPurchaseConfirm;
    private readonly NetherShopPurchaseCloseCoordinator _shopPurchaseClose;
    private readonly NetherCodeReloadEpochCoordinator _codeReloadEpoch;
    private readonly NetherCodeKeepCancelCoordinator _codeKeepCancel;
    private readonly NetherCodeTransformNativeFlow _codeTransform;

    public NetherOwnedPopupNativeStageRuntime(
        INetherOwnedPopupNativeStagePort port,
        int maximumPendingPumps = 600
    )
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _shopPurchaseConfirm = new NetherShopPurchaseConfirmCoordinator(maximumPendingPumps);
        _shopPurchaseClose = new NetherShopPurchaseCloseCoordinator(maximumPendingPumps);
        _codeReloadEpoch = new NetherCodeReloadEpochCoordinator(maximumPendingPumps);
        _codeKeepCancel = new NetherCodeKeepCancelCoordinator(maximumPendingPumps);
        _codeTransform = new NetherCodeTransformNativeFlow(maximumPendingPumps);
    }

    public long GetDecisionEpoch(NetherOwnedPopupStageOwner owner) =>
        owner.IsValid ? _codeReloadEpoch.GetDecisionEpoch(owner.ReloadOwner) : 0;

    public NetherCodeKeepCancelOwner? KeepOwner => _codeKeepCancel.Owner;

    public NetherCodeTransformOwner? TransformOwner => _codeTransform.Owner;

    public bool HasPendingMutation => _shopPurchaseConfirm.IsActive
        || _shopPurchaseConfirm.Stage == NetherShopPurchaseConfirmStage.Faulted
        || _shopPurchaseClose.IsActive
        || _shopPurchaseClose.Stage == NetherShopPurchaseCloseStage.Faulted
        || _codeReloadEpoch.IsActive
        || _codeReloadEpoch.Stage == NetherCodeReloadEpochStage.Faulted
        || _codeKeepCancel.IsActive
        || _codeKeepCancel.Stage == NetherCodeKeepCancelStage.Faulted
        || _codeTransform.IsActive
        || _codeTransform.Stage == NetherCodeTransformNativeStage.Faulted;

    /// <summary>
    /// Validates a final Receive/Keep decision against the same current CodeOffer tuple.  The
    /// actual SelectCode UI chain remains in the bridge, but must call this gate first so a
    /// terminal action cannot jump over an active reload or stale decision epoch.
    /// </summary>
    public bool CanInvokeCodeTerminal(NetherRuntimePopupContext popup, NetherActionKind action)
    {
        if (popup == null
            || popup.Kind != NetherRuntimePopupKind.CodeOffer
            || action is not (NetherActionKind.SelectCode or NetherActionKind.KeepCode)
            || !TryCreateOwner(popup, out NetherOwnedPopupStageOwner owner)
            || !_port.IsCurrentOwnedPopup(NetherRuntimePopupKind.CodeOffer, owner)
            || popup.DecisionEpoch != GetDecisionEpoch(owner)
            || _codeReloadEpoch.IsActive
            || _codeReloadEpoch.Stage == NetherCodeReloadEpochStage.Faulted
            || _codeKeepCancel.Stage != NetherCodeKeepCancelStage.Idle)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Starts exactly one production child mutation under the immutable SelectFloor owner.
    /// Non-stage actions are rejected here so a bridge call site cannot accidentally bypass
    /// this owner-checked state machine.
    /// </summary>
    public NetherNativeActionResult Dispatch(
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
        if ((!floorOwner && !resultCodeOwner)
            || popup == null
            || !TryCreateOwner(popup, out NetherOwnedPopupStageOwner owner)
            || !_port.IsCurrentOwnedPopup(popup.Kind, owner))
        {
            return NetherNativeActionResult.BindingUnavailable("owned-popup-stage-owner-unavailable");
        }

        return action.Kind switch
        {
            NetherActionKind.BuyShopItem => BeginShopPurchase(popup, owner, action),
            NetherActionKind.ReloadCode => BeginCodeReload(popup, owner),
            NetherActionKind.KeepCode => BeginCodeKeep(popup, owner),
            NetherActionKind.TransformCode => BeginCodeTransform(popup, owner, action),
            _ => NetherNativeActionResult.Rejected("unsupported-owned-popup-stage-action:" + action.Kind),
        };
    }

    /// <summary>
    /// Called by the exact Harmony postfix for HandleCancelSequenceAsync.  Registration must
    /// still match the owner that invoked b__12_0; unrelated player cancels cannot satisfy it.
    /// </summary>
    public bool ObserveKeepCancelTask(NetherCodeKeepCancelOwner owner) =>
        _codeKeepCancel.Stage == NetherCodeKeepCancelStage.AwaitingTaskRegistration
        && _codeKeepCancel.Owner is NetherCodeKeepCancelOwner expected
        && expected == owner
        && _port.IsCurrentOwnedPopup(
            NetherRuntimePopupKind.CodeOffer,
            new NetherOwnedPopupStageOwner(
                owner.OwnerAction,
                owner.Generation,
                owner.Sequence,
                owner.DecisionEpoch
            )
        )
        && _codeKeepCancel.ObserveTask(owner);

    public bool ObserveCodeTransformTask(NetherCodeTransformOwner owner) =>
        _codeTransform.Owner is NetherCodeTransformOwner expected
        && expected == owner
        && _port.IsCurrentOwnedPopup(
            NetherRuntimePopupKind.CodeTransform,
            new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
        )
        && _codeTransform.ObserveTask(owner);

    /// <summary>
    /// Advances one owned child at a time.  ReloadReady deliberately returns its own typed
    /// result, forcing the caller to re-dispatch a newer CodeOffer decision before it may poll
    /// the original parent task.
    /// </summary>
    public NetherOwnedPopupNativeStagePumpResult Pump()
    {
        if (_shopPurchaseConfirm.IsActive
            || _shopPurchaseConfirm.Stage == NetherShopPurchaseConfirmStage.Faulted)
        {
            NetherNativeActionResult result = _shopPurchaseConfirm.Pump(
                () => _shopPurchaseConfirm.Owner is NetherShopPurchaseCloseOwner owner
                    ? _port.InvokeShopPurchaseConfirm(owner)
                    : NetherNativeActionResult.BindingUnavailable(
                        "shop-purchase-confirm-missing-owner"
                    )
            );
            return result.Kind switch
            {
                NetherNativeActionResultKind.Started => new(
                    NetherOwnedPopupNativeStagePumpKind.Pending,
                    result
                ),
                NetherNativeActionResultKind.Completed => new(
                    NetherOwnedPopupNativeStagePumpKind.Pending,
                    NetherNativeActionResult.Started("shop-purchase-confirm-complete")
                ),
                _ => new(NetherOwnedPopupNativeStagePumpKind.Faulted, result),
            };
        }

        if (_shopPurchaseClose.IsActive || _shopPurchaseClose.Stage == NetherShopPurchaseCloseStage.Faulted)
        {
            NetherNativeActionResult result = _shopPurchaseClose.Pump(
                () => _shopPurchaseClose.Owner is NetherShopPurchaseCloseOwner owner
                    ? _port.PollShopPurchaseTask(owner)
                    : NetherNativeActionResult.BindingUnavailable("shop-purchase-missing-owner"),
                () => _shopPurchaseClose.Owner is NetherShopPurchaseCloseOwner owner
                    ? _port.InvokeExactShopClose(owner)
                    : NetherNativeActionResult.BindingUnavailable("shop-purchase-close-missing-owner")
            );
            return ToPumpResult(result, reloadReady: false);
        }

        if (_codeReloadEpoch.IsActive || _codeReloadEpoch.Stage == NetherCodeReloadEpochStage.Faulted)
        {
            NetherNativeActionResult result = _codeReloadEpoch.Pump(
                () => _codeReloadEpoch.Owner is NetherCodeReloadEpochOwner owner
                    ? _port.PollCodeReloadTask(owner)
                    : NetherNativeActionResult.BindingUnavailable("code-reload-missing-owner"),
                () => _codeReloadEpoch.Owner is NetherCodeReloadEpochOwner owner
                    ? _port.CaptureFreshCodeReloadOffer(owner)
                    : new NetherCodeReloadEpochRefresh(
                        default,
                        0,
                        NetherRuntimeCodeCandidatesResult.Failure("code-reload-missing-owner")
                    )
            );
            return ToPumpResult(result, reloadReady: result.Kind == NetherNativeActionResultKind.Completed);
        }

        if (_codeKeepCancel.IsActive || _codeKeepCancel.Stage == NetherCodeKeepCancelStage.Faulted)
        {
            NetherNativeActionResult result = _codeKeepCancel.Pump(
                () => _codeKeepCancel.Owner is NetherCodeKeepCancelOwner owner
                    ? _port.PollCodeKeepCancelTask(owner)
                    : NetherNativeActionResult.BindingUnavailable("code-keep-cancel-missing-owner")
            );
            return ToPumpResult(result, reloadReady: false);
        }

        if (_codeTransform.IsActive || _codeTransform.Stage == NetherCodeTransformNativeStage.Faulted)
        {
            NetherNativeActionResult result = _codeTransform.Pump(
                () => _codeTransform.Owner is NetherCodeTransformOwner owner
                    ? _port.InvokeCodeTransformConfirm(owner)
                    : NetherNativeActionResult.BindingUnavailable("code-transform-missing-owner"),
                () => _codeTransform.Owner is NetherCodeTransformOwner owner
                    ? _port.InvokeCodeTransformCompleteClose(owner)
                    : NetherNativeActionResult.BindingUnavailable("code-transform-missing-owner"),
                () => _codeTransform.Owner is NetherCodeTransformOwner owner
                    ? _port.PollCodeTransformTask(owner)
                    : NetherNativeActionResult.BindingUnavailable("code-transform-missing-owner")
            );
            return ToPumpResult(result, reloadReady: false);
        }

        return NetherOwnedPopupNativeStagePumpResult.None();
    }

    public void Reset()
    {
        _shopPurchaseConfirm.Reset();
        _shopPurchaseClose.Reset();
        _codeReloadEpoch.Reset();
        _codeKeepCancel.Reset();
        _codeTransform.Reset();
    }

    private NetherNativeActionResult BeginShopPurchase(
        NetherRuntimePopupContext popup,
        NetherOwnedPopupStageOwner owner,
        NetherPlannedAction action
    )
    {
        if (popup.Kind != NetherRuntimePopupKind.Shop
            || action.ContentId <= 0
            || action.ContentAmount <= 0
            || action.GoldCost < 0)
        {
            return NetherNativeActionResult.BindingUnavailable("invalid-owned-shop-purchase");
        }

        var purchaseOwner = new NetherShopPurchaseCloseOwner(
            owner.OwnerAction,
            owner.Generation,
            owner.Sequence,
            action.ContentId,
            action.ContentAmount,
            action.GoldCost
        );
        if (!_shopPurchaseConfirm.Begin(purchaseOwner)
            || !_shopPurchaseClose.Begin(purchaseOwner))
        {
            _shopPurchaseConfirm.Reset();
            _shopPurchaseClose.Reset();
            return NetherNativeActionResult.BindingUnavailable("shop-purchase-stage-already-active");
        }

        NetherNativeActionResult invoked = _port.InvokeShopPurchase(owner, action);
        if (invoked.Kind == NetherNativeActionResultKind.Started)
            return invoked;

        _shopPurchaseConfirm.Reset();
        _shopPurchaseClose.Reset();
        return invoked;
    }

    private NetherNativeActionResult BeginCodeReload(
        NetherRuntimePopupContext popup,
        NetherOwnedPopupStageOwner owner
    )
    {
        if (popup.Kind != NetherRuntimePopupKind.CodeOffer
            || popup.DecisionEpoch != GetDecisionEpoch(owner)
            || _codeKeepCancel.Stage != NetherCodeKeepCancelStage.Idle)
        {
            return NetherNativeActionResult.BindingUnavailable("stale-code-offer-decision-epoch");
        }

        NetherOwnedPopupCodeReloadStart start = _port.CaptureCodeReloadStart(owner);
        if (!start.IsSuccess || !_codeReloadEpoch.Begin(owner.ReloadOwner, start.ReloadCount, start.Candidates))
        {
            return NetherNativeActionResult.BindingUnavailable(
                "code-reload-invalid-owner-or-candidates:" + start.Detail
            );
        }

        NetherNativeActionResult invoked = _port.InvokeCodeReload(owner.ReloadOwner);
        if (invoked.Kind == NetherNativeActionResultKind.Started)
            return invoked;

        _codeReloadEpoch.Reset();
        return invoked;
    }

    private NetherNativeActionResult BeginCodeKeep(
        NetherRuntimePopupContext popup,
        NetherOwnedPopupStageOwner owner
    )
    {
        if (!CanInvokeCodeTerminal(popup, NetherActionKind.KeepCode)
            || !_codeKeepCancel.Begin(owner.KeepOwner))
        {
            return NetherNativeActionResult.BindingUnavailable("code-keep-cancel-already-in-flight-or-stale");
        }

        NetherNativeActionResult invoked = _port.InvokeCodeKeepCancel(owner.KeepOwner);
        if (invoked.Kind == NetherNativeActionResultKind.Started)
            return invoked;

        _codeKeepCancel.Reset();
        return invoked;
    }

    private NetherNativeActionResult BeginCodeTransform(
        NetherRuntimePopupContext popup,
        NetherOwnedPopupStageOwner owner,
        NetherPlannedAction action
    )
    {
        var transformOwner = new NetherCodeTransformOwner(
            owner.OwnerAction,
            owner.Generation,
            owner.Sequence,
            action.ReplaceCodeId
        );
        if (popup.Kind != NetherRuntimePopupKind.CodeTransform
            || !transformOwner.IsValid
            || !_codeTransform.Begin(transformOwner))
        {
            return NetherNativeActionResult.BindingUnavailable("code-transform-already-in-flight-or-invalid");
        }

        NetherNativeActionResult invoked = _port.InvokeCodeTransform(transformOwner);
        if (invoked.Kind == NetherNativeActionResultKind.Started)
            return invoked;

        _codeTransform.Reset();
        return invoked;
    }

    private static bool TryCreateOwner(
        NetherRuntimePopupContext popup,
        out NetherOwnedPopupStageOwner owner
    )
    {
        owner = new NetherOwnedPopupStageOwner(
            popup.OwnerAction,
            popup.OwnerGeneration,
            popup.Sequence,
            popup.DecisionEpoch
        );
        return owner.IsValid;
    }

    private static NetherOwnedPopupNativeStagePumpResult ToPumpResult(
        NetherNativeActionResult result,
        bool reloadReady
    ) => result.Kind switch
    {
        NetherNativeActionResultKind.Started => new(
            NetherOwnedPopupNativeStagePumpKind.Pending,
            result
        ),
        NetherNativeActionResultKind.Completed => new(
            reloadReady
                ? NetherOwnedPopupNativeStagePumpKind.ReloadReady
                : NetherOwnedPopupNativeStagePumpKind.Completed,
            result
        ),
        _ => new(NetherOwnedPopupNativeStagePumpKind.Faulted, result),
    };
}
