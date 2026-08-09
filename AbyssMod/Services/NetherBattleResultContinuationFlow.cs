#nullable enable

using System;

namespace AbyssMod.Services;

internal enum NetherBattleResultContinuationStepKind
{
    AwaitingView,
    AwaitingFloorRebind,
    Completed,
    CanceledBeforeInvoke,
    BindingUnavailable,
    Faulted,
}

internal readonly record struct NetherBattleResultContinuationStep(
    NetherBattleResultContinuationStepKind Kind,
    string Detail,
    NetherSnapshot? Snapshot = null
);

/// <summary>
/// Owns one post-battle Result view from ready-task through the exact native Next callback and
/// the strictly newer FloorSelection generation. It does not know reflection, Unity, or APIs.
/// </summary>
internal sealed class NetherBattleResultContinuationFlow
{
    private readonly NetherNativeWaitGate _viewWait;
    private readonly NetherNativeWaitGate _rebindWait;
    private object? _controller;
    private object? _initializeTask;
    private long _floorGenerationBeforeResult;
    private bool _nextInvoked;

    public NetherBattleResultContinuationFlow(
        int maximumMissingPolls = 600,
        int maximumRebindPolls = 600
    )
    {
        _viewWait = new NetherNativeWaitGate(maximumMissingPolls);
        _rebindWait = new NetherNativeWaitGate(maximumRebindPolls);
    }

    public bool HasObservation => _controller != null && _initializeTask != null;

    public bool NextInvoked => _nextInvoked;

    public void Observe(object? controller, object? initializeTask, long floorGenerationBeforeResult)
    {
        if (controller == null || initializeTask == null || floorGenerationBeforeResult < 0)
            return;

        // A second initialization of the same live view is a refresh, not a second Next owner.
        if (ReferenceEquals(_controller, controller) && _nextInvoked)
            return;

        _controller = controller;
        _initializeTask = initializeTask;
        _floorGenerationBeforeResult = floorGenerationBeforeResult;
        _nextInvoked = false;
        _viewWait.ObserveRegistration();
        _rebindWait.Clear();
    }

    public NetherBattleResultContinuationStep Pump(
        Func<object, NetherNativeActionResult> pollInitializeTask,
        Func<object, NetherNativeActionResult> invokeNext,
        bool hasFloorSelection,
        long currentFloorGeneration,
        bool allowInvoke
    )
    {
        if (pollInitializeTask == null)
            throw new ArgumentNullException(nameof(pollInitializeTask));
        if (invokeNext == null)
            throw new ArgumentNullException(nameof(invokeNext));

        if (_controller == null || _initializeTask == null)
        {
            NetherNativeActionResult wait = _viewWait.AwaitRegistration("battle-result-view");
            return wait.Kind == NetherNativeActionResultKind.Started
                ? new(NetherBattleResultContinuationStepKind.AwaitingView, wait.Detail)
                : new(NetherBattleResultContinuationStepKind.BindingUnavailable, wait.Detail);
        }

        if (!_nextInvoked)
        {
            if (!allowInvoke)
            {
                Reset();
                return new(
                    NetherBattleResultContinuationStepKind.CanceledBeforeInvoke,
                    "f12-disabled-before-battle-result-next"
                );
            }

            NetherNativeActionResult ready = pollInitializeTask(_initializeTask);
            if (ready.Kind == NetherNativeActionResultKind.Started)
                return new(NetherBattleResultContinuationStepKind.AwaitingView, ready.Detail);
            if (ready.Kind == NetherNativeActionResultKind.BindingUnavailable)
                return Terminate(NetherBattleResultContinuationStepKind.BindingUnavailable, ready.Detail);
            if (ready.Kind != NetherNativeActionResultKind.Completed)
                return Terminate(NetherBattleResultContinuationStepKind.Faulted, ready.Detail);

            NetherNativeActionResult invoked = invokeNext(_controller);
            if (invoked.Kind is not (
                    NetherNativeActionResultKind.Started
                    or NetherNativeActionResultKind.Completed
                ))
            {
                return Terminate(
                    invoked.Kind == NetherNativeActionResultKind.BindingUnavailable
                        ? NetherBattleResultContinuationStepKind.BindingUnavailable
                        : NetherBattleResultContinuationStepKind.Faulted,
                    invoked.Detail
                );
            }

            _nextInvoked = true;
            _rebindWait.Clear();
            return new(
                NetherBattleResultContinuationStepKind.AwaitingFloorRebind,
                invoked.Detail
            );
        }

        if (hasFloorSelection && currentFloorGeneration > _floorGenerationBeforeResult)
        {
            // The bridge still has to prove that this newly registered owner exposes a valid
            // current model. Keep the completed identity until that snapshot is captured and
            // the bridge explicitly resets this flow.
            return new(
                NetherBattleResultContinuationStepKind.Completed,
                "battle-result-floor-rebound"
            );
        }

        NetherNativeActionResult rebind = _rebindWait.AwaitRegistration("battle-result-floor-rebind");
        return rebind.Kind == NetherNativeActionResultKind.Started
            ? new(NetherBattleResultContinuationStepKind.AwaitingFloorRebind, rebind.Detail)
            : Terminate(NetherBattleResultContinuationStepKind.BindingUnavailable, rebind.Detail);
    }

    public void Reset()
    {
        _controller = null;
        _initializeTask = null;
        _floorGenerationBeforeResult = 0;
        _nextInvoked = false;
        _viewWait.Clear();
        _rebindWait.Clear();
    }

    private NetherBattleResultContinuationStep Terminate(
        NetherBattleResultContinuationStepKind kind,
        string detail
    )
    {
        Reset();
        return new(kind, detail ?? string.Empty);
    }
}
