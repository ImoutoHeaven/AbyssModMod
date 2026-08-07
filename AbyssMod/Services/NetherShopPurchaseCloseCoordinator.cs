#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Immutable identity for the one Shop buy child that belongs to a SelectFloor parent.  It is
/// deliberately more specific than a popup kind: a stale popup sequence must never receive a
/// close after a later floor action owns the same controller type.
/// </summary>
internal readonly record struct NetherShopPurchaseCloseOwner(
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence,
    long ContentId,
    int ContentAmount,
    int GoldCost
);

internal enum NetherShopPurchaseCloseStage
{
    Idle,
    AwaitingPurchaseTask,
    ClosePending,
    AwaitingParent,
    Faulted,
}

/// <summary>
/// Owns the native Shop Buy sub-sequence.  The packaged OnPurchaseContentAsync task settles
/// only the purchase; the Shop popup remains open until its exact SetupPopupEvent close Action
/// is invoked.  This coordinator serializes Buy -> close -> original floor parent without ever
/// replaying Buy or using visual disappearance as a success signal.
/// </summary>
internal sealed class NetherShopPurchaseCloseCoordinator
{
    private readonly int _maximumPendingPumps;
    private NetherShopPurchaseCloseOwner? _owner;
    private string _faultDetail = string.Empty;
    private int _pendingPumps;

    public NetherShopPurchaseCloseCoordinator(int maximumPendingPumps = 600)
    {
        if (maximumPendingPumps < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingPumps));
        _maximumPendingPumps = maximumPendingPumps;
    }

    public NetherShopPurchaseCloseStage Stage { get; private set; } = NetherShopPurchaseCloseStage.Idle;

    public NetherShopPurchaseCloseOwner? Owner => _owner;

    public bool IsActive => Stage is NetherShopPurchaseCloseStage.AwaitingPurchaseTask
        or NetherShopPurchaseCloseStage.ClosePending
        or NetherShopPurchaseCloseStage.AwaitingParent;

    public bool Begin(NetherShopPurchaseCloseOwner owner)
    {
        if (Stage != NetherShopPurchaseCloseStage.Idle
            || owner.OwnerAction != NetherActionKind.SelectFloor
            || owner.Generation <= 0
            || owner.Sequence <= 0
            || owner.ContentId <= 0
            || owner.ContentAmount <= 0
            || owner.GoldCost < 0)
        {
            return false;
        }

        _owner = owner;
        _faultDetail = string.Empty;
        _pendingPumps = 0;
        Stage = NetherShopPurchaseCloseStage.AwaitingPurchaseTask;
        return true;
    }

    /// <summary>
    /// Pumps only the child task and exact close callback.  Once it reports Completed the
    /// caller, not this class, must poll the original parent task.  A fault is sticky so an
    /// update repeat cannot retry the close or purchase.
    /// </summary>
    public NetherNativeActionResult Pump(
        Func<NetherNativeActionResult> pollPurchaseTask,
        Func<NetherNativeActionResult> invokeExactClose
    )
    {
        if (pollPurchaseTask == null)
            throw new ArgumentNullException(nameof(pollPurchaseTask));
        if (invokeExactClose == null)
            throw new ArgumentNullException(nameof(invokeExactClose));

        switch (Stage)
        {
            case NetherShopPurchaseCloseStage.AwaitingPurchaseTask:
            {
                NetherNativeActionResult purchase = pollPurchaseTask();
                if (purchase.Kind == NetherNativeActionResultKind.Started)
                {
                    if (++_pendingPumps > _maximumPendingPumps)
                    {
                        return Fault(
                            "shop-purchase-timeout",
                            NetherNativeActionResult.BindingUnavailable("pending-pump-limit")
                        );
                    }
                    return NetherNativeActionResult.Started("shop-purchase-awaiting-child");
                }
                if (purchase.Kind != NetherNativeActionResultKind.Completed)
                    return Fault("shop-purchase-child", purchase);

                Stage = NetherShopPurchaseCloseStage.ClosePending;
                return NetherNativeActionResult.Started("shop-purchase-child-complete");
            }
            case NetherShopPurchaseCloseStage.ClosePending:
            {
                NetherNativeActionResult close = invokeExactClose();
                if (close.Kind is not (NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed))
                    return Fault("shop-purchase-close", close);

                Stage = NetherShopPurchaseCloseStage.AwaitingParent;
                return NetherNativeActionResult.Started("shop-purchase-close-invoked");
            }
            case NetherShopPurchaseCloseStage.AwaitingParent:
                return NetherNativeActionResult.Completed("shop-purchase-close-complete");
            case NetherShopPurchaseCloseStage.Faulted:
                return NetherNativeActionResult.BindingUnavailable(
                    _faultDetail.Length == 0 ? "shop-purchase-close-faulted" : _faultDetail
                );
            default:
                return NetherNativeActionResult.BindingUnavailable("shop-purchase-close-not-started");
        }
    }

    public bool IsOwner(NetherShopPurchaseCloseOwner owner) =>
        _owner is NetherShopPurchaseCloseOwner current && current == owner;

    public void Reset()
    {
        _owner = null;
        _faultDetail = string.Empty;
        _pendingPumps = 0;
        Stage = NetherShopPurchaseCloseStage.Idle;
    }

    private NetherNativeActionResult Fault(string phase, NetherNativeActionResult result)
    {
        Stage = NetherShopPurchaseCloseStage.Faulted;
        _faultDetail = phase + ":" + result.Kind + ":" + result.Detail;
        return NetherNativeActionResult.BindingUnavailable(_faultDetail);
    }
}
