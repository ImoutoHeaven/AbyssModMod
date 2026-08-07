#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Immutable identity of the exact owned CodeOffer cancel path.  The same visual popup can
/// survive RerollAsync, so the decision epoch is part of the owner contract as well as the
/// SelectFloor generation and popup sequence.
/// </summary>
internal readonly record struct NetherCodeKeepCancelOwner(
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence,
    long DecisionEpoch
);

internal enum NetherCodeKeepCancelStage
{
    Idle,
    AwaitingTaskRegistration,
    AwaitingTaskTerminal,
    Completed,
    Faulted,
}

/// <summary>
/// Bounded observer for the generated Keep/cancel sequence.  It never invokes the native
/// callback: the bridge calls the exact b__12_0 once, Harmony registers the generated
/// HandleCancelSequenceAsync UniTask only when its owner still matches, and this coordinator
/// waits that task before the original SelectFloor parent can be polled.
/// </summary>
internal sealed class NetherCodeKeepCancelCoordinator
{
    private readonly int _maximumPendingPumps;
    private NetherCodeKeepCancelOwner? _owner;
    private int _pendingPumps;
    private string _faultDetail = string.Empty;

    public NetherCodeKeepCancelCoordinator(int maximumPendingPumps = 600)
    {
        if (maximumPendingPumps < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingPumps));
        _maximumPendingPumps = maximumPendingPumps;
    }

    public NetherCodeKeepCancelStage Stage { get; private set; } = NetherCodeKeepCancelStage.Idle;

    public NetherCodeKeepCancelOwner? Owner => _owner;

    public bool IsActive => Stage is NetherCodeKeepCancelStage.AwaitingTaskRegistration
        or NetherCodeKeepCancelStage.AwaitingTaskTerminal;

    public bool Begin(NetherCodeKeepCancelOwner owner)
    {
        if (owner.OwnerAction != NetherActionKind.SelectFloor
            || owner.Generation <= 0
            || owner.Sequence <= 0
            || owner.DecisionEpoch < 0
            || Stage != NetherCodeKeepCancelStage.Idle)
        {
            return false;
        }

        _owner = owner;
        _pendingPumps = 0;
        _faultDetail = string.Empty;
        Stage = NetherCodeKeepCancelStage.AwaitingTaskRegistration;
        return true;
    }

    /// <summary>
    /// Accept only the Harmony-observed generated cancel task for the exact popup action that
    /// invoked b__12_0.  A player callback, old epoch, or out-of-order popup cannot satisfy a
    /// later Keep action.
    /// </summary>
    public bool ObserveTask(NetherCodeKeepCancelOwner owner)
    {
        if (Stage != NetherCodeKeepCancelStage.AwaitingTaskRegistration
            || _owner is not NetherCodeKeepCancelOwner expected
            || expected != owner)
        {
            return false;
        }

        _pendingPumps = 0;
        Stage = NetherCodeKeepCancelStage.AwaitingTaskTerminal;
        return true;
    }

    public NetherNativeActionResult Pump(Func<NetherNativeActionResult> pollTask)
    {
        if (pollTask == null)
            throw new ArgumentNullException(nameof(pollTask));

        switch (Stage)
        {
            case NetherCodeKeepCancelStage.AwaitingTaskRegistration:
                return PendingOrFault("code-keep-task-registration");
            case NetherCodeKeepCancelStage.AwaitingTaskTerminal:
            {
                NetherNativeActionResult result = pollTask();
                if (result.Kind == NetherNativeActionResultKind.Started)
                    return PendingOrFault("code-keep-task-terminal");
                if (result.Kind != NetherNativeActionResultKind.Completed)
                    return Fault("code-keep-task", result);

                Stage = NetherCodeKeepCancelStage.Completed;
                _pendingPumps = 0;
                return NetherNativeActionResult.Completed("code-keep-cancel-task-terminal");
            }
            case NetherCodeKeepCancelStage.Completed:
                return NetherNativeActionResult.Completed("code-keep-cancel-completed");
            case NetherCodeKeepCancelStage.Faulted:
                return NetherNativeActionResult.BindingUnavailable(
                    _faultDetail.Length == 0 ? "code-keep-cancel-faulted" : _faultDetail
                );
            default:
                return NetherNativeActionResult.BindingUnavailable("code-keep-cancel-not-started");
        }
    }

    public void Reset()
    {
        _owner = null;
        _pendingPumps = 0;
        _faultDetail = string.Empty;
        Stage = NetherCodeKeepCancelStage.Idle;
    }

    private NetherNativeActionResult PendingOrFault(string phase)
    {
        if (++_pendingPumps > _maximumPendingPumps)
        {
            return Fault(
                phase + "-timeout",
                NetherNativeActionResult.BindingUnavailable("pending-pump-limit")
            );
        }
        return NetherNativeActionResult.Started(phase + "-pending");
    }

    private NetherNativeActionResult Fault(string phase, NetherNativeActionResult result)
    {
        Stage = NetherCodeKeepCancelStage.Faulted;
        _faultDetail = phase + ":" + result.Kind + ":" + result.Detail;
        return NetherNativeActionResult.BindingUnavailable(_faultDetail);
    }
}
