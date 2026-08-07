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
    private static readonly NetherAutoClimbStateMachine State = new();
    private static readonly NetherAutoClimbSettingsSnapshotGate SettingsGate = new();
    private static readonly NetherCheckpointPolicy CheckpointPolicy = new();
    private static readonly NetherCodePolicy CodePolicy = new();
    private static readonly NetherRoutePlanner RoutePlanner = new();
    private static readonly NetherReturnItemPolicy ReturnItemPolicy = new();
    private static readonly NetherActionProjectionCalibration ProjectionCalibration = new();
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
            Logger.Info("[F12][NetherClimb] persisted battle-settings lease is awaiting exact native accessor: " + recovery.Detail);
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
        LogTransition("ON maxDepth=" + BuildSettings().MaxDepth + " softErosion=" + BuildSettings().SoftErosionLimit + " lease=deferred-until-battle");
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
            && State.Phase is (NetherAutoClimbPhase.ExecutingNativeAction or NetherAutoClimbPhase.Reconciling)
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
        ProjectionCalibration.Clear();
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
        NetherProjectionObservation projection = ProjectionCalibration.Observe(snapshot);
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
        State.ObserveActionResult(snapshot.Fingerprint, outcome);
        if (State.Phase == NetherAutoClimbPhase.Paused)
            RestoreLease("ambiguous-reconcile");
    }

    private static void ObserveBattle()
    {
        State.ObserveF11Busy(BattleSessionAutoSL.HasActiveNetherOperation);
        if (State.Phase == NetherAutoClimbPhase.AwaitingF11)
            return;

        if (!EnsureBattleLease())
            return;

        NetherNativeActionResult lifecycle = _bridge.PollBattleLifecycle();
        if (lifecycle.Kind == NetherNativeActionResultKind.Started)
            return;
        if (lifecycle.Kind is NetherNativeActionResultKind.UnknownOutcome or NetherNativeActionResultKind.BindingUnavailable)
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "battle-lifecycle:" + lifecycle.Detail);
            return;
        }

        if (_bridge.TryConsumeBattleClear() || _bridge.TryConsumeBattleClose())
        {
            RestoreLease("battle-native-settled");
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
        NetherSnapshot snapshot = captured.Snapshot!;
        if (snapshot.Status != NetherSessionStatus.Battle && !_bridge.IsBattleActive)
            RestoreLease("battle-status-settled");
        State.ObserveStable(snapshot.Fingerprint);
    }

    private static void ObserveResult()
    {
        NetherNativeActionResult result = _bridge.PollResultFlow();
        if (result.Kind == NetherNativeActionResultKind.Started)
            return;
        if (result.Kind == NetherNativeActionResultKind.Completed)
        {
            if (State.Complete())
                LogTransition("COMPLETED native-result-succeeded");
            return;
        }

        // Result failure/cancellation is never an infinitely pending scene transition.  Keep
        // player control and require an explicit fresh observation/recovery rather than
        // issuing another Result request from F12.
        FailClosed(
            result.Kind == NetherNativeActionResultKind.BindingUnavailable
                ? NetherPauseReason.BindingUnavailable
                : NetherPauseReason.AmbiguousServerOutcome,
            "native-result:" + result.Detail
        );
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

        NetherRuntimePopupResult popup = _bridge.TryGetActivePopup();
        if (popup.IsSuccess)
        {
            PlanNativePopup(snapshot, settings, popup.Popup!);
            return;
        }

        // Wait is the server's modal state.  Choosing code from a stale owned-code list or
        // guessing a close action before its controller has registered would be unsafe.
        if (snapshot.Status == NetherSessionStatus.Wait)
        {
            FailClosed(NetherPauseReason.UnsupportedPopup, "wait-popup:" + popup.Detail);
            return;
        }

        if (snapshot.Status == NetherSessionStatus.Play)
        {
            PlanRoute(snapshot, checkpoint.EffectiveMaxDepth);
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

        // Native HandleGameClearedIfNeededAsync opens Continue first, performs its one-ticket
        // server mutation, then creates the pristine return popup.  Its current ContentModel
        // list (including real drop rarity) is selected in the bridge only after that UI exists.
        // Finish transitions directly to result and carries no return selection information.

        NetherPlannedAction action = checkpoint.Kind == NetherCheckpointDecisionKind.ContinueOneTicket
            ? new NetherPlannedAction(NetherActionKind.Continue)
            {
                TicketCount = checkpoint.TicketCount,
                ReturnLockReward = snapshot.LockReward,
                ReturnPreserveItemIds = preserveIds.OrderBy(itemId => itemId).ToArray(),
            }
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

    private static void PlanRoute(NetherSnapshot snapshot, int effectiveMaxDepth)
    {
        // The map model is an exact native rendering of the server segment: floor id, edge,
        // type, hidden state and unlock state are all mapped by the bridge.  Dynamic popup
        // effects are still gated later by their own policy; this graph gate must not erase
        // that confirmed topology by treating every node as unknown.
        var known = new Dictionary<long, bool>();
        var hardSafe = new Dictionary<long, bool>();
        var hpSafe = new Dictionary<long, bool>();
        foreach (NetherFloorNode floor in snapshot.Floors)
        {
            bool mapped = floor.NodeType is not NetherFloorNodeType.Unknown and not NetherFloorNodeType.Default;
            known[floor.FloorId] = mapped;
            // Every dynamic effect is checked again after its native popup is generated.  At
            // graph choice time only unmapped node kinds are unsafe; do not invent a numeric
            // erosion/HP delta for a future server event.
            hardSafe[floor.FloorId] = mapped && snapshot.ErosionPoint < 100;
            hpSafe[floor.FloorId] = snapshot.Characters.All(character =>
                !character.IsActive || character.HpPermille > 0
            );
        }

        NetherRoutePlan route = RoutePlanner.Plan(snapshot, new NetherRouteSafetyContext
        {
            MaximumFloorLevel = effectiveMaxDepth,
            KnownNodeByFloorId = known,
            HardSafeByFloorId = hardSafe,
            HpSafeByFloorId = hpSafe,
        });
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
        LogAction(
            "route-selected",
            snapshot,
            string.Join(",", route.Audit.Take(16).Select(item => item.FloorId + ":" + item.Reason))
        );
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
        if (!State.TryBegin(new NetherPlannedAction(NetherActionKind.AwaitNativeFlow), snapshot))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin-battle-wait");
            return;
        }
        if (!BattleSessionAutoSL.HasActiveNetherOperation && !EnsureBattleLease())
            return;
        LogAction("await-battle", snapshot, string.Empty);
    }

    private static bool EnsureBattleLease()
    {
        if (NetherBattleSettingsLease.Instance.Phase == NetherBattleSettingsLeasePhase.Forced)
            return true;
        NetherNativeActionResult lease = NetherBattleSettingsLease.Instance.AcquireAndForce();
        if (lease.Kind == NetherNativeActionResultKind.Completed)
            return true;

        FailClosed(
            lease.Kind == NetherNativeActionResultKind.BindingUnavailable
                ? NetherPauseReason.BindingUnavailable
                : NetherPauseReason.BattleSettingsLeaseFault,
            "battle-settings-lease-at-battle-entry:" + lease.Detail
        );
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
        if (!State.TryBegin(action, snapshot))
        {
            FailClosed(NetherPauseReason.AmbiguousServerOutcome, "could-not-begin:" + action.Kind);
            return;
        }
        NetherNativeActionResult native = _bridge.Invoke(action);
        afterInvoke?.Invoke(native);
        HandleInvocationResult(snapshot, action, native, boundary);
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
        ProjectionCalibration.Clear();
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
        if (!Config.NetherAutoClimbDetailedLogging.Value)
            return;
        if (string.Equals(_lastTransition, transition, StringComparison.Ordinal))
            return;
        _lastTransition = transition;
        Logger.Info("[F12][NetherClimb] " + transition);
    }
}
