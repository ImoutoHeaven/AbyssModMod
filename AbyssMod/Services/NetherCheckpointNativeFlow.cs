#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Models the native Sleep continuation UI sequence.  It is intentionally independent of the
/// bridge's reflection work so its ordering invariants are executable: no return list is read
/// until the native Continue (and optional one-ticket Boost) has generated it.
/// </summary>
internal enum NetherCheckpointNativeStage
{
    Idle,
    AwaitingContinuePopup,
    AwaitingBoostConfirmation,
    AwaitingPristineReturnPopup,
    AwaitingTerminalTask,
    Completed,
}

internal sealed class NetherCheckpointNativeFlow
{
    private NetherPlannedAction? _action;

    public NetherCheckpointNativeStage Stage { get; private set; } = NetherCheckpointNativeStage.Idle;

    public bool CanSubmitReturnSelection => Stage == NetherCheckpointNativeStage.AwaitingPristineReturnPopup;

    public bool Begin(NetherPlannedAction action)
    {
        if (action.Kind is not (NetherActionKind.Continue or NetherActionKind.FinishAtCheckpoint)
            || _action != null
            || Stage is not (NetherCheckpointNativeStage.Idle or NetherCheckpointNativeStage.Completed))
        {
            return false;
        }

        _action = action;
        Stage = NetherCheckpointNativeStage.AwaitingContinuePopup;
        return true;
    }

    public bool SubmitContinue(bool canBoost)
    {
        if (_action?.Kind != NetherActionKind.Continue || Stage != NetherCheckpointNativeStage.AwaitingContinuePopup)
            return false;
        Stage = canBoost
            ? NetherCheckpointNativeStage.AwaitingBoostConfirmation
            : _action.Value.ReturnLockReward > 0
                ? NetherCheckpointNativeStage.AwaitingPristineReturnPopup
                : NetherCheckpointNativeStage.AwaitingTerminalTask;
        return true;
    }

    public bool SubmitFinish()
    {
        if (_action?.Kind != NetherActionKind.FinishAtCheckpoint || Stage != NetherCheckpointNativeStage.AwaitingContinuePopup)
            return false;
        Stage = NetherCheckpointNativeStage.AwaitingTerminalTask;
        return true;
    }

    public bool SubmitBoostConfirmation()
    {
        if (_action?.Kind != NetherActionKind.Continue || Stage != NetherCheckpointNativeStage.AwaitingBoostConfirmation)
            return false;
        Stage = _action.Value.ReturnLockReward > 0
            ? NetherCheckpointNativeStage.AwaitingPristineReturnPopup
            : NetherCheckpointNativeStage.AwaitingTerminalTask;
        return true;
    }

    public bool SubmitReturnSelection()
    {
        if (_action?.Kind != NetherActionKind.Continue || Stage != NetherCheckpointNativeStage.AwaitingPristineReturnPopup)
            return false;
        Stage = NetherCheckpointNativeStage.AwaitingTerminalTask;
        return true;
    }

    public void Complete()
    {
        if (_action == null)
            return;
        _action = null;
        Stage = NetherCheckpointNativeStage.Completed;
    }

    public void Clear()
    {
        _action = null;
        Stage = NetherCheckpointNativeStage.Idle;
    }
}
