#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Main-thread F12 coordinator.  Harmony patches only register observations with the bridge;
/// this class consumes them from Hotkey.Update and invokes at most one verified native UI flow
/// at a time.  It deliberately pauses when a current-version mapping cannot prove a safe
/// decision instead of substituting a datastore/API request.
/// </summary>
internal static class NetherAutoClimbController
{
    private static readonly NetherAutoClimbStateMachine State = new();
    private static readonly NetherAutoClimbSettingsSnapshotGate SettingsGate = new();
    private static readonly NetherCheckpointPolicy CheckpointPolicy = new();
    private static readonly NetherCodePolicy CodePolicy = new();
    private static readonly NetherRoutePlanner RoutePlanner = new();
    private static readonly NetherReturnItemPolicy ReturnItemPolicy = new();
    private static INetherRuntimeBridge _bridge = NetherRuntimeBridge.Instance;

    private static bool _initialized;
    private static bool _reconcileRequested;
    private static bool _leaseRestoreCompleted;
    private static NetherCombatLane? _lockedCombatLane;
    private static string _lastTransition = string.Empty;

    public static bool IsEnabled => State.IsEnabled;

    public static NetherAutoClimbPhase Phase => State.Phase;

    public static NetherPauseReason PauseReason => State.PauseReason;

    public static string PauseDetail => State.PauseDetail;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        NetherBattleSettingsLease.Initialize();
        NetherNativeActionResult recovery = NetherBattleSettingsLease.Instance.RecoverOnLoad();
        if (recovery.Kind is NetherNativeActionResultKind.BindingUnavailable or NetherNativeActionResultKind.UnknownOutcome)
        {
            Logger.Error("[F12][NetherClimb] persisted battle-settings lease requires recovery: " + recovery.Detail);
        }
        else if (recovery.Kind == NetherNativeActionResultKind.Rejected)
        {
            Logger.Error("[F12][NetherClimb] persisted battle-settings lease was rejected: " + recovery.Detail);
        }
    }

    public static void Toggle()
    {
        Initialize();
        if (State.IsEnabled)
        {
            State.Toggle(isInNether: true);
            RestoreLease("f12-disabled");
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

        _leaseRestoreCompleted = false;
        _lockedCombatLane = null;
        NetherNativeActionResult lease = NetherBattleSettingsLease.Instance.AcquireAndForce();
        if (lease.Kind != NetherNativeActionResultKind.Completed)
        {
            FailClosed(
                lease.Kind == NetherNativeActionResultKind.BindingUnavailable
                    ? NetherPauseReason.BindingUnavailable
                    : NetherPauseReason.BattleSettingsLeaseFault,
                "battle-settings-lease:" + lease.Detail
            );
            return;
        }

        LogTransition("ON maxDepth=" + BuildSettings().MaxDepth + " softErosion=" + BuildSettings().SoftErosionLimit);
    }

    public static void Update()
    {
        if (!_initialized)
            return;

        if (State.Phase == NetherAutoClimbPhase.Completed)
        {
            RestoreLease("nether-result-complete");
            return;
        }
        if (State.Phase == NetherAutoClimbPhase.Paused)
        {
            RestoreLease("fail-closed:" + State.PauseReason);
            return;
        }

        bool disabledReconciliation = !State.IsEnabled
            && State.Phase == NetherAutoClimbPhase.Reconciling
            && State.PendingAction != null;
        if (!State.IsEnabled && !disabledReconciliation)
            return;

        if (!_bridge.HasRegisteredFloorSelection)
        {
            if (State.IsEnabled)
                FailClosed(NetherPauseReason.NotInNether, "registered-nether-runtime-lost");
            return;
        }

        switch (State.Phase)
        {
            case NetherAutoClimbPhase.ExecutingNativeAction:
                PollPendingNativeAction();
                return;
            case NetherAutoClimbPhase.Reconciling:
                Reconcile();
                return;
            case NetherAutoClimbPhase.AwaitingBattle:
            case NetherAutoClimbPhase.AwaitingF11:
                ObserveBattle();
                return;
            case NetherAutoClimbPhase.AwaitingSceneChange:
                ObserveResult();
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
        RestoreLease("plugin-unload");
        _bridge.ClearRegistrations();
        _initialized = false;
        _reconcileRequested = false;
        _lockedCombatLane = null;
        _lastTransition = string.Empty;
    }

    private static void PollPendingNativeAction()
    {
        NetherNativeActionResult native = _bridge.PollNativeFlow();
        switch (native.Kind)
        {
            case NetherNativeActionResultKind.Started:
                return;
            case NetherNativeActionResultKind.Completed:
                // Most native controller methods return UniTask but do not expose that task to
                // Harmony.  Reconcile rather than guessing that the server mutation succeeded.
                State.ObserveUnknownOutcome();
                _reconcileRequested = false;
                return;
            case NetherNativeActionResultKind.UnknownOutcome:
                State.ObserveUnknownOutcome();
                _reconcileRequested = false;
                return;
            case NetherNativeActionResultKind.BindingUnavailable:
                FailClosed(NetherPauseReason.BindingUnavailable, native.Detail);
                return;
            default:
                FailClosed(NetherPauseReason.AmbiguousServerOutcome, native.Detail);
                return;
        }
    }

    private static void Reconcile()
    {
        if (!_reconcileRequested)
        {
            NetherNativeActionResult request = _bridge.Reconcile();
            if (request.Kind == NetherNativeActionResultKind.Started || request.Kind == NetherNativeActionResultKind.Completed)
            {
                _reconcileRequested = true;
                return;
            }
            if (request.Kind == NetherNativeActionResultKind.BindingUnavailable)
            {
                FailClosed(NetherPauseReason.BindingUnavailable, request.Detail);
                return;
            }
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "reconcile:" + request.Detail);
            return;
        }

        NetherNativeActionResult native = _bridge.PollNativeFlow();
        if (native.Kind == NetherNativeActionResultKind.Started)
            return;
        if (native.Kind == NetherNativeActionResultKind.BindingUnavailable)
        {
            FailClosed(NetherPauseReason.BindingUnavailable, "reconcile-poll:" + native.Detail);
            return;
        }
        if (native.Kind is NetherNativeActionResultKind.UnknownOutcome or NetherNativeActionResultKind.Rejected)
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "reconcile-poll:" + native.Detail);
            return;
        }

        NetherRuntimeSnapshotResult captured = _bridge.TryCaptureSnapshot();
        if (!captured.IsSuccess)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "reconcile-snapshot:" + captured.Detail);
            return;
        }

        _reconcileRequested = false;
        NetherSnapshot snapshot = captured.Snapshot!;
        if (State.PendingAction == null || State.PreActionFingerprint == null)
        {
            State.ObserveStable(snapshot.Fingerprint);
            return;
        }

        // A same fingerprint cannot prove that the original controller did nothing: visual
        // close-only actions and a delayed response are indistinguishable here.  Pause rather
        // than replaying or marking it NotApplied.
        NetherActionOutcome outcome = State.PreActionFingerprint.Value == snapshot.Fingerprint
            ? NetherActionOutcome.Ambiguous
            : NetherActionOutcome.Applied;
        State.ObserveActionResult(snapshot.Fingerprint, outcome);
        if (State.Phase == NetherAutoClimbPhase.Paused)
            RestoreLease("ambiguous-reconcile");
    }

    private static void ObserveBattle()
    {
        State.ObserveF11Busy(BattleSessionAutoSL.HasActiveNetherOperation);
        if (State.Phase == NetherAutoClimbPhase.AwaitingF11)
            return;

        if (_bridge.TryConsumeBattleClear() || _bridge.TryConsumeBattleClose())
        {
            State.BeginReconcile();
            _reconcileRequested = false;
            return;
        }

        NetherRuntimeSnapshotResult captured = _bridge.TryCaptureSnapshot();
        if (!captured.IsSuccess)
        {
            FailClosed(NetherPauseReason.UnknownMasterData, "battle-snapshot:" + captured.Detail);
            return;
        }
        State.ObserveStable(captured.Snapshot!.Fingerprint);
    }

    private static void ObserveResult()
    {
        if (!_bridge.TryConsumeResultSuccess())
            return;

        if (State.Complete())
            LogTransition("COMPLETED native-result-succeeded");
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
        State.ObserveStable(snapshot.Fingerprint);
        if (State.Phase != NetherAutoClimbPhase.Stable)
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

        if (snapshot.Status == NetherSessionStatus.Wait)
        {
            PlanCodeSelection(snapshot, settings);
            return;
        }

        if (snapshot.Status == NetherSessionStatus.Play)
        {
            PlanRoute(snapshot);
            return;
        }

        FailClosed(NetherPauseReason.UnknownStatus, "unhandled-stable-status:" + snapshot.Status);
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

        if (snapshot.LockReward > 0)
        {
            NetherReturnItemSelection selection = ReturnItemPolicy.Select(
                snapshot.AcquiredItems,
                snapshot.LockReward,
                preserveIds
            );
            if (selection.Kind == NetherReturnItemSelectionKind.Pause)
            {
                FailClosed(selection.PauseReason, selection.Detail);
                return;
            }
            if (selection.Items.Count > 0)
            {
                ExecuteReturnSelection(snapshot, selection);
                return;
            }
        }

        NetherPlannedAction action = checkpoint.Kind == NetherCheckpointDecisionKind.ContinueOneTicket
            ? new NetherPlannedAction(NetherActionKind.Continue) { TicketCount = checkpoint.TicketCount }
            : new NetherPlannedAction(NetherActionKind.FinishAtCheckpoint);
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

    private static void PlanRoute(NetherSnapshot snapshot)
    {
        // The live map model currently exposes graph topology, not a version-confirmed effect
        // projection for every floor.  Passing a permissive default here would silently treat
        // an unmodelled Battle/Event erosion or HP effect as safe, so every unprojected node is
        // explicitly marked unknown and the planner pauses with its audit trail.
        var known = new Dictionary<long, bool>();
        foreach (NetherFloorNode floor in snapshot.Floors)
            known[floor.FloorId] = false;

        NetherRoutePlan route = RoutePlanner.Plan(snapshot, new NetherRouteSafetyContext
        {
            KnownNodeByFloorId = known,
        });
        if (!route.HasSelection)
        {
            FailClosed(route.PauseReason, "route:" + route.PauseDetail);
            return;
        }

        NetherFloorNode node = route.SelectedNode!;
        ExecuteNativeAction(
            snapshot,
            new NetherPlannedAction(NetherActionKind.SelectFloor)
            {
                FloorId = node.FloorId,
                FloorLevel = node.FloorLevel,
                FloorIndex = node.FloorIndex,
            },
            "route"
        );
    }

    private static void BeginBattleWait(NetherSnapshot snapshot)
    {
        if (!State.TryBegin(new NetherPlannedAction(NetherActionKind.AwaitNativeFlow), snapshot.Fingerprint))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin-battle-wait");
            return;
        }
        LogAction("await-battle", snapshot, string.Empty);
    }

    private static void ExecuteReturnSelection(NetherSnapshot snapshot, NetherReturnItemSelection selection)
    {
        NetherPlannedAction action = new(NetherActionKind.SelectReturnItems);
        if (!State.TryBegin(action, snapshot.Fingerprint))
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

    private static void ExecuteNativeAction(NetherSnapshot snapshot, NetherPlannedAction action, string boundary)
    {
        if (!State.TryBegin(action, snapshot.Fingerprint))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin:" + action.Kind);
            return;
        }
        HandleInvocationResult(snapshot, action, _bridge.Invoke(action), boundary);
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
                _reconcileRequested = false;
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
        RestoreLease("pause:" + reason);
        LogTransition("PAUSED " + reason + ":" + detail);
    }

    private static void RestoreLease(string reason)
    {
        if (_leaseRestoreCompleted)
            return;

        NetherNativeActionResult restore = NetherBattleSettingsLease.Instance.Restore(reason);
        if (restore.Kind == NetherNativeActionResultKind.Completed)
        {
            _leaseRestoreCompleted = true;
            return;
        }
        if (restore.Kind is NetherNativeActionResultKind.BindingUnavailable or NetherNativeActionResultKind.UnknownOutcome)
            Logger.Error("[F12][NetherClimb] battle settings restore requires recovery: " + restore.Detail);
    }

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
        if (!Config.NetherAutoClimbDetailedLogging.Value)
            return;
        Logger.Info(
            "[F12][NetherClimb] action=" + action
                + " status=" + snapshot.Status
                + " floor=" + snapshot.FloorLevel
                + " erosion=" + snapshot.ErosionPoint
                + " detail=" + detail
        );
    }

    private static void LogTransition(string transition)
    {
        if (string.Equals(_lastTransition, transition, StringComparison.Ordinal))
            return;
        _lastTransition = transition;
        Logger.Info("[F12][NetherClimb] " + transition);
    }
}
