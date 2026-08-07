#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Main-thread F12 coordinator.  Harmony patches only register observations with the bridge;
/// this class consumes them from Hotkey.Update and invokes at most one verified native UI flow
/// at a time.  It deliberately pauses when a current-version mapping cannot prove a safe
/// decision instead of substituting a datastore/API request.
/// </summary>
internal static class NetherAutoClimbController
{
    private static NetherAutoClimbStateMachine State = new();
    private static NetherAutoClimbSettingsSnapshotGate SettingsGate = new();
    private static readonly NetherCheckpointPolicy CheckpointPolicy = new();
    private static readonly NetherCodePolicy CodePolicy = new();
    private static readonly NetherAutoClimbRouteSafetyWiring RouteSafetyWiring = new();
    private static readonly NetherReturnItemPolicy ReturnItemPolicy = new();
    private static readonly NetherCheckpointReturnPreflight CheckpointReturnPreflight = new();
    private static NetherActionProjectionCalibration ProjectionCalibration = new();
    private static INetherRuntimeBridge _bridge = NetherRuntimeBridge.Instance;
    // This production coordinator is deliberately free of Unity/reflection dependencies.
    // The bridge remains the thin native adapter; characterization tests exercise the exact
    // owner/generation/parent-terminal transitions used below.
    private static NetherRuntimeFlowCoordinator RuntimeFlow = new(_bridge);
    private static NetherReadOnlyReconcileCoordinator ReadOnlyReconcileFlow = new(_bridge);
    private static NetherBattleSettlementCoordinator BattleSettlementFlow = new(_bridge, _bridge, _bridge);
    private static NetherContinueSceneRuntimeCoordinator ContinueSceneFlow = new(State, _bridge);
    private static readonly NetherDetailedAuditLogger DetailedAudit = new(message =>
        Logger.Info("[F12][NetherClimb] " + message)
    );
    private static NetherBattleSettingsLeaseControllerLifecycle BattleSettingsLifecycle = new(
        NetherBattleSettingsLease.Instance
    );

    private static bool _initialized;
    private static NetherCombatLane? _lockedCombatLane;
    private static NetherBattleProjectionPayload? _pendingBattleProjection;
    private static string _lastTransition = string.Empty;

    public static bool IsEnabled => State.IsEnabled;

    public static NetherAutoClimbPhase Phase => State.Phase;

    public static NetherPauseReason PauseReason => State.PauseReason;

    public static string PauseDetail => State.PauseDetail;

    /// <summary>
    /// Characterization seam for the production coordinator.  It swaps only the already
    /// abstract runtime boundary and rebuilds the coordinator-owned state machines; no test
    /// path can reach a raw Nether endpoint.  The shipped controller continues to initialize
    /// this field with the reflection-only <see cref="NetherRuntimeBridge"/> singleton.
    /// </summary>
    internal static IDisposable PushRuntimeBridgeForTests(INetherRuntimeBridge bridge)
        => PushRuntimeBridgeForTests(bridge, BattleSettingsLifecycle);

    /// <summary>
    /// Overload used by production characterization tests to compile the exact lease lifecycle
    /// branch with a deterministic native-accessor driver.  The runtime lifecycle object itself
    /// is unchanged production code; this avoids test reflection into controller fields.
    /// </summary>
    internal static IDisposable PushRuntimeBridgeForTests(
        INetherRuntimeBridge bridge,
        NetherBattleSettingsLeaseControllerLifecycle battleSettingsLifecycle
    )
    {
        if (bridge == null)
            throw new ArgumentNullException(nameof(bridge));
        if (battleSettingsLifecycle == null)
            throw new ArgumentNullException(nameof(battleSettingsLifecycle));

        var scope = new RuntimeBridgeTestScope(
            _bridge,
            State,
            SettingsGate,
            ProjectionCalibration,
            RuntimeFlow,
            ReadOnlyReconcileFlow,
            BattleSettlementFlow,
            ContinueSceneFlow,
            BattleSettingsLifecycle,
            _initialized,
            _lockedCombatLane,
            _pendingBattleProjection,
            _lastTransition
        );
        _bridge = bridge;
        State = new NetherAutoClimbStateMachine();
        SettingsGate = new NetherAutoClimbSettingsSnapshotGate();
        ProjectionCalibration = new NetherActionProjectionCalibration();
        RuntimeFlow = new NetherRuntimeFlowCoordinator(bridge);
        ReadOnlyReconcileFlow = new NetherReadOnlyReconcileCoordinator(bridge);
        BattleSettlementFlow = new NetherBattleSettlementCoordinator(bridge, bridge, bridge);
        ContinueSceneFlow = new NetherContinueSceneRuntimeCoordinator(State, bridge);
        BattleSettingsLifecycle = battleSettingsLifecycle;
        _initialized = false;
        _lockedCombatLane = null;
        _pendingBattleProjection = null;
        _lastTransition = string.Empty;
        return scope;
    }

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        NetherBattleSettingsLease.Initialize();
        // Persisted Auto/speed recovery intentionally waits for the exact BottomRight accessor
        // registration.  Calling RecoverOnLoad here would attempt the lease before the native
        // object exists and used to hide a later restore behind a session-wide completion bit.
        BattleSettingsLifecycle.OnControllerInitialized();
    }

    public static void Toggle()
    {
        Initialize();
        if (State.IsEnabled)
        {
            State.Toggle(isInNether: true);
            ObserveBattleSettingsLeaseBoundary(
                BattleSettingsLifecycle.OnF12Off(),
                "f12-disabled",
                pauseEnabledState: false
            );
            LogTransition("OFF user-disabled");
            return;
        }

        if (!_bridge.HasRegisteredFloorSelection)
        {
            State.Toggle(isInNether: false);
            LogTransition("OFF no-registered-nether-runtime");
            return;
        }

        NetherRuntimeSnapshotResult captured = _bridge.TryCaptureSnapshot();
        if (!captured.IsSuccess)
        {
            State.Toggle(isInNether: false);
            LogTransition("OFF snapshot-before-enable-failed:" + captured.Detail);
            return;
        }

        State.Toggle(isInNether: true);
        State.ObserveStable(captured.Snapshot!.Fingerprint);
        if (State.Phase != NetherAutoClimbPhase.Stable)
        {
            NetherAutoClimbPhase unavailablePhase = State.Phase;
            State.Toggle(isInNether: true);
            LogTransition("OFF not-at-stable-nether-boundary:" + unavailablePhase);
            return;
        }

        _lockedCombatLane = null;
        _pendingBattleProjection = null;
        LogTransition("ON maxDepth=" + BuildSettings().MaxDepth + " softErosion=" + BuildSettings().SoftErosionLimit + " lease=deferred-until-battle");
    }

    public static void Update()
    {
        if (!_initialized)
            return;

        PumpBattleSettingsLeaseRetry();

        if (State.Phase == NetherAutoClimbPhase.Completed)
            return;
        if (State.Phase == NetherAutoClimbPhase.Paused)
            return;

        // Finish naturally tears FloorSelection down before the separate Result scene
        // registers CreateNetherResultModelAsync.  Result is a global owner: observe its
        // bounded task before applying any FloorSelection availability gate.
        if (State.Phase == NetherAutoClimbPhase.AwaitingSceneChange)
        {
            ObserveResult();
            return;
        }

        // A native Sleep Continue intentionally tears down the old FloorSelection before the
        // new NetherTop segment registers.  Its coordinator owns the parent/teardown/rebind
        // evidence, so it must run before the ordinary "no floor controller" fail-closed gate.
        if (State.PendingAction?.Kind == NetherActionKind.Continue
            && State.Phase is (
                NetherAutoClimbPhase.ExecutingNativeAction
                or NetherAutoClimbPhase.AwaitingContinueSceneHandoff
            ))
        {
            ObserveContinueSceneHandoff();
            return;
        }

        bool disabledReconciliation = !State.IsEnabled
            && State.Phase is (
                NetherAutoClimbPhase.ExecutingNativeAction or
                NetherAutoClimbPhase.Reconciling or
                NetherAutoClimbPhase.AwaitingF11 or
                NetherAutoClimbPhase.AwaitingBattle or
                NetherAutoClimbPhase.AwaitingBattleSettlement or
                NetherAutoClimbPhase.AwaitingContinueSceneHandoff or
                NetherAutoClimbPhase.AwaitingSceneChange
            )
            && State.PendingAction != null;
        if (!State.IsEnabled && !disabledReconciliation)
            return;

        if (!_bridge.HasRegisteredFloorSelection)
        {
            if (State.PendingAction?.Kind == NetherActionKind.BattleSettlement)
            {
                FailClosedTerminal(NetherPauseReason.BattleSceneLost, "registered-nether-runtime-lost-during-battle-settlement");
                return;
            }
            if (State.IsEnabled)
                FailClosed(NetherPauseReason.NotInNether, "registered-nether-runtime-lost");
            return;
        }

        switch (State.Phase)
        {
            case NetherAutoClimbPhase.ExecutingNativeAction:
                if (State.PendingAction?.Kind == NetherActionKind.SelectFloor && RuntimeFlow.HasPendingParent)
                    PollFloorParentNativeAction();
                else
                    PollPendingNativeAction();
                return;
            case NetherAutoClimbPhase.Reconciling:
                Reconcile();
                return;
            case NetherAutoClimbPhase.AwaitingBattle:
            case NetherAutoClimbPhase.AwaitingF11:
            case NetherAutoClimbPhase.AwaitingBattleSettlement:
                ObserveBattle();
                return;
            case NetherAutoClimbPhase.Stable:
                PlanStableBoundary();
                return;
            default:
                return;
        }
    }

    public static void OnPluginUnload()
    {
        if (State.IsEnabled)
            State.Toggle(isInNether: true);
        ObserveBattleSettingsLeaseBoundary(
            BattleSettingsLifecycle.OnPluginUnload(),
            "plugin-unload",
            pauseEnabledState: false
        );
        _bridge.ClearRegistrations();
        RuntimeFlow.TerminateParent();
        ReadOnlyReconcileFlow.Reset();
        BattleSettlementFlow.TerminateForSceneLoss();
        ContinueSceneFlow.Reset();
        _initialized = false;
        _lockedCombatLane = null;
        _pendingBattleProjection = null;
        ProjectionCalibration.Clear();
        _lastTransition = string.Empty;
    }

    /// <summary>
    /// Called only after the exact BottomRightView.ApplyUserSettings patch has constructed an
    /// accessor.  This is the first safe point for persisted lease recovery.
    /// </summary>
    internal static void OnBattleSettingsAccessorRegistered()
    {
        if (!_initialized)
            return;
        ObserveBattleSettingsLeaseBoundary(
            BattleSettingsLifecycle.OnExactAccessorRegistered(),
            "native-accessor-registered",
            pauseEnabledState: State.IsEnabled
        );
    }

    /// <summary>
    /// The owner has been destroyed.  Keep any persisted original values on disk, but block a
    /// later route/battle until an exact replacement accessor can read them back.
    /// </summary>
    internal static void OnBattleSettingsAccessorUnregistered()
    {
        if (!_initialized)
            return;
        BattleSettingsLifecycle.OnExactAccessorUnregistered();
        if (State.IsEnabled)
        {
            PauseForBattleSettingsLease(
                NetherNativeActionResult.BindingUnavailable("native-battle-settings-accessor-owner-destroyed"),
                "native-accessor-unregistered"
            );
        }
    }

    /// <summary>
    /// FloorSelection is the native Nether scene owner.  An exact owner termination is a
    /// settings-restore boundary even when Result or Continue retains separate task evidence.
    /// </summary>
    internal static void OnNetherFloorSelectionTerminated()
    {
        if (!_initialized)
            return;
        ObserveBattleSettingsLeaseBoundary(
            BattleSettingsLifecycle.OnLeaveNether(),
            "floor-selection-terminated",
            pauseEnabledState: State.IsEnabled
        );
    }

    private static void PollPendingNativeAction()
    {
        NetherNativeActionResult native = _bridge.PollNativeFlow();
        Audit(
            NetherDetailedAuditKind.Task,
            "native-flow:" + State.Phase + ":" + native.Kind + ":" + native.Detail,
            new NetherDetailedAuditField("phase", State.Phase.ToString()),
            new NetherDetailedAuditField("terminal", native.Kind.ToString()),
            new NetherDetailedAuditField("detail", native.Detail)
        );
        switch (native.Kind)
        {
            case NetherNativeActionResultKind.Started:
                return;
            case NetherNativeActionResultKind.Completed:
                // Most native controller methods return UniTask but do not expose that task to
                // Harmony.  Reconcile rather than guessing that the server mutation succeeded.
                State.ObserveUnknownOutcome();
                return;
            case NetherNativeActionResultKind.UnknownOutcome:
                State.ObserveUnknownOutcome();
                return;
            case NetherNativeActionResultKind.BindingUnavailable:
                FailClosed(NetherPauseReason.BindingUnavailable, native.Detail);
                return;
            default:
                FailClosed(NetherPauseReason.AmbiguousServerOutcome, native.Detail);
                return;
        }
    }

    private static void PollFloorParentNativeAction()
    {
        NetherPlannedAction? pending = State.PendingAction;
        if (pending is not NetherPlannedAction parent || parent.Kind != NetherActionKind.SelectFloor)
        {
            FailClosed(NetherPauseReason.BindingUnavailable, "missing-floor-parent-state");
            return;
        }

        NetherRuntimeParentPollResult result = RuntimeFlow.Poll(
            popup => DispatchOwnedFloorPopup(parent, popup)
        );
        Audit(
            NetherDetailedAuditKind.Task,
            "floor-parent:" + parent.FloorId + ":" + result.Kind + ":" + result.Detail,
            new NetherDetailedAuditField("parent", parent.Kind.ToString()),
            new NetherDetailedAuditField("floorId", parent.FloorId.ToString()),
            new NetherDetailedAuditField("terminal", result.Kind.ToString()),
            new NetherDetailedAuditField("detail", result.Detail)
        );
        switch (result.Kind)
        {
            case NetherRuntimeParentPollKind.Pending:
                return;
            case NetherRuntimeParentPollKind.Completed:
                _bridge.TerminateFloorParent();
                // The parent task is the only proof that Event/Treasure's internal void flow
                // has reached its native terminal.  Reconcile before making another decision.
                State.ObserveUnknownOutcome();
                return;
            case NetherRuntimeParentPollKind.Faulted:
                _bridge.TerminateFloorParent();
                State.ObserveUnknownOutcome();
                return;
            default:
                FailClosed(NetherPauseReason.BindingUnavailable, "floor-parent-poll:" + result.Detail);
                return;
        }
    }

    private static NetherNativeActionResult DispatchOwnedFloorPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup
    )
    {
        NetherRuntimeSnapshotResult captured = _bridge.TryCaptureSnapshot();
        if (!captured.IsSuccess)
            return NetherNativeActionResult.BindingUnavailable("owned-popup-snapshot:" + captured.Detail);
        if (!SettingsGate.TryCapture(
                BuildSettings(),
                State.Phase,
                out NetherAutoClimbSettings settings,
                out NetherPauseReason settingsReason,
                out string settingsDetail
            ))
        {
            return NetherNativeActionResult.BindingUnavailable(
                "owned-popup-settings:" + settingsReason + ":" + settingsDetail
            );
        }

        NetherSnapshot snapshot = captured.Snapshot!;
        NetherPopupDispatchDecision decision = NetherPopupDispatchPolicy.Decide(snapshot, popup, settings);
        if (decision.Kind == NetherPopupDispatchKind.Code)
            return DispatchOwnedCodePopup(parent, popup, snapshot, settings);
        if (decision.Kind == NetherPopupDispatchKind.AwaitNativeFlow)
            return NetherNativeActionResult.Started("owned-popup-await-native-flow");
        if (decision.Kind != NetherPopupDispatchKind.NativeAction)
            return NetherNativeActionResult.BindingUnavailable(
                "owned-popup-policy:" + decision.PauseReason + ":" + decision.Detail
            );

        NetherNativeActionResult native = _bridge.InvokeOwnedPopup(parent, popup, decision.Action);
        if (native.Kind is NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed)
        {
            if (decision.HasEffectProjection)
            {
                ProjectionCalibration.Expect(new NetherEventDecision
                {
                    Kind = NetherEventDecisionKind.Select,
                    ProjectedErosion = decision.ProjectedErosion,
                    HpDelta = decision.HpDelta,
                }, snapshot);
            }
            LogAction(
                "owned-popup:" + popup.Kind + ":" + decision.Action.Kind,
                snapshot,
                "owner=" + popup.OwnerAction + ":" + popup.OwnerGeneration + ":" + popup.Sequence
            );
        }
        return native;
    }

    private static NetherNativeActionResult DispatchOwnedCodePopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings
    )
    {
        NetherRuntimeCodeCandidatesResult candidates = _bridge.TryGetCodeCandidates();
        if (!candidates.IsSuccess)
            return NetherNativeActionResult.BindingUnavailable("owned-code-candidates:" + candidates.Detail);

        NetherCodeDecision decision = CodePolicy.Decide(
            new NetherCodePortfolio
            {
                CurrentCodes = snapshot.Codes,
                Capacity = snapshot.CodeCapacity,
                ReloadCount = snapshot.CodeReloadCount,
                IsMasterComplete = candidates.IsMasterComplete,
                LockedLane = _lockedCombatLane,
            },
            candidates.Candidates,
            settings
        );
        if (decision.Kind == NetherCodeDecisionKind.Pause)
            return NetherNativeActionResult.BindingUnavailable("owned-code-policy:" + decision.PauseReason + ":" + decision.Detail);
        if (decision.Kind == NetherCodeDecisionKind.Keep)
            return NetherNativeActionResult.BindingUnavailable("owned-code-policy-keep:" + decision.Detail);

        _lockedCombatLane = decision.LockedLane;
        NetherPlannedAction action = decision.Kind == NetherCodeDecisionKind.Reload
            ? new NetherPlannedAction(NetherActionKind.ReloadCode)
            : new NetherPlannedAction(NetherActionKind.SelectCode)
            {
                CodeId = decision.SelectedCodeId,
                ReplaceCodeId = decision.RemoveCodeId,
            };
        return _bridge.InvokeOwnedPopup(parent, popup, action);
    }

    private static void Reconcile()
    {
        NetherReadOnlyReconcileStep refresh = ReadOnlyReconcileFlow.Pump();
        Audit(
            NetherDetailedAuditKind.Reconcile,
            "refresh:" + refresh.Kind + ":" + refresh.Detail,
            new NetherDetailedAuditField("state", refresh.Kind.ToString()),
            new NetherDetailedAuditField("detail", refresh.Detail)
        );
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.Pending)
            return;
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.BindingUnavailable)
        {
            FailClosed(NetherPauseReason.BindingUnavailable, refresh.Detail);
            return;
        }
        if (refresh.Kind != NetherReadOnlyReconcileStepKind.Applied || refresh.Snapshot == null)
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, refresh.Detail);
            return;
        }

        NetherSnapshot snapshot = refresh.Snapshot;
        AuditSnapshot(snapshot, "reconcile");
        NetherProjectionObservation projection = ProjectionCalibration.Observe(snapshot);
        Audit(
            NetherDetailedAuditKind.Battle,
            "calibration:" + projection.IsDrift + ":" + projection.RequiresRebaseline + ":" + projection.Detail,
            new NetherDetailedAuditField("drift", projection.IsDrift.ToString()),
            new NetherDetailedAuditField("rebaseline", projection.RequiresRebaseline.ToString()),
            new NetherDetailedAuditField("detail", projection.Detail)
        );
        if (projection.IsDrift)
        {
            FailClosed(projection.PauseReason, projection.Detail);
            return;
        }
        if (projection.RequiresRebaseline)
            LogAction("projection-rebaseline", snapshot, projection.Detail);

        if (State.PendingAction == null || State.PreActionFingerprint == null)
        {
            State.ObserveStable(snapshot.Fingerprint);
            return;
        }

        // A same fingerprint cannot prove that the original controller did nothing: visual
        // close-only actions and a delayed response are indistinguishable here.  Pause rather
        // than replaying or marking it NotApplied.
        NetherActionOutcome outcome = NetherActionReconcilePolicy.Evaluate(
            State.PendingAction.Value,
            State.PreActionSnapshot ?? BuildSnapshotFromFingerprint(State.PreActionFingerprint.Value),
            snapshot
        );
        Audit(
            NetherDetailedAuditKind.Reconcile,
            "classification:" + State.PendingAction.Value.Kind + ":" + outcome + ":" + snapshot.Fingerprint,
            new NetherDetailedAuditField("action", State.PendingAction.Value.Kind.ToString()),
            new NetherDetailedAuditField("classification", outcome.ToString()),
            new NetherDetailedAuditField("floorId", snapshot.CurrentFloorId.ToString())
        );
        State.ObserveActionResult(snapshot.Fingerprint, outcome);
        if (State.Phase == NetherAutoClimbPhase.Paused)
        {
            ObserveBattleSettingsLeaseBoundary(
                BattleSettingsLifecycle.OnAutomationPause(),
                "ambiguous-reconcile",
                pauseEnabledState: false
            );
        }
    }

    private static void ObserveBattle()
    {
        if (State.PendingAction?.Kind != NetherActionKind.BattleSettlement)
        {
            FailClosedTerminal(NetherPauseReason.BattleLifecycleFault, "missing-battle-settlement-pending-action");
            return;
        }

        NetherBattleSettlementStep step = BattleSettlementFlow.Pump();
        Audit(
            NetherDetailedAuditKind.Battle,
            "settlement:" + step.Kind + ":" + step.Detail,
            new NetherDetailedAuditField("step", step.Kind.ToString()),
            new NetherDetailedAuditField("detail", step.Detail),
            new NetherDetailedAuditField("pending", State.PendingAction?.Kind.ToString() ?? "none")
        );
        switch (step.Kind)
        {
            case NetherBattleSettlementStepKind.AwaitingF11:
                Audit(
                    NetherDetailedAuditKind.F11,
                    "battle-f11-blocked",
                    new NetherDetailedAuditField("blocked", "true"),
                    new NetherDetailedAuditField("phase", State.Phase.ToString())
                );
                State.ObserveF11Busy(isBusy: true);
                return;
            case NetherBattleSettlementStepKind.AwaitingBattle:
                State.ObserveF11Busy(isBusy: false);
                if (State.IsEnabled && !EnsureBattleLease())
                    return;
                return;
            case NetherBattleSettlementStepKind.AwaitingSettlement:
                State.ObserveF11Busy(isBusy: false);
                // The coordinator returns AwaitingSettlement both for the clear/close edge and
                // for each subsequent GET-only poll.  Enter the state and restore the lease on
                // that edge exactly once; treating a pending refresh as a second transition
                // would turn an otherwise valid server wait into a terminal pause.
                bool enteringSettlement = State.Phase != NetherAutoClimbPhase.AwaitingBattleSettlement;
                if (enteringSettlement && !State.BeginBattleSettlement())
                {
                    FailClosedTerminal(NetherPauseReason.BattleLifecycleFault, "could-not-enter-battle-settlement");
                    return;
                }
                if (enteringSettlement
                    && !ObserveBattleSettingsLeaseBoundary(
                        BattleSettingsLifecycle.OnBattleClearOrClose(),
                        "battle-clear-or-close",
                        pauseEnabledState: State.IsEnabled
                    ))
                {
                    return;
                }
                return;
            case NetherBattleSettlementStepKind.Settled:
                if (step.Snapshot == null)
                {
                    FailClosedTerminal(NetherPauseReason.BattleSettlementWrongTarget, "battle-settlement-missing-authoritative-snapshot");
                    return;
                }
                State.ObserveActionResult(step.Snapshot.Fingerprint, NetherActionOutcome.Applied);
                return;
            case NetherBattleSettlementStepKind.Unchanged:
                FailClosedTerminal(NetherPauseReason.BattleSettlementUnchanged, "battle-settlement-unchanged:" + step.Detail);
                return;
            case NetherBattleSettlementStepKind.WrongTarget:
                FailClosedTerminal(NetherPauseReason.BattleSettlementWrongTarget, "battle-settlement-wrong-target:" + step.Detail);
                return;
            case NetherBattleSettlementStepKind.ProjectionUnknown:
                FailClosedTerminal(
                    step.PauseReason == NetherPauseReason.None ? NetherPauseReason.BattleProjectionUnknown : step.PauseReason,
                    "battle-projection-unknown:" + step.Detail
                );
                return;
            case NetherBattleSettlementStepKind.ProjectionDrift:
                FailClosedTerminal(
                    step.PauseReason == NetherPauseReason.None ? NetherPauseReason.BattleProjectionDrift : step.PauseReason,
                    "battle-projection-drift:" + step.Detail
                );
                return;
            case NetherBattleSettlementStepKind.Canceled:
                FailClosedTerminal(NetherPauseReason.BattleLifecycleCanceled, "battle-lifecycle-canceled:" + step.Detail);
                return;
            case NetherBattleSettlementStepKind.SceneLost:
                FailClosedTerminal(NetherPauseReason.BattleSceneLost, step.Detail);
                return;
            case NetherBattleSettlementStepKind.BindingUnavailable:
                FailClosedTerminal(NetherPauseReason.BindingUnavailable, "battle-lifecycle-binding:" + step.Detail);
                return;
            default:
                FailClosedTerminal(NetherPauseReason.BattleLifecycleFault, "battle-lifecycle-fault:" + step.Detail);
                return;
        }
    }

    private static void ObserveResult()
    {
        NetherNativeActionResult result = _bridge.PollResultFlow();
        if (result.Kind == NetherNativeActionResultKind.Started)
            return;
        if (result.Kind == NetherNativeActionResultKind.Completed)
        {
            if (State.Complete())
            {
                ObserveBattleSettingsLeaseBoundary(
                    BattleSettingsLifecycle.OnLeaveNether(),
                    "nether-result-complete",
                    pauseEnabledState: false
                );
                LogTransition("COMPLETED native-result-succeeded");
            }
            return;
        }

        // Result failure/cancellation is never an infinitely pending scene transition.  Keep
        // player control and require an explicit fresh observation/recovery rather than
        // issuing another Result request from F12.
        FailClosed(
            result.Kind == NetherNativeActionResultKind.BindingUnavailable
                ? NetherPauseReason.BindingUnavailable
                : result.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0
                    ? NetherPauseReason.ResultLifecycleCanceled
                    : NetherPauseReason.ResultLifecycleFault,
            "native-result:" + result.Detail
        );
    }

    private static void ObserveContinueSceneHandoff()
    {
        NetherContinueSceneStep step = ContinueSceneFlow.Pump();
        switch (step.Kind)
        {
            case NetherContinueSceneStepKind.WaitForTeardown:
            case NetherContinueSceneStepKind.WaitForRebind:
            case NetherContinueSceneStepKind.Reconcile:
                return;
            case NetherContinueSceneStepKind.Complete:
                if (step.Snapshot != null)
                    LogAction("continue-handoff-complete", step.Snapshot, step.Detail);
                return;
            case NetherContinueSceneStepKind.Pause:
                // The runtime seam has already terminally cleared the Continue pending action
                // with an action-specific reason.  Preserve that reason rather than turning an
                // expected scene handoff into generic "not in Nether".
                FailClosed(
                    State.PauseReason == NetherPauseReason.None
                        ? NetherPauseReason.ContinueLifecycleFault
                        : State.PauseReason,
                    "continue-handoff:" + step.Detail
                );
                return;
            default:
                FailClosed(NetherPauseReason.ContinueLifecycleFault, "continue-handoff-invalid-step:" + step.Kind);
                return;
        }
    }

    private static void PlanStableBoundary()
    {
        NetherRuntimeSnapshotResult captured = _bridge.TryCaptureSnapshot();
        if (!captured.IsSuccess)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "stable-snapshot:" + captured.Detail);
            return;
        }

        NetherSnapshot snapshot = captured.Snapshot!;
        AuditSnapshot(snapshot, "stable");
        State.ObserveStable(snapshot.Fingerprint);
        if (State.Phase != NetherAutoClimbPhase.Stable)
            return;

        if (!EnsureBattleSettingsLifecycleReady("stable-route-boundary"))
            return;

        if (!SettingsGate.TryCapture(
                BuildSettings(),
                State.Phase,
                out NetherAutoClimbSettings settings,
                out NetherPauseReason settingsReason,
                out string settingsDetail
            ))
        {
            FailClosed(settingsReason, settingsDetail);
            return;
        }

        NetherCheckpointDecision checkpoint = CheckpointPolicy.Decide(snapshot, settings);
        switch (checkpoint.Kind)
        {
            case NetherCheckpointDecisionKind.Pause:
            case NetherCheckpointDecisionKind.PauseAtNonCheckpointTarget:
                FailClosed(checkpoint.PauseReason, checkpoint.Detail);
                return;
            case NetherCheckpointDecisionKind.AwaitResult:
                State.ObserveStable(snapshot.Fingerprint);
                return;
            case NetherCheckpointDecisionKind.ContinueOneTicket:
            case NetherCheckpointDecisionKind.FinishNormally:
                PlanCheckpoint(snapshot, checkpoint);
                return;
        }

        if (snapshot.Status == NetherSessionStatus.Battle || _bridge.IsBattleActive)
        {
            BeginBattleWait(snapshot);
            return;
        }

        // Wait is the server's modal state.  Choosing code from a stale owned-code list or
        // guessing a close action before its controller has registered would be unsafe.
        if (snapshot.Status == NetherSessionStatus.Wait)
        {
            NetherRuntimePopupResult popup = _bridge.TryGetActivePopup();
            if (popup.IsSuccess)
            {
                PlanNativePopup(snapshot, settings, popup.Popup!);
                return;
            }
            FailClosed(NetherPauseReason.UnsupportedPopup, "wait-popup:" + popup.Detail);
            return;
        }

        if (snapshot.Status == NetherSessionStatus.Play)
        {
            PlanRoute(snapshot, settings, checkpoint.EffectiveMaxDepth);
            return;
        }

        FailClosed(NetherPauseReason.UnknownStatus, "unhandled-stable-status:" + snapshot.Status);
    }

    private static void PlanNativePopup(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings,
        NetherRuntimePopupContext popup
    )
    {
        Audit(
            NetherDetailedAuditKind.Interactive,
            "popup:" + popup.Kind + ":" + popup.OwnerGeneration + ":" + popup.Sequence,
            new NetherDetailedAuditField("kind", popup.Kind.ToString()),
            new NetherDetailedAuditField("owner", popup.OwnerAction.ToString()),
            new NetherDetailedAuditField("generation", popup.OwnerGeneration.ToString()),
            new NetherDetailedAuditField("sequence", popup.Sequence.ToString()),
            new NetherDetailedAuditField("floorId", snapshot.CurrentFloorId.ToString())
        );
        NetherPopupDispatchDecision decision = NetherPopupDispatchPolicy.Decide(snapshot, popup, settings);
        switch (decision.Kind)
        {
            case NetherPopupDispatchKind.Code:
                PlanCodeSelection(snapshot, settings);
                return;
            case NetherPopupDispatchKind.NativeAction:
                ExecuteNativeAction(
                    snapshot,
                    decision.Action,
                    "popup:" + popup.Kind + ":" + decision.Detail,
                    decision.HasEffectProjection
                        ? native =>
                        {
                            if (native.Kind is NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed)
                            {
                                ProjectionCalibration.Expect(new NetherEventDecision
                                {
                                    Kind = NetherEventDecisionKind.Select,
                                    ProjectedErosion = decision.ProjectedErosion,
                                    HpDelta = decision.HpDelta,
                                }, snapshot);
                            }
                        }
                        : null
                );
                return;
            case NetherPopupDispatchKind.AwaitNativeFlow:
                // Continue/return popups are driven by the already registered native
                // checkpoint sequence.  Do not synthesize a close or a raw API request.
                LogAction("popup-await:" + popup.Kind, snapshot, decision.Detail);
                return;
            default:
                FailClosed(decision.PauseReason, "popup:" + popup.Kind + ":" + decision.Detail);
                return;
        }
    }

    private static void PlanCheckpoint(NetherSnapshot snapshot, NetherCheckpointDecision checkpoint)
    {
        if (!NetherPreserveItemIdParser.TryParse(
                Config.BattleSessionAutoSLNetherPreserveItemIds.Value,
                out HashSet<long> preserveIds,
                out string preserveError
            ))
        {
            FailClosed(NetherPauseReason.InvalidConfiguration, "nether-preserve-item-ids:" + preserveError);
            return;
        }

        // Native HandleGameClearedIfNeededAsync reads LockReward before any
        // RequestNetherContinueAsync.  A positive value creates the return popup, whose fresh
        // ContentModel list must match the live datastore preflight before OnConfirmAsync;
        // Finish transitions directly to result and carries no return selection information.

        NetherPlannedAction action;
        if (checkpoint.Kind == NetherCheckpointDecisionKind.ContinueOneTicket)
        {
            NetherContinuationTarget? target = snapshot.ContinuationTarget;
            if (target == null)
            {
                FailClosed(NetherPauseReason.UnknownMasterData, "continue-target-unavailable-before-native-mutation");
                return;
            }

            action = new NetherPlannedAction(NetherActionKind.Continue)
            {
                TicketCount = checkpoint.TicketCount,
                TicketCost = 1,
                ExpectedMapId = target.MapId,
                ExpectedFloorId = target.FloorId,
                ExpectedSegmentFloorLevel = target.SegmentFloorLevel,
                ReturnLockReward = snapshot.LockReward,
                ReturnPreserveItemIds = preserveIds.OrderBy(itemId => itemId).ToArray(),
            };
        }
        else
        {
            // Finish is deliberately not a Continue handoff: it reaches the independent
            // Result owner/scene path and never waits for a new segment rebind.
            action = new NetherPlannedAction(NetherActionKind.FinishAtCheckpoint);
        }
        Audit(
            NetherDetailedAuditKind.Checkpoint,
            "checkpoint:" + checkpoint.Kind + ":" + snapshot.MapId + ":" + snapshot.CurrentFloorId,
            new NetherDetailedAuditField("decision", checkpoint.Kind.ToString()),
            new NetherDetailedAuditField("action", action.Kind.ToString()),
            new NetherDetailedAuditField("ticketCount", action.TicketCount.ToString()),
            new NetherDetailedAuditField("lockReward", action.ReturnLockReward.ToString()),
            new NetherDetailedAuditField("preserveCount", action.ReturnPreserveItemIds.Count.ToString()),
            new NetherDetailedAuditField("order", action.Kind == NetherActionKind.Continue ? "continue-preflight-return" : "finish-no-return")
        );
        ExecuteNativeAction(snapshot, action, "checkpoint");
    }

    private static void PlanCodeSelection(NetherSnapshot snapshot, NetherAutoClimbSettings settings)
    {
        NetherRuntimeCodeCandidatesResult candidates = _bridge.TryGetCodeCandidates();
        if (!candidates.IsSuccess)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "code-candidates:" + candidates.Detail);
            return;
        }

        NetherCodeDecision decision = CodePolicy.Decide(
            new NetherCodePortfolio
            {
                CurrentCodes = snapshot.Codes,
                Capacity = snapshot.CodeCapacity,
                ReloadCount = snapshot.CodeReloadCount,
                IsMasterComplete = candidates.IsMasterComplete,
                LockedLane = _lockedCombatLane,
            },
            candidates.Candidates,
            settings
        );
        if (decision.Kind == NetherCodeDecisionKind.Pause)
        {
            FailClosed(decision.PauseReason, decision.Detail);
            return;
        }
        if (decision.Kind == NetherCodeDecisionKind.Keep)
        {
            // There is no confirmed native "close without selecting" callback for this popup.
            FailClosed(NetherPauseReason.UnsupportedPopup, "code-policy-keep:" + decision.Detail);
            return;
        }

        _lockedCombatLane = decision.LockedLane;

        NetherPlannedAction action = decision.Kind == NetherCodeDecisionKind.Reload
            ? new NetherPlannedAction(NetherActionKind.ReloadCode)
            : new NetherPlannedAction(NetherActionKind.SelectCode)
            {
                CodeId = decision.SelectedCodeId,
                ReplaceCodeId = decision.RemoveCodeId,
            };
        ExecuteNativeAction(snapshot, action, "code");
    }

    private static void PlanRoute(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings,
        int effectiveMaxDepth
    )
    {
        NetherRuntimeRouteSafetyData runtimeSafety = _bridge.TryCaptureRouteSafety(snapshot.Floors);
        NetherRuntimeInteractivePreEntryInputsResult interactivePreEntry =
            _bridge.TryCaptureInteractivePreEntryInputs(snapshot, settings);
        Audit(
            NetherDetailedAuditKind.Interactive,
            "preentry:" + interactivePreEntry.IsSuccess + ":" + interactivePreEntry.ByFloorMasterId.Count,
            new NetherDetailedAuditField("captured", interactivePreEntry.IsSuccess.ToString()),
            new NetherDetailedAuditField("floorInputs", interactivePreEntry.ByFloorMasterId.Count.ToString()),
            new NetherDetailedAuditField("detail", interactivePreEntry.Detail)
        );
        NetherAutoClimbRouteSafetyDecision routeDecision = RouteSafetyWiring.Plan(
            snapshot,
            settings,
            effectiveMaxDepth,
            runtimeSafety,
            interactivePreEntry
        );
        NetherRoutePlan route = routeDecision.Route;
        AuditRoute(snapshot, route, routeDecision.Context);
        if (!route.HasSelection)
        {
            LogAction(
                "route-rejected",
                snapshot,
                string.Join(",", route.Audit.Take(16).Select(item => item.FloorId + ":" + item.Reason))
            );
            FailClosed(route.PauseReason, "route:" + route.PauseDetail);
            return;
        }

        NetherFloorNode node = route.SelectedNode!;
        bool combatNode = node.NodeType is NetherFloorNodeType.Battle
            or NetherFloorNodeType.MiniBoss or NetherFloorNodeType.Boss;
        if (combatNode && routeDecision.IsCombatSelectionMissingProjection)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "route-selected-combat-without-projection-payload");
            return;
        }
        if (routeDecision.SelectFloorAction is not NetherPlannedAction action)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "route-selected-without-production-floor-action");
            return;
        }
        LogAction(
            "route-selected",
            snapshot,
            string.Join(",", route.Audit.Take(16).Select(item => item.FloorId + ":" + item.Reason))
        );
        ExecuteNativeAction(snapshot, action, "route");
    }

    private static void BeginBattleWait(NetherSnapshot snapshot)
    {
        NetherBattleProjectionPayload? entryProjection = _pendingBattleProjection;
        if (entryProjection == null
            || entryProjection.MapId != snapshot.MapId
            || entryProjection.FloorId != snapshot.CurrentFloorId
            || entryProjection.PreBattleErosion is < 0 or >= 100
            || string.IsNullOrEmpty(entryProjection.CodeHash)
            || string.IsNullOrEmpty(entryProjection.ProjectionIdentity))
        {
            FailClosedTerminal(NetherPauseReason.UnknownMasterData, "battle-entry-projection-unavailable-or-wrong-target");
            return;
        }

        // The native battle tasks prove only that clear/close has ended.  The contract is
        // settled exclusively by the exact GET-only refresh in BattleSettlementFlow; do not
        // infer success from the task completing or issue a second battle request.
        var action = new NetherPlannedAction(NetherActionKind.BattleSettlement)
        {
            BattleSettlement = new NetherBattleSettlementContract(
                EntryMapId: snapshot.MapId,
                EntryFloorId: snapshot.CurrentFloorId,
                EntryStatus: NetherSessionStatus.Battle,
                ExpectedMapId: snapshot.MapId,
                ExpectedFloorId: snapshot.CurrentFloorId,
                ExpectedStatus: NetherSessionStatus.Play,
                ProjectionIdentity: entryProjection.ProjectionIdentity
            )
            {
                EntryProjection = entryProjection,
            },
        };
        Audit(
            NetherDetailedAuditKind.Battle,
            "entry-projection:" + entryProjection.ProjectionIdentity,
            new NetherDetailedAuditField("mapId", entryProjection.MapId.ToString()),
            new NetherDetailedAuditField("floorId", entryProjection.FloorId.ToString()),
            new NetherDetailedAuditField("preErosion", entryProjection.PreBattleErosion.ToString()),
            new NetherDetailedAuditField("projectedMin", entryProjection.ProjectedMinimumErosion.ToString()),
            new NetherDetailedAuditField("projectedMax", entryProjection.ProjectedMaximumErosion.ToString()),
            new NetherDetailedAuditField("codeHash", entryProjection.CodeHash),
            new NetherDetailedAuditField("identity", entryProjection.ProjectionIdentity)
        );
        if (!State.TryBegin(action, snapshot) || !BattleSettlementFlow.Begin(action, snapshot))
        {
            FailClosedTerminal(NetherPauseReason.BattleLifecycleFault, "could-not-begin-battle-settlement");
            return;
        }
        _pendingBattleProjection = null;
        if (!_bridge.IsF11Busy && !EnsureBattleLease())
            return;
        LogAction("await-battle-settlement", snapshot, action.BattleSettlement.ProjectionIdentity);
    }

    private static bool EnsureBattleLease()
    {
        if (BattleSettingsLifecycle.LeasePhase == NetherBattleSettingsLeasePhase.Forced)
            return true;
        NetherNativeActionResult lease = BattleSettingsLifecycle.OnBattleEnter();
        if (lease.Kind == NetherNativeActionResultKind.Completed)
            return true;

        PauseForBattleSettingsLease(lease, "battle-entry");
        return false;
    }

    private static void ExecuteReturnSelection(NetherSnapshot snapshot, NetherReturnItemSelection selection)
    {
        NetherPlannedAction action = new(NetherActionKind.SelectReturnItems);
        if (!State.TryBegin(action, snapshot))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin-return-selection");
            return;
        }

        HandleInvocationResult(
            snapshot,
            action,
            _bridge.SelectReturnItems(selection.Items),
            "return-items:" + string.Join(",", selection.Audit)
        );
    }

    private static void ExecuteNativeAction(
        NetherSnapshot snapshot,
        NetherPlannedAction action,
        string boundary,
        Action<NetherNativeActionResult>? afterInvoke = null
    )
    {
        if (action.Kind == NetherActionKind.Continue)
        {
            ExecuteContinueNativeAction(snapshot, action, boundary);
            return;
        }

        if (!State.TryBegin(action, snapshot))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin:" + action.Kind);
            return;
        }

        if (action.Kind == NetherActionKind.SelectFloor)
        {
            _pendingBattleProjection = action.BattleProjection;
            if (!RuntimeFlow.BeginFloorParent(action)
                || !_bridge.BeginFloorParent(action, RuntimeFlow.Generation))
            {
                RuntimeFlow.TerminateParent();
                _pendingBattleProjection = null;
                State.ObserveActionResult(snapshot.Fingerprint, NetherActionOutcome.NotApplied);
                FailClosed(NetherPauseReason.BindingUnavailable, "floor-parent-owner-registration-unavailable");
                return;
            }
        }
        NetherNativeActionResult native = _bridge.Invoke(action);
        if (action.Kind == NetherActionKind.SelectFloor
            && native.Kind is not (NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed))
        {
            _bridge.TerminateFloorParent();
            RuntimeFlow.TerminateParent();
            _pendingBattleProjection = null;
        }
        afterInvoke?.Invoke(native);
        HandleInvocationResult(snapshot, action, native, boundary);
    }

    private static void ExecuteContinueNativeAction(
        NetherSnapshot snapshot,
        NetherPlannedAction action,
        string boundary
    )
    {
        if (action.TicketCount != 1 || action.TicketCost != 1)
        {
            FailClosed(NetherPauseReason.InvalidConfiguration, "continue-requires-exact-one-ticket-nonboost");
            return;
        }

        // This is intentionally before State.TryBegin/TryBeginContinueSceneHandoff/Invoke.
        // A pause here proves that no native parent task and therefore no native API chain has
        // been started for an incomplete carry-out contract.
        NetherCheckpointReturnPreflightDecision preflight = _bridge.PreflightContinueReturn(action);
        Audit(
            NetherDetailedAuditKind.Checkpoint,
            "continue-preflight:" + preflight.Kind + ":" + preflight.SelectionLimit + ":" + preflight.ExpectedPristineHash,
            new NetherDetailedAuditField("result", preflight.Kind.ToString()),
            new NetherDetailedAuditField("selectionLimit", preflight.SelectionLimit.ToString()),
            new NetherDetailedAuditField("wholeEntries", preflight.WholeEntrySelection.Count.ToString()),
            new NetherDetailedAuditField("pristineHash", preflight.ExpectedPristineHash),
            new NetherDetailedAuditField("detail", preflight.Detail)
        );
        if (!CheckpointReturnPreflight.CanStartNativeContinueParent(preflight))
        {
            FailClosed(
                preflight.PauseReason == NetherPauseReason.None
                    ? NetherPauseReason.UnknownMasterData
                    : preflight.PauseReason,
                "continue-return-preflight:" + preflight.Detail
            );
            return;
        }
        action = action with
        {
            ReturnLockReward = preflight.SelectionLimit,
            ReturnPreflightSelectionLimit = preflight.SelectionLimit,
            ReturnExpectedPristineHash = preflight.ExpectedPristineHash,
            ReturnPreflightWholeEntrySelection = preflight.WholeEntrySelection,
        };

        if (!State.TryBegin(action, snapshot))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin:continue");
            return;
        }
        if (!_bridge.TryBeginContinueSceneHandoff(out long ownerGeneration)
            || !ContinueSceneFlow.Begin(action, snapshot, ownerGeneration))
        {
            State.ObserveActionResult(snapshot.Fingerprint, NetherActionOutcome.NotApplied);
            FailClosed(NetherPauseReason.BindingUnavailable, "continue-handoff-owner-generation-unavailable");
            return;
        }

        NetherNativeActionResult native = _bridge.Invoke(action);
        LogAction(boundary + ":" + action.Kind, snapshot, native.Detail);
        switch (native.Kind)
        {
            case NetherNativeActionResultKind.Started:
            case NetherNativeActionResultKind.Completed:
                // The exact checkpoint parent still owns terminal evidence.  Do not convert
                // invocation return into generic reconciliation before teardown/rebind.
                return;
            case NetherNativeActionResultKind.Rejected:
                ContinueSceneFlow.Reset();
                State.ObserveActionResult(snapshot.Fingerprint, NetherActionOutcome.NotApplied);
                return;
            case NetherNativeActionResultKind.BindingUnavailable:
                ContinueSceneFlow.Reset();
                FailClosed(NetherPauseReason.BindingUnavailable, native.Detail);
                return;
            default:
                ContinueSceneFlow.Reset();
                FailClosedTerminal(NetherPauseReason.ContinueLifecycleFault, "continue-native-invocation:" + native.Detail);
                return;
        }
    }

    private static void HandleInvocationResult(
        NetherSnapshot snapshot,
        NetherPlannedAction action,
        NetherNativeActionResult native,
        string boundary
    )
    {
        LogAction(boundary + ":" + action.Kind, snapshot, native.Detail);
        switch (native.Kind)
        {
            case NetherNativeActionResultKind.Started:
                return;
            case NetherNativeActionResultKind.Completed:
            case NetherNativeActionResultKind.UnknownOutcome:
                State.ObserveUnknownOutcome();
                return;
            case NetherNativeActionResultKind.BindingUnavailable:
                FailClosed(NetherPauseReason.BindingUnavailable, native.Detail);
                return;
            case NetherNativeActionResultKind.Rejected:
                State.ObserveActionResult(snapshot.Fingerprint, NetherActionOutcome.NotApplied);
                return;
            default:
                FailClosed(NetherPauseReason.AmbiguousServerOutcome, native.Detail);
                return;
        }
    }

    private static void FailClosed(NetherPauseReason reason, string detail)
    {
        if (State.Phase != NetherAutoClimbPhase.Paused)
            State.Pause(reason, detail);
        ProjectionCalibration.Clear();
        ObserveBattleSettingsLeaseBoundary(
            BattleSettingsLifecycle.OnAutomationPause(),
            "pause:" + reason,
            pauseEnabledState: false
        );
        LogTransition("PAUSED " + reason + ":" + detail);
    }

    private static void FailClosedTerminal(NetherPauseReason reason, string detail)
    {
        // Unlike an ordinary pause, a terminal battle fault must invalidate the pending
        // action evidence.  Re-enabling F12 cannot replay or reconcile a task after the
        // native scene/controller that owned it has faulted, been canceled, or disappeared.
        BattleSettlementFlow.TerminateForSceneLoss();
        State.TerminatePendingAndPause(reason, detail);
        ProjectionCalibration.Clear();
        ObserveBattleSettingsLeaseBoundary(
            BattleSettingsLifecycle.OnAutomationPause(),
            "terminal-pause:" + reason,
            pauseEnabledState: false
        );
        LogTransition("PAUSED terminal " + reason + ":" + detail);
    }

    private static void PumpBattleSettingsLeaseRetry()
    {
        NetherBattleSettingsLeaseRetryPumpResult retry = BattleSettingsLifecycle.PumpUpdate();
        if (!retry.Attempted || retry.Result is not NetherNativeActionResult result)
            return;

        ObserveBattleSettingsLeaseBoundary(result, "scheduled-restore-retry", pauseEnabledState: State.IsEnabled);
    }

    private static bool EnsureBattleSettingsLifecycleReady(string boundary)
    {
        if (!BattleSettingsLifecycle.BlocksRouteOrBattle)
            return true;

        PauseForBattleSettingsLease(
            BattleSettingsLifecycle.IsExactAccessorRegistered
                ? NetherNativeActionResult.UnknownOutcome(
                    "battle-settings-lease-recovery-pending:"
                    + BattleSettingsLifecycle.RuntimeState
                    + ":"
                    + BattleSettingsLifecycle.LeasePhase
                )
                : NetherNativeActionResult.BindingUnavailable("native-battle-settings-accessor-unregistered"),
            boundary
        );
        return false;
    }

    private static bool ObserveBattleSettingsLeaseBoundary(
        NetherNativeActionResult result,
        string boundary,
        bool pauseEnabledState
    )
    {
        Audit(
            NetherDetailedAuditKind.Lease,
            "lease:" + boundary + ":" + result.Kind + ":" + result.Detail,
            new NetherDetailedAuditField("boundary", boundary),
            new NetherDetailedAuditField("result", result.Kind.ToString()),
            new NetherDetailedAuditField("phase", BattleSettingsLifecycle.LeasePhase.ToString()),
            new NetherDetailedAuditField("runtimeState", BattleSettingsLifecycle.RuntimeState.ToString()),
            new NetherDetailedAuditField("detail", result.Detail)
        );
        if (result.Kind == NetherNativeActionResultKind.Completed)
        {
            LogTransition("LEASE " + boundary + " completed:" + result.Detail);
            return true;
        }

        LogTransition("LEASE " + boundary + " " + result.Kind + ":" + result.Detail);
        if (pauseEnabledState && State.IsEnabled)
            PauseForBattleSettingsLease(result, boundary);
        return false;
    }

    private static void PauseForBattleSettingsLease(NetherNativeActionResult result, string boundary)
    {
        NetherPauseReason reason = result.Kind == NetherNativeActionResultKind.BindingUnavailable
            ? NetherPauseReason.BindingUnavailable
            : NetherPauseReason.BattleSettingsLeaseFault;
        string detail = "battle-settings-lease:" + boundary + ":" + result.Detail;
        if (State.Phase != NetherAutoClimbPhase.Paused)
            State.Pause(reason, detail);
        ProjectionCalibration.Clear();
        LogTransition("PAUSED " + reason + ":" + detail);
    }

    private static NetherSnapshot BuildSnapshotFromFingerprint(NetherSnapshotFingerprint fingerprint) => new()
    {
        Status = fingerprint.Status,
        NetherId = fingerprint.NetherId,
        MapId = fingerprint.MapId,
        CurrentFloorId = fingerprint.CurrentFloorId,
        FloorLevel = fingerprint.FloorLevel,
        FloorIndex = fingerprint.FloorIndex,
        ErosionPoint = fingerprint.ErosionPoint,
        TicketCount = fingerprint.TicketCount,
        TreasureKeyCount = fingerprint.TreasureKeyCount,
        NetherGold = fingerprint.NetherGold,
        CodeReloadCount = fingerprint.CodeReloadCount,
        LockReward = fingerprint.LockReward,
        CharacterHpHash = fingerprint.CharacterHpHash,
        CodeHash = fingerprint.CodeHash,
        MapHash = fingerprint.MapHash,
    };

    private static NetherAutoClimbSettings BuildSettings() => new()
    {
        MaxDepth = Config.NetherAutoClimbMaxDepth.Value,
        SoftErosionLimit = Config.NetherAutoClimbSoftErosionLimit.Value,
        MinimumCharacterHpPermille = Config.NetherAutoClimbMinimumCharacterHpPermille.Value,
        CombatLane = Config.NetherAutoClimbCombatLane.Value,
        CodeReloadReserve = Config.NetherAutoClimbCodeReloadReserve.Value,
        TreasureMode = Config.NetherAutoClimbTreasureMode.Value,
        ShopMode = Config.NetherAutoClimbShopMode.Value,
        DetailedLogging = Config.NetherAutoClimbDetailedLogging.Value,
    };

    private static void LogAction(string action, NetherSnapshot snapshot, string detail)
    {
        Audit(
            NetherDetailedAuditKind.Native,
            "invoke:" + action + ":" + snapshot.MapId + ":" + snapshot.CurrentFloorId,
            new NetherDetailedAuditField("action", action),
            new NetherDetailedAuditField("status", snapshot.Status.ToString()),
            new NetherDetailedAuditField("mapId", snapshot.MapId.ToString()),
            new NetherDetailedAuditField("floorId", snapshot.CurrentFloorId.ToString()),
            new NetherDetailedAuditField("floorLevel", snapshot.FloorLevel.ToString()),
            new NetherDetailedAuditField("erosion", snapshot.ErosionPoint.ToString()),
            new NetherDetailedAuditField("detail", detail)
        );
    }

    private static void LogTransition(string transition)
    {
        if (!Config.NetherAutoClimbDetailedLogging.Value)
            return;
        if (string.Equals(_lastTransition, transition, StringComparison.Ordinal))
            return;
        _lastTransition = transition;
        Audit(
            transition.StartsWith("LEASE ", StringComparison.Ordinal)
                ? NetherDetailedAuditKind.Lease
                : NetherDetailedAuditKind.Task,
            "transition:" + transition,
            new NetherDetailedAuditField("transition", transition)
        );
    }

    private static void Audit(
        NetherDetailedAuditKind kind,
        string key,
        params NetherDetailedAuditField[] fields
    )
    {
        DetailedAudit.Emit(Config.NetherAutoClimbDetailedLogging.Value, kind, key, fields);
    }

    private static void AuditSnapshot(NetherSnapshot snapshot, string boundary)
    {
        int minimumHp = snapshot.Characters
            .Where(character => character.IsActive)
            .Select(character => character.HpPermille)
            .DefaultIfEmpty(-1)
            .Min();
        Audit(
            NetherDetailedAuditKind.Snapshot,
            "snapshot:" + boundary + ":" + snapshot.MapId + ":" + snapshot.CurrentFloorId + ":" + snapshot.Fingerprint,
            new NetherDetailedAuditField("boundary", boundary),
            new NetherDetailedAuditField("netherId", snapshot.NetherId.ToString()),
            new NetherDetailedAuditField("mapId", snapshot.MapId.ToString()),
            new NetherDetailedAuditField("floorId", snapshot.CurrentFloorId.ToString()),
            new NetherDetailedAuditField("floorLevel", snapshot.FloorLevel.ToString()),
            new NetherDetailedAuditField("hpPermille", minimumHp.ToString()),
            new NetherDetailedAuditField("erosion", snapshot.ErosionPoint.ToString()),
            new NetherDetailedAuditField("tickets", snapshot.TicketCount.ToString()),
            new NetherDetailedAuditField("keys", snapshot.TreasureKeyCount.ToString()),
            new NetherDetailedAuditField("gold", snapshot.NetherGold.ToString()),
            new NetherDetailedAuditField("codeHash", snapshot.CodeHash)
        );
    }

    private static void AuditRoute(
        NetherSnapshot snapshot,
        NetherRoutePlan route,
        NetherRouteSafetyContext context
    )
    {
        long selectedFloorId = route.SelectedNode?.FloorId ?? 0;
        string candidates = string.Join(
            "|",
            route.Audit
                .Take(8)
                .Select(candidate => candidate.FloorId + ":" + candidate.Reason)
        );
        int terminalWorstCase = selectedFloorId > 0
            ? context.MinimumWorstCaseErosion(selectedFloorId)
            : -1;
        Audit(
            NetherDetailedAuditKind.Route,
            "route:" + snapshot.MapId + ":" + snapshot.CurrentFloorId + ":" + selectedFloorId + ":" + route.PauseReason,
            new NetherDetailedAuditField("selectedFloorId", selectedFloorId.ToString()),
            new NetherDetailedAuditField("pauseReason", route.PauseReason.ToString()),
            new NetherDetailedAuditField("pauseDetail", route.PauseDetail),
            new NetherDetailedAuditField("candidates", candidates),
            new NetherDetailedAuditField("reverseWorst", terminalWorstCase.ToString()),
            new NetherDetailedAuditField("maxDepth", context.MaximumFloorLevel.ToString()),
            new NetherDetailedAuditField("mapId", snapshot.MapId.ToString())
        );
    }

    private sealed class RuntimeBridgeTestScope : IDisposable
    {
        private readonly INetherRuntimeBridge _bridge;
        private readonly NetherAutoClimbStateMachine _state;
        private readonly NetherAutoClimbSettingsSnapshotGate _settingsGate;
        private readonly NetherActionProjectionCalibration _projectionCalibration;
        private readonly NetherRuntimeFlowCoordinator _runtimeFlow;
        private readonly NetherReadOnlyReconcileCoordinator _readOnlyReconcileFlow;
        private readonly NetherBattleSettlementCoordinator _battleSettlementFlow;
        private readonly NetherContinueSceneRuntimeCoordinator _continueSceneFlow;
        private readonly NetherBattleSettingsLeaseControllerLifecycle _battleSettingsLifecycle;
        private readonly bool _initialized;
        private readonly NetherCombatLane? _lockedCombatLane;
        private readonly NetherBattleProjectionPayload? _pendingBattleProjection;
        private readonly string _lastTransition;
        private bool _disposed;

        public RuntimeBridgeTestScope(
            INetherRuntimeBridge bridge,
            NetherAutoClimbStateMachine state,
            NetherAutoClimbSettingsSnapshotGate settingsGate,
            NetherActionProjectionCalibration projectionCalibration,
            NetherRuntimeFlowCoordinator runtimeFlow,
            NetherReadOnlyReconcileCoordinator readOnlyReconcileFlow,
            NetherBattleSettlementCoordinator battleSettlementFlow,
            NetherContinueSceneRuntimeCoordinator continueSceneFlow,
            NetherBattleSettingsLeaseControllerLifecycle battleSettingsLifecycle,
            bool initialized,
            NetherCombatLane? lockedCombatLane,
            NetherBattleProjectionPayload? pendingBattleProjection,
            string lastTransition
        )
        {
            _bridge = bridge;
            _state = state;
            _settingsGate = settingsGate;
            _projectionCalibration = projectionCalibration;
            _runtimeFlow = runtimeFlow;
            _readOnlyReconcileFlow = readOnlyReconcileFlow;
            _battleSettlementFlow = battleSettlementFlow;
            _continueSceneFlow = continueSceneFlow;
            _battleSettingsLifecycle = battleSettingsLifecycle;
            _initialized = initialized;
            _lockedCombatLane = lockedCombatLane;
            _pendingBattleProjection = pendingBattleProjection;
            _lastTransition = lastTransition;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            NetherAutoClimbController._bridge = _bridge;
            NetherAutoClimbController.State = _state;
            NetherAutoClimbController.SettingsGate = _settingsGate;
            NetherAutoClimbController.ProjectionCalibration = _projectionCalibration;
            NetherAutoClimbController.RuntimeFlow = _runtimeFlow;
            NetherAutoClimbController.ReadOnlyReconcileFlow = _readOnlyReconcileFlow;
            NetherAutoClimbController.BattleSettlementFlow = _battleSettlementFlow;
            NetherAutoClimbController.ContinueSceneFlow = _continueSceneFlow;
            NetherAutoClimbController.BattleSettingsLifecycle = _battleSettingsLifecycle;
            NetherAutoClimbController._initialized = _initialized;
            NetherAutoClimbController._lockedCombatLane = _lockedCombatLane;
            NetherAutoClimbController._pendingBattleProjection = _pendingBattleProjection;
            NetherAutoClimbController._lastTransition = _lastTransition;
        }
    }
}
