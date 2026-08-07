#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Controller-facing production wiring for the Continue scene handoff.  It keeps policy/state
/// mutations outside the reflection bridge, so characterization tests execute the same state
/// transitions used by the Hotkey controller without requiring Unity or Harmony.
/// </summary>
internal sealed class NetherContinueSceneRuntimeCoordinator
{
    private readonly NetherAutoClimbStateMachine _state;
    private readonly NetherContinueSceneCoordinator _scene;

    public NetherContinueSceneRuntimeCoordinator(
        NetherAutoClimbStateMachine state,
        INetherContinueSceneDriver driver,
        int maximumMissingTicks = 600
    )
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _scene = new NetherContinueSceneCoordinator(
            driver ?? throw new ArgumentNullException(nameof(driver)),
            maximumMissingTicks
        );
    }

    public bool IsActive => _scene.IsActive;

    /// <summary>
    /// Must run after State.TryBegin but before the native controller callback is invoked, so
    /// the source owner generation and immutable server snapshot cannot be replaced mid-flight.
    /// </summary>
    public bool Begin(NetherPlannedAction action, NetherSnapshot before, long ownerGeneration)
    {
        if (action.Kind != NetherActionKind.Continue
            || _state.PendingAction?.Kind != NetherActionKind.Continue)
        {
            return false;
        }

        return _scene.Begin(
            new NetherContinueSceneContract(
                action.ExpectedMapId,
                action.ExpectedFloorId,
                action.ExpectedSegmentFloorLevel,
                action.TicketCost,
                NetherSessionStatus.Play
            ),
            before,
            ownerGeneration
        );
    }

    public NetherContinueSceneStep Pump()
    {
        NetherContinueSceneStep step = _scene.Pump();
        if (_scene.ParentTerminalObserved)
            _state.BeginContinueSceneHandoff();

        switch (step.Kind)
        {
            case NetherContinueSceneStepKind.Complete:
                if (step.Snapshot == null)
                {
                    _state.TerminatePendingAndPause(
                        NetherPauseReason.ContinueLifecycleFault,
                        "continue-handoff-complete-without-snapshot"
                    );
                    return NetherContinueSceneStep.Pause("continue-handoff-complete-without-snapshot");
                }
                _state.ObserveActionResult(step.Snapshot.Fingerprint, NetherActionOutcome.Applied);
                return step;
            case NetherContinueSceneStepKind.Pause:
                _state.TerminatePendingAndPause(MapPauseReason(step.Detail), step.Detail);
                return step;
            default:
                return step;
        }
    }

    public void Reset() => _scene.Reset();

    private static NetherPauseReason MapPauseReason(string detail)
    {
        if (detail.IndexOf("binding", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.BindingUnavailable;
        if (detail.IndexOf("parent-canceled", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.ContinueLifecycleCanceled;
        if (detail.IndexOf("teardown-timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.ContinueTeardownTimeout;
        if (detail.IndexOf("rebind-timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.ContinueRebindTimeout;
        if (detail.IndexOf("rebind-wrong", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.ContinueRebindWrongScene;
        if (detail.IndexOf("settlement-wrong", StringComparison.OrdinalIgnoreCase) >= 0)
            return NetherPauseReason.ContinueSettlementWrongTarget;
        return NetherPauseReason.ContinueLifecycleFault;
    }
}
