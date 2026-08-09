#nullable enable

using System;

namespace AbyssMod.Services;

internal readonly record struct NetherCodeTransformOwner(
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence,
    long ReplaceCodeId
)
{
    public bool IsValid => OwnerAction == NetherActionKind.SelectFloor
        && Generation > 0
        && Sequence > 0
        && ReplaceCodeId > 0;
}

internal enum NetherCodeTransformNativeStage
{
    Idle,
    AwaitingTaskRegistration,
    AwaitingConfirmPopup,
    AwaitingCompletePopup,
    AwaitingTaskTerminal,
    Completed,
    Faulted,
}

/// <summary>
/// Exact target_type=7 child sequence beneath one SelectFloor parent.  The list click starts a
/// generated UniTask (via Forget); that same task owns the confirm popup, server update, and
/// completion popup.  Missing stages are bounded and no mutation is ever replayed.
/// </summary>
internal sealed class NetherCodeTransformNativeFlow
{
    private readonly int _maximumPendingPumps;
    private int _pendingPumps;

    public NetherCodeTransformNativeFlow(int maximumPendingPumps = 600)
    {
        if (maximumPendingPumps < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingPumps));
        _maximumPendingPumps = maximumPendingPumps;
    }

    public NetherCodeTransformNativeStage Stage { get; private set; }
    public NetherCodeTransformOwner? Owner { get; private set; }
    public bool IsActive => Stage is NetherCodeTransformNativeStage.AwaitingTaskRegistration
        or NetherCodeTransformNativeStage.AwaitingConfirmPopup
        or NetherCodeTransformNativeStage.AwaitingCompletePopup
        or NetherCodeTransformNativeStage.AwaitingTaskTerminal;

    public bool Begin(NetherCodeTransformOwner owner)
    {
        if (!owner.IsValid || Stage != NetherCodeTransformNativeStage.Idle)
            return false;
        Owner = owner;
        Stage = NetherCodeTransformNativeStage.AwaitingTaskRegistration;
        _pendingPumps = 0;
        return true;
    }

    public bool ObserveTask(NetherCodeTransformOwner owner)
    {
        if (Stage != NetherCodeTransformNativeStage.AwaitingTaskRegistration
            || Owner is not NetherCodeTransformOwner expected
            || expected != owner)
        {
            return false;
        }
        Stage = NetherCodeTransformNativeStage.AwaitingConfirmPopup;
        _pendingPumps = 0;
        return true;
    }

    public NetherNativeActionResult Pump(
        Func<NetherNativeActionResult> confirm,
        Func<NetherNativeActionResult> closeComplete,
        Func<NetherNativeActionResult> pollTask
    )
    {
        if (confirm == null || closeComplete == null || pollTask == null)
            throw new ArgumentNullException();

        switch (Stage)
        {
            case NetherCodeTransformNativeStage.AwaitingTaskRegistration:
                return PendingOrFault("code-transform-task-registration-timeout");
            case NetherCodeTransformNativeStage.AwaitingConfirmPopup:
                return AdvancePopup(confirm(), NetherCodeTransformNativeStage.AwaitingCompletePopup,
                    "code-transform-confirm-timeout");
            case NetherCodeTransformNativeStage.AwaitingCompletePopup:
                return AdvancePopup(closeComplete(), NetherCodeTransformNativeStage.AwaitingTaskTerminal,
                    "code-transform-complete-timeout");
            case NetherCodeTransformNativeStage.AwaitingTaskTerminal:
            {
                NetherNativeActionResult task = pollTask();
                if (task.Kind == NetherNativeActionResultKind.Started)
                    return PendingOrFault("code-transform-task-terminal-timeout");
                if (task.Kind != NetherNativeActionResultKind.Completed)
                    return Fault(task.Detail);
                Stage = NetherCodeTransformNativeStage.Completed;
                _pendingPumps = 0;
                return NetherNativeActionResult.Completed("code-transform-native-flow-completed");
            }
            case NetherCodeTransformNativeStage.Completed:
                return NetherNativeActionResult.Completed("code-transform-native-flow-completed");
            case NetherCodeTransformNativeStage.Faulted:
                return NetherNativeActionResult.BindingUnavailable("code-transform-native-flow-faulted");
            default:
                return NetherNativeActionResult.BindingUnavailable("code-transform-native-flow-idle");
        }
    }

    public void Reset()
    {
        Stage = NetherCodeTransformNativeStage.Idle;
        Owner = null;
        _pendingPumps = 0;
    }

    private NetherNativeActionResult AdvancePopup(
        NetherNativeActionResult result,
        NetherCodeTransformNativeStage next,
        string timeout
    )
    {
        if (result.Kind == NetherNativeActionResultKind.Started)
            return PendingOrFault(timeout);
        if (result.Kind != NetherNativeActionResultKind.Completed)
            return Fault(result.Detail);
        Stage = next;
        _pendingPumps = 0;
        return NetherNativeActionResult.Started(result.Detail);
    }

    private NetherNativeActionResult PendingOrFault(string timeout)
    {
        _pendingPumps++;
        return _pendingPumps > _maximumPendingPumps
            ? Fault(timeout)
            : NetherNativeActionResult.Started(timeout.Replace("-timeout", "-pending", StringComparison.Ordinal));
    }

    private NetherNativeActionResult Fault(string detail)
    {
        Stage = NetherCodeTransformNativeStage.Faulted;
        return NetherNativeActionResult.BindingUnavailable(detail);
    }
}
