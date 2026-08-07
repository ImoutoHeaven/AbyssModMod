#nullable enable

namespace AbyssMod.Services;

internal sealed class NetherAutoClimbStateMachine
{
    private NetherPlannedAction? _pendingAction;
    private NetherSnapshotFingerprint? _preActionFingerprint;
    private NetherPlannedAction? _knownNotAppliedAction;
    private NetherSnapshotFingerprint? _knownNotAppliedFingerprint;

    public bool IsEnabled { get; private set; }
    public NetherAutoClimbPhase Phase { get; private set; } = NetherAutoClimbPhase.Disabled;
    public NetherPauseReason PauseReason { get; private set; } = NetherPauseReason.None;
    public string PauseDetail { get; private set; } = string.Empty;
    public NetherPlannedAction? PendingAction => _pendingAction;
    public NetherSnapshotFingerprint? PreActionFingerprint => _preActionFingerprint;

    public void Toggle(bool isInNether)
    {
        if (!isInNether)
        {
            IsEnabled = false;
            Phase = NetherAutoClimbPhase.Disabled;
            PauseReason = NetherPauseReason.NotInNether;
            PauseDetail = "not-in-nether";
            return;
        }

        if (IsEnabled)
        {
            IsEnabled = false;
            if (Phase != NetherAutoClimbPhase.Reconciling || _pendingAction == null)
            {
                Phase = NetherAutoClimbPhase.Disabled;
                PauseReason = NetherPauseReason.UserDisabled;
                PauseDetail = "user-disabled";
            }
            return;
        }

        IsEnabled = true;
        Phase = NetherAutoClimbPhase.Reconciling;
        PauseReason = NetherPauseReason.None;
        PauseDetail = string.Empty;
    }

    public void BeginReconcile()
    {
        if (IsEnabled || _pendingAction != null)
            Phase = NetherAutoClimbPhase.Reconciling;
    }

    public void ObserveStable(NetherSnapshotFingerprint fingerprint)
    {
        if (!IsEnabled && _pendingAction == null)
            return;

        if (fingerprint.Status == NetherSessionStatus.NotPlayed)
        {
            Pause(NetherPauseReason.NotPlayed, "not-played");
            return;
        }
        if (fingerprint.Status == NetherSessionStatus.Unknown)
        {
            Pause(NetherPauseReason.UnknownStatus, "unknown-status");
            return;
        }
        if (RequiresResultScene(fingerprint.Status))
        {
            _pendingAction = null;
            _preActionFingerprint = null;
            Phase = NetherAutoClimbPhase.AwaitingSceneChange;
            return;
        }

        if (_pendingAction == null)
            Phase = IsEnabled ? NetherAutoClimbPhase.Stable : NetherAutoClimbPhase.Disabled;
    }

    public bool TryBegin(NetherPlannedAction action, NetherSnapshotFingerprint fingerprint)
    {
        if (!IsEnabled || Phase != NetherAutoClimbPhase.Stable || action.Kind == NetherActionKind.None)
            return false;
        if (_knownNotAppliedAction == action && _knownNotAppliedFingerprint == fingerprint)
            return false;

        _pendingAction = action;
        _preActionFingerprint = fingerprint;
        PauseReason = NetherPauseReason.None;
        PauseDetail = string.Empty;
        Phase = action.Kind == NetherActionKind.AwaitNativeFlow
            ? NetherAutoClimbPhase.AwaitingBattle
            : NetherAutoClimbPhase.ExecutingNativeAction;
        return true;
    }

    public void ObserveActionResult(NetherSnapshotFingerprint fingerprint, NetherActionOutcome outcome)
    {
        if (_pendingAction == null || _preActionFingerprint == null)
            return;

        NetherPlannedAction action = _pendingAction.Value;
        NetherSnapshotFingerprint before = _preActionFingerprint.Value;
        if (outcome == NetherActionOutcome.Ambiguous)
        {
            Pause(NetherPauseReason.AmbiguousServerOutcome, "ambiguous-server-outcome");
            return;
        }
        if (outcome == NetherActionOutcome.Applied && fingerprint == before)
        {
            Pause(NetherPauseReason.AmbiguousServerOutcome, "applied-without-fingerprint-change");
            return;
        }

        _pendingAction = null;
        _preActionFingerprint = null;
        if (outcome == NetherActionOutcome.NotApplied)
        {
            _knownNotAppliedAction = action;
            _knownNotAppliedFingerprint = before;
        }
        else
        {
            _knownNotAppliedAction = null;
            _knownNotAppliedFingerprint = null;
        }

        ObserveStable(fingerprint);
    }

    public void ObserveUnknownOutcome()
    {
        if (_pendingAction != null)
            Phase = NetherAutoClimbPhase.Reconciling;
    }

    public void ObserveF11Busy(bool isBusy)
    {
        if (isBusy && Phase == NetherAutoClimbPhase.AwaitingBattle)
            Phase = NetherAutoClimbPhase.AwaitingF11;
        else if (!isBusy && Phase == NetherAutoClimbPhase.AwaitingF11)
            Phase = NetherAutoClimbPhase.AwaitingBattle;
    }

    public void Pause(NetherPauseReason reason, string detail)
    {
        Phase = NetherAutoClimbPhase.Paused;
        PauseReason = reason;
        PauseDetail = detail ?? string.Empty;
    }

    public bool Complete()
    {
        if (Phase != NetherAutoClimbPhase.AwaitingSceneChange)
            return false;

        _pendingAction = null;
        _preActionFingerprint = null;
        Phase = NetherAutoClimbPhase.Completed;
        return true;
    }

    private static bool RequiresResultScene(NetherSessionStatus status) =>
        status == NetherSessionStatus.Clear || status == NetherSessionStatus.Lose;
}
