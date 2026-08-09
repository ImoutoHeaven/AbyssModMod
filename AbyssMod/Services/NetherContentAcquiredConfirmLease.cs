#nullable enable

namespace AbyssMod.Services;

internal enum NetherContentAcquiredConfirmClaimKind
{
    None,
    Claimed,
    CorrelationMismatch,
    MissingClose,
}

internal readonly record struct NetherContentAcquiredConfirmClaim(
    NetherContentAcquiredConfirmClaimKind Kind,
    object? Close,
    long Sequence,
    string Detail
);

/// <summary>
/// Owns the one-shot close callback for NetherContentAcquiredPopup.  The popup is a child of
/// the still-running floor-event sequence, not a new floor action: closing it only releases the
/// native sequence, whose exact UniTask remains the settlement authority.
/// </summary>
internal sealed class NetherContentAcquiredConfirmLease
{
    private object? _popup;
    private object? _close;
    private long _sequence;
    private NetherActionKind _ownerAction;
    private long _ownerGeneration;
    private long _runtimeGeneration;
    private bool _claimed;

    public bool Register(
        object? popup,
        object? close,
        long sequence,
        NetherActionKind ownerAction,
        long ownerGeneration,
        long runtimeGeneration
    )
    {
        bool owned = ownerAction == NetherActionKind.SelectFloor && ownerGeneration > 0;
        bool recovered = ownerAction == NetherActionKind.None && ownerGeneration == 0;
        if (popup == null || sequence < 1 || runtimeGeneration < 1 || (!owned && !recovered))
            return false;

        _popup = popup;
        _close = close;
        _sequence = sequence;
        _ownerAction = ownerAction;
        _ownerGeneration = ownerGeneration;
        _runtimeGeneration = runtimeGeneration;
        _claimed = false;
        return true;
    }

    public NetherContentAcquiredConfirmClaim ClaimOwned(long ownerGeneration) =>
        Claim(
            _ownerAction == NetherActionKind.SelectFloor
                && _ownerGeneration == ownerGeneration
                && ownerGeneration > 0,
            "content-acquired-owned-popup-correlation-mismatch"
        );

    public NetherContentAcquiredConfirmClaim ClaimRecovered(long runtimeGeneration) =>
        Claim(
            _ownerAction == NetherActionKind.None
                && _ownerGeneration == 0
                && _runtimeGeneration == runtimeGeneration
                && runtimeGeneration > 0,
            "content-acquired-recovered-popup-correlation-mismatch"
        );

    public bool InvalidatePopup(object? popup)
    {
        if (popup == null || !ReferenceEquals(_popup, popup))
            return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _popup = null;
        _close = null;
        _sequence = 0;
        _ownerAction = NetherActionKind.None;
        _ownerGeneration = 0;
        _runtimeGeneration = 0;
        _claimed = false;
    }

    private NetherContentAcquiredConfirmClaim Claim(bool correlationMatches, string mismatchDetail)
    {
        if (_popup == null || _claimed)
            return new(NetherContentAcquiredConfirmClaimKind.None, null, 0, "no-content-acquired-confirm");
        if (!correlationMatches)
        {
            return new(
                NetherContentAcquiredConfirmClaimKind.CorrelationMismatch,
                null,
                _sequence,
                mismatchDetail
            );
        }

        _claimed = true;
        if (_close == null)
        {
            return new(
                NetherContentAcquiredConfirmClaimKind.MissingClose,
                null,
                _sequence,
                "content-acquired-popup-missing-close"
            );
        }

        return new(
            NetherContentAcquiredConfirmClaimKind.Claimed,
            _close,
            _sequence,
            "content-acquired-confirm-claimed"
        );
    }
}
