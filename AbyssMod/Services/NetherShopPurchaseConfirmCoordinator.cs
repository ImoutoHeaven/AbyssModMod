#nullable enable

using System;

namespace AbyssMod.Services;

internal enum NetherShopPurchaseConfirmStage
{
    Idle,
    AwaitingPopup,
    Confirmed,
    Faulted,
}

/// <summary>
/// Owns the confirmation modal opened inside OnPurchaseContentAsync.  The purchase UniTask
/// cannot finish until this exact child popup invokes its native confirm callback, so task
/// polling alone deadlocks on the visual shop screen.  Missing-popup waits are bounded and a
/// successful callback advances before returning, which makes confirmation non-replayable.
/// </summary>
internal sealed class NetherShopPurchaseConfirmCoordinator
{
    private readonly int _maximumPendingPumps;
    private NetherShopPurchaseCloseOwner? _owner;
    private int _pendingPumps;
    private string _faultDetail = string.Empty;

    public NetherShopPurchaseConfirmCoordinator(int maximumPendingPumps = 600)
    {
        if (maximumPendingPumps < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingPumps));
        _maximumPendingPumps = maximumPendingPumps;
    }

    public NetherShopPurchaseConfirmStage Stage { get; private set; } =
        NetherShopPurchaseConfirmStage.Idle;

    public NetherShopPurchaseCloseOwner? Owner => _owner;

    public bool IsActive => Stage == NetherShopPurchaseConfirmStage.AwaitingPopup;

    public bool Begin(NetherShopPurchaseCloseOwner owner)
    {
        if (Stage != NetherShopPurchaseConfirmStage.Idle
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
        _pendingPumps = 0;
        _faultDetail = string.Empty;
        Stage = NetherShopPurchaseConfirmStage.AwaitingPopup;
        return true;
    }

    public NetherNativeActionResult Pump(Func<NetherNativeActionResult> tryInvokeConfirm)
    {
        if (tryInvokeConfirm == null)
            throw new ArgumentNullException(nameof(tryInvokeConfirm));

        switch (Stage)
        {
            case NetherShopPurchaseConfirmStage.AwaitingPopup:
            {
                NetherNativeActionResult result = tryInvokeConfirm();
                if (result.Kind == NetherNativeActionResultKind.Started)
                {
                    if (++_pendingPumps > _maximumPendingPumps)
                        return Fault("shop-purchase-confirm-timeout:pending-pump-limit");
                    return result;
                }
                if (result.Kind != NetherNativeActionResultKind.Completed)
                {
                    return Fault(
                        "shop-purchase-confirm:"
                            + result.Kind + ":" + result.Detail
                    );
                }

                Stage = NetherShopPurchaseConfirmStage.Confirmed;
                return NetherNativeActionResult.Completed("shop-purchase-confirm-complete");
            }
            case NetherShopPurchaseConfirmStage.Confirmed:
                return NetherNativeActionResult.Completed("shop-purchase-confirm-already-complete");
            case NetherShopPurchaseConfirmStage.Faulted:
                return NetherNativeActionResult.BindingUnavailable(_faultDetail);
            default:
                return NetherNativeActionResult.BindingUnavailable(
                    "shop-purchase-confirm-not-started"
                );
        }
    }

    public void Reset()
    {
        _owner = null;
        _pendingPumps = 0;
        _faultDetail = string.Empty;
        Stage = NetherShopPurchaseConfirmStage.Idle;
    }

    private NetherNativeActionResult Fault(string detail)
    {
        _faultDetail = detail;
        Stage = NetherShopPurchaseConfirmStage.Faulted;
        return NetherNativeActionResult.BindingUnavailable(detail);
    }
}
