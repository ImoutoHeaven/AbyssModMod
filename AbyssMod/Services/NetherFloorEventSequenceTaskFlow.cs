#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Tracks the two native tasks involved in selecting an interactive Nether floor.  The
/// click task only schedules a UniTask.Void movement callback; the exact
/// ExecuteCurrentFloorEventSequenceAsync task remains pending until the popup flow has
/// actually finished.  Reconciliation must therefore wait for both tasks in this order.
/// </summary>
internal sealed class NetherFloorEventSequenceTaskFlow
{
    private readonly NetherNativeWaitGate _clickTaskWait;
    private readonly NetherNativeWaitGate _eventSequenceTaskWait;
    private object? _clickTask;
    private object? _eventSequenceTask;
    private bool _clickCompleted;

    public NetherFloorEventSequenceTaskFlow(int maximumMissingPolls)
    {
        _clickTaskWait = new NetherNativeWaitGate(maximumMissingPolls);
        _eventSequenceTaskWait = new NetherNativeWaitGate(maximumMissingPolls);
    }

    public bool IsActive { get; private set; }

    public bool HasClickEvidence => IsActive && (_clickTask != null || _clickCompleted);

    public bool HasEventSequenceEvidence => IsActive && _eventSequenceTask != null;

    public bool Begin()
    {
        if (IsActive)
            return false;

        ResetCore();
        IsActive = true;
        return true;
    }

    /// <summary>
    /// Adopts an already-running exact sequence observed before F12 was enabled.  The game
    /// has already performed the floor click in this case, so polling starts at the sequence
    /// task and can never replay the click.
    /// </summary>
    public bool BeginRecovered(object? sequenceTask)
    {
        if (IsActive || sequenceTask == null)
            return false;

        ResetCore();
        IsActive = true;
        _clickCompleted = true;
        return ObserveEventSequenceTask(sequenceTask);
    }

    public bool ObserveClickTask(object? task)
    {
        if (!IsActive || task == null)
            return false;
        if (_clickTask != null)
            return ReferenceEquals(_clickTask, task);

        _clickTask = task;
        _clickTaskWait.ObserveRegistration();
        return true;
    }

    public bool ObserveEventSequenceTask(object? task)
    {
        if (!IsActive || task == null)
            return false;
        if (_eventSequenceTask != null)
            return ReferenceEquals(_eventSequenceTask, task);

        _eventSequenceTask = task;
        _eventSequenceTaskWait.ObserveRegistration();
        return true;
    }

    public NetherNativeActionResult Pump(Func<object, NetherNativeActionResult> pollTask)
    {
        if (pollTask == null)
            throw new ArgumentNullException(nameof(pollTask));
        if (!IsActive)
            return NetherNativeActionResult.BindingUnavailable("inactive-floor-event-sequence-flow");

        if (!_clickCompleted)
        {
            if (_clickTask == null)
                return AwaitOrReset(_clickTaskWait, "floor-click-parent");

            NetherNativeActionResult click = pollTask(_clickTask);
            if (click.Kind == NetherNativeActionResultKind.Started)
                return click;
            if (click.Kind != NetherNativeActionResultKind.Completed)
            {
                Reset();
                return click;
            }

            _clickCompleted = true;
            _clickTask = null;
        }

        if (_eventSequenceTask == null)
            return AwaitOrReset(_eventSequenceTaskWait, "floor-event-sequence");

        NetherNativeActionResult sequence = pollTask(_eventSequenceTask);
        if (sequence.Kind == NetherNativeActionResultKind.Started)
            return sequence;

        Reset();
        return sequence;
    }

    public void Reset()
    {
        ResetCore();
        IsActive = false;
    }

    private NetherNativeActionResult AwaitOrReset(NetherNativeWaitGate gate, string flow)
    {
        NetherNativeActionResult result = gate.AwaitRegistration(flow);
        if (result.Kind != NetherNativeActionResultKind.Started)
            Reset();
        return result;
    }

    private void ResetCore()
    {
        _clickTask = null;
        _eventSequenceTask = null;
        _clickCompleted = false;
        _clickTaskWait.Clear();
        _eventSequenceTaskWait.Clear();
    }
}
