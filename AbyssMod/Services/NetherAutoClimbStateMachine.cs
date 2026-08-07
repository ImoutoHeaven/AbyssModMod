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
    private NetherSnapshot? _preActionSnapshot;
    private NetherPlannedAction? _knownNotAppliedAction;
    private NetherSnapshotFingerprint? _knownNotAppliedFingerprint;

    public bool IsEnabled { get; private set; }
    public NetherAutoClimbPhase Phase { get; private set; } = NetherAutoClimbPhase.Disabled;
    public NetherPauseReason PauseReason { get; private set; } = NetherPauseReason.None;
    public string PauseDetail { get; private set; } = string.Empty;
    public NetherPlannedAction? PendingAction => _pendingAction;
    public NetherSnapshotFingerprint? PreActionFingerprint => _preActionFingerprint;
    public NetherSnapshot? PreActionSnapshot => _preActionSnapshot;

    public void Toggle(bool isInNether)
    {
        if (!isInNether)
        {
            // Result owns a scene-global task which normally outlives FloorSelection.  A
            // temporary lack of that floor controller must not erase the task or let an
            // off→on repeat start a fresh session before Result reaches a terminal state.
            if (HasDrainEvidence() && IsDrainPhase(Phase))
            {
                IsEnabled = false;
                return;
            }
            IsEnabled = false;
            Phase = NetherAutoClimbPhase.Disabled;
            PauseReason = NetherPauseReason.NotInNether;
            PauseDetail = "not-in-nether";
            return;
        }

        // An off→on key repeat must not replace an in-flight native operation with a fresh
        // reconciliation.  Keep F12 disabled until the existing controller task has reached
        // its terminal observation and the action-specific read-only reconcile is complete.
        if (!IsEnabled && HasDrainEvidence() && IsDrainPhase(Phase))
        {
            return;
        }

        if (IsEnabled)
        {
            IsEnabled = false;
            // A controller call can still be running when the user turns F12 off.  Do not
            // discard its identity: observe it to a terminal native result, then reconcile
            // once before allowing a later enable.  This prevents a second non-idempotent
            // request from being issued against an unknown first outcome.
            if (HasDrainEvidence() && IsDrainPhase(Phase))
            {
                // Preserve the exact pending phase and action evidence.  An off→on repeat
                // cannot replace a F11/battle/settlement/native/reconcile drain with a new
                // request before the existing outcome reaches its terminal observation.
            }
            else
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
        if (fingerprint.Status == NetherSessionStatus.Lose)
        {
            _pendingAction = null;
            _preActionFingerprint = null;
            _preActionSnapshot = null;
            Pause(NetherPauseReason.Lose, "lose-no-signal-auto-use");
            return;
        }
        if (RequiresResultScene(fingerprint.Status))
        {
            _pendingAction = null;
            _preActionFingerprint = null;
            _preActionSnapshot = null;
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
        Phase = action.Kind is NetherActionKind.AwaitNativeFlow or NetherActionKind.BattleSettlement
            ? NetherAutoClimbPhase.AwaitingBattle
            : NetherAutoClimbPhase.ExecutingNativeAction;
        return true;
    }

    public bool TryBegin(NetherPlannedAction action, NetherSnapshot snapshot)
    {
        if (snapshot == null)
            throw new System.ArgumentNullException(nameof(snapshot));
        if (!TryBegin(action, snapshot.Fingerprint))
            return false;
        _preActionSnapshot = snapshot;
        return true;
    }

    /// <summary>
    /// Enriches the pending SelectFloor settlement contract after an owned modal has been
    /// selected.  The native parent remains owned by RuntimeFlow; this method replaces only
    /// the immutable reconciliation copy and deliberately preserves the original pre-action
    /// snapshot/fingerprint.  A stale popup cannot replace a newer parent because every
    /// floor identity and the pre-status must still agree with the registered owner.
    /// </summary>
    public bool TryReplacePendingFloorTransaction(
        NetherPlannedAction ownerParent,
        NetherPlannedAction composed
    )
    {
        if (_pendingAction is not NetherPlannedAction pending
            || pending.Kind != NetherActionKind.SelectFloor
            || ownerParent.Kind != NetherActionKind.SelectFloor
            || composed.Kind != NetherActionKind.SelectFloor
            || pending.FloorId <= 0
            || pending.FloorId != ownerParent.FloorId
            || pending.FloorLevel != ownerParent.FloorLevel
            || pending.FloorIndex != ownerParent.FloorIndex
            || pending.ExpectedBeforeStatus != ownerParent.ExpectedBeforeStatus
            || composed.FloorId != ownerParent.FloorId
            || composed.FloorLevel != ownerParent.FloorLevel
            || composed.FloorIndex != ownerParent.FloorIndex
            || composed.ExpectedBeforeStatus != ownerParent.ExpectedBeforeStatus
            || composed.ExpectedAfterStatus == NetherSessionStatus.Unknown
            || composed.OwnedPopupKind == NetherRuntimePopupKind.None
            || composed.OwnedPopupActionKind == NetherActionKind.None)
        {
            return false;
        }

        _pendingAction = composed;
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
        _preActionSnapshot = null;
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

        // A disabled F12 still drains its one in-flight native chain.  Once that chain reaches
        // a terminal observation, it must become a safe Disabled boundary rather than retain a
        // stale handoff phase that could be mistaken for a re-enableable action.
        if (!IsEnabled)
        {
            Phase = NetherAutoClimbPhase.Disabled;
            PauseReason = NetherPauseReason.UserDisabled;
            PauseDetail = "user-disabled-after-drain";
            return;
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

    public bool BeginBattleSettlement()
    {
        if (_pendingAction?.Kind != NetherActionKind.BattleSettlement
            || Phase is not (NetherAutoClimbPhase.AwaitingBattle or NetherAutoClimbPhase.AwaitingF11))
        {
            return false;
        }

        Phase = NetherAutoClimbPhase.AwaitingBattleSettlement;
        return true;
    }

    /// <summary>
    /// Continue has a parent task whose terminal state intentionally tears the old
    /// FloorSelection down before a new NetherTop runtime is registered.  Preserve the pending
    /// action through that expected absence; it remains a drain phase until exact GET-only
    /// settlement succeeds or pauses.
    /// </summary>
    public bool BeginContinueSceneHandoff()
    {
        if (_pendingAction?.Kind != NetherActionKind.Continue
            || Phase is not (
                NetherAutoClimbPhase.ExecutingNativeAction
                or NetherAutoClimbPhase.AwaitingContinueSceneHandoff
            ))
        {
            return false;
        }

        Phase = NetherAutoClimbPhase.AwaitingContinueSceneHandoff;
        return true;
    }

    public void TerminatePendingAndPause(NetherPauseReason reason, string detail)
    {
        _pendingAction = null;
        _preActionFingerprint = null;
        _preActionSnapshot = null;
        _knownNotAppliedAction = null;
        _knownNotAppliedFingerprint = null;
        Pause(reason, detail);
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
        _preActionSnapshot = null;
        Phase = NetherAutoClimbPhase.Completed;
        return true;
    }

    private static bool RequiresResultScene(NetherSessionStatus status) =>
        status == NetherSessionStatus.Clear;

    private bool HasDrainEvidence() => _pendingAction != null
        || Phase is NetherAutoClimbPhase.AwaitingSceneChange or NetherAutoClimbPhase.AwaitingContinueSceneHandoff;

    private static bool IsDrainPhase(NetherAutoClimbPhase phase) => phase is
        NetherAutoClimbPhase.ExecutingNativeAction or
        NetherAutoClimbPhase.AwaitingContinueSceneHandoff or
        NetherAutoClimbPhase.Reconciling or
        NetherAutoClimbPhase.AwaitingF11 or
        NetherAutoClimbPhase.AwaitingBattle or
        NetherAutoClimbPhase.AwaitingBattleSettlement or
        NetherAutoClimbPhase.AwaitingSceneChange;
}
