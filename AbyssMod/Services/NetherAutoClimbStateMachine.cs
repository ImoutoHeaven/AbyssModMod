#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Captures a validated immutable configuration only at a stable decision boundary.  A reload
/// during a native action is deliberately invisible until reconciliation reaches Stable, so a
/// request can never be started with one half of an old/new settings set.
/// </summary>
internal sealed class NetherAutoClimbSettingsSnapshotGate
{
    private NetherAutoClimbSettings? _lastStableSettings;

    public bool TryCapture(
        NetherAutoClimbSettings candidate,
        NetherAutoClimbPhase phase,
        out NetherAutoClimbSettings settings,
        out NetherPauseReason pauseReason,
        out string detail
    )
    {
        settings = _lastStableSettings ?? candidate;
        pauseReason = NetherPauseReason.None;
        detail = string.Empty;

        if (phase != NetherAutoClimbPhase.Stable)
        {
            if (_lastStableSettings != null)
            {
                settings = _lastStableSettings;
                return true;
            }

            pauseReason = NetherPauseReason.InvalidConfiguration;
            detail = "settings-capture-before-stable";
            return false;
        }

        if (!TryValidate(candidate, out detail))
        {
            pauseReason = NetherPauseReason.InvalidConfiguration;
            return false;
        }

        _lastStableSettings = candidate;
        settings = candidate;
        return true;
    }

    private static bool TryValidate(NetherAutoClimbSettings? candidate, out string detail)
    {
        if (candidate == null)
        {
            detail = "missing-settings";
            return false;
        }
        if (candidate.MaxDepth < 1)
        {
            detail = "invalid-max-depth";
            return false;
        }
        if (candidate.SoftErosionLimit is < 1 or >= 100)
        {
            detail = "invalid-soft-erosion-limit";
            return false;
        }
        if (candidate.MinimumCharacterHpPermille is < 1 or > 1000)
        {
            detail = "invalid-minimum-character-hp-permille";
            return false;
        }
        if (candidate.CodeReloadReserve < 0)
        {
            detail = "invalid-code-reload-reserve";
            return false;
        }
        if (!System.Enum.IsDefined(typeof(NetherCombatLane), candidate.CombatLane)
            || !System.Enum.IsDefined(typeof(NetherTreasureMode), candidate.TreasureMode)
            || !System.Enum.IsDefined(typeof(NetherShopMode), candidate.ShopMode))
        {
            detail = "invalid-nether-policy-enum";
            return false;
        }

        detail = string.Empty;
        return true;
    }
}

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
