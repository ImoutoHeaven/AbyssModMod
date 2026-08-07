#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// The narrow runtime seam shared by the real bridge and characterization tests.  It owns a
/// floor-click parent task separately from the modal callbacks it creates: a modal completion
/// cannot make the parent terminal, and a parent terminal cannot be observed while a stale
/// popup from an earlier generation is being considered.
/// </summary>
internal interface INetherRuntimeParentDriver
{
    NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent);

    NetherNativeActionResult PollFloorParent();
}

internal enum NetherRuntimeParentPollKind
{
    Idle,
    Pending,
    Completed,
    Faulted,
}

internal readonly record struct NetherRuntimeParentPollResult(NetherRuntimeParentPollKind Kind, string Detail)
{
    public static NetherRuntimeParentPollResult Pending(string detail) => new(NetherRuntimeParentPollKind.Pending, detail);
    public static NetherRuntimeParentPollResult Completed(string detail) => new(NetherRuntimeParentPollKind.Completed, detail);
    public static NetherRuntimeParentPollResult Faulted(string detail) => new(NetherRuntimeParentPollKind.Faulted, detail);
}

internal sealed class NetherRuntimeFlowCoordinator
{
    private readonly INetherRuntimeParentDriver _driver;
    private NetherPlannedAction? _parent;
    private long _generation;
    private long _lastDispatchedSequence;

    public NetherRuntimeFlowCoordinator(INetherRuntimeParentDriver driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public long Generation => _generation;

    public bool HasPendingParent => _parent != null;

    public bool BeginFloorParent(NetherPlannedAction action)
    {
        if (action.Kind != NetherActionKind.SelectFloor || _parent != null)
            return false;
        _parent = action;
        _generation = checked(_generation + 1);
        _lastDispatchedSequence = 0;
        return true;
    }

    public NetherRuntimeParentPollResult Poll(Func<NetherRuntimePopupContext, NetherNativeActionResult> dispatchOwnedPopup)
    {
        if (dispatchOwnedPopup == null)
            throw new ArgumentNullException(nameof(dispatchOwnedPopup));
        if (_parent == null)
            return new NetherRuntimeParentPollResult(NetherRuntimeParentPollKind.Idle, "no-floor-parent");

        bool dispatchedOwnedPopup = false;
        NetherRuntimePopupResult popup = _driver.TryGetOwnedPopup(_parent.Value);
        if (popup.IsSuccess)
        {
            NetherRuntimePopupContext context = popup.Popup!;
            if (context.OwnerAction == NetherActionKind.SelectFloor
                && context.OwnerGeneration == _generation
                && context.Sequence > _lastDispatchedSequence)
            {
                NetherNativeActionResult child = dispatchOwnedPopup(context);
                if (child.Kind == NetherNativeActionResultKind.Started || child.Kind == NetherNativeActionResultKind.Completed)
                {
                    _lastDispatchedSequence = context.Sequence;
                    dispatchedOwnedPopup = true;
                }
                else
                    return NetherRuntimeParentPollResult.Faulted("owned-popup:" + child.Detail);
            }
        }

        // A parent UniTask must be observed on a subsequent pump after an owned modal action
        // has been submitted.  This prohibits an Event/Treasure UniTask.Void click from being
        // mistaken for a synchronous floor completion by an eager/faulty adapter.
        if (dispatchedOwnedPopup)
            return NetherRuntimeParentPollResult.Pending("owned-popup-dispatched");

        NetherNativeActionResult parent = _driver.PollFloorParent();
        return parent.Kind switch
        {
            NetherNativeActionResultKind.Started => NetherRuntimeParentPollResult.Pending(parent.Detail),
            NetherNativeActionResultKind.Completed => Complete(parent.Detail),
            _ => Fail(parent.Detail),
        };
    }

    public void TerminateParent()
    {
        _parent = null;
        _lastDispatchedSequence = 0;
    }

    private NetherRuntimeParentPollResult Complete(string detail)
    {
        TerminateParent();
        return NetherRuntimeParentPollResult.Completed(detail);
    }

    private NetherRuntimeParentPollResult Fail(string detail)
    {
        TerminateParent();
        return NetherRuntimeParentPollResult.Faulted(detail);
    }
}
