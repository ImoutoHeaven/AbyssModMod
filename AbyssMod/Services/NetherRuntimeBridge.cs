#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Absf;
using Cysharp.Threading.Tasks;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Ingame.Exploration;
using Project.Master;
using Project.Master.NoaMessagePack;
using Project.Outgame.UI;
using Project.User;

namespace AbyssMod.Services;

/// <summary>
/// Result of reading the live, server-owned Nether model.  A mapping error is deliberately
/// not converted into a partially populated snapshot: policy code must not make a decision
/// from guessed client state.
/// </summary>
internal readonly record struct NetherRuntimeSnapshotResult(NetherSnapshot? Snapshot, string Detail)
{
    public bool IsSuccess => Snapshot != null && Detail.Length == 0;

    public static NetherRuntimeSnapshotResult Success(NetherSnapshot snapshot) => new(snapshot, string.Empty);

    public static NetherRuntimeSnapshotResult Failure(string detail) => new(null, detail);
}

internal readonly record struct NetherRuntimeCodeCandidatesResult(
    IReadOnlyList<NetherCodeCandidate> Candidates,
    bool IsMasterComplete,
    string Detail
)
{
    public bool IsSuccess => Detail.Length == 0;

    public static NetherRuntimeCodeCandidatesResult Failure(string detail) => new(
        Array.Empty<NetherCodeCandidate>(),
        false,
        detail
    );
}

/// <summary>
/// The runtime boundary for F12.  No member in this interface issues a Nether endpoint
/// directly.  Mutating operations invoke an already registered game controller callback,
/// and report an exact binding failure when that callback is not available.
/// </summary>
internal interface INetherRuntimeBridge : INetherRuntimeParentDriver, INetherReadOnlyReconcileDriver,
    INetherBattleIngressDriver, INetherBattleSettlementDriver, INetherBattleProjectionSnapshotDriver,
    INetherContinueSceneDriver, INetherBattleResultCodeDriver, INetherRecoveredCodeOfferDriver
{
    bool HasRegisteredFloorSelection { get; }

    bool HasObservedNetherBattleResult { get; }

    bool IsBattleActive { get; }

    bool IsResultObserved { get; }

    NetherRuntimeSnapshotResult TryCaptureSnapshot();

    /// <summary>Read-only master/HP/code inputs for the production combat route gate.</summary>
    NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors);

    /// <summary>
    /// Copies every current interactive FloorSelection model into a complete fail-closed
    /// pre-entry proof.  This is read-only and must never cause a controller callback/API call.
    /// </summary>
    NetherRuntimeInteractivePreEntryInputsResult TryCaptureInteractivePreEntryInputs(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings
    );

    NetherRuntimePopupResult TryGetActivePopup();

    /// <summary>Begins the owner identity before the reflected floor-click parent task starts.</summary>
    bool BeginFloorParent(NetherPlannedAction action, long generation);

    /// <summary>Drives exactly one popup registered by the current floor-click parent.</summary>
    NetherNativeActionResult InvokeOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    );

    void TerminateFloorParent();

    NetherNativeActionResult Reconcile();

    NetherNativeActionResult Invoke(NetherPlannedAction action);

    NetherNativeActionResult PollNativeFlow();

    /// <summary>
    /// Starts local observation of a Continue scene handoff before invoking the native parent.
    /// This carries no server request and fails when there is no exact current FloorSelection
    /// owner/generation to bind.
    /// </summary>
    bool TryBeginContinueSceneHandoff(out long ownerGeneration);

    /// <summary>
    /// Reads only the current live NetherDataStore/master cache and never invokes a native
    /// Continue callback.  A pause result must prevent the parent from being started.
    /// </summary>
    NetherCheckpointReturnPreflightDecision PreflightContinueReturn(NetherPlannedAction action);

    NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items);

    bool TryConsumeResultSuccess();

    NetherNativeActionResult PollResultFlow();

    NetherBattleResultContinuationStep PollBattleResultContinuation(bool allowInvoke);

    void ClearRegistrations();
}

/// <summary>
/// Reflection-only bindings for the versioned IL2CPP game surface.  The bridge has no
/// fallback to NetherApiDataStore: exact name, arity, parameter type and return type are
/// selected before an action is invoked.  This keeps a patched client from turning an
/// unknown build into a raw-request automation client.
/// </summary>
internal sealed class NetherRuntimeBridge : NetherOwnedPopupStageBridgeAdapter, INetherRuntimeBridge, INetherCheckpointPopupWaitDriver, INetherOwnedPopupNativeStagePort
{
    private const string UniTaskTypeName = "Cysharp.Threading.Tasks.UniTask";
    private const string UnitTypeName = "UniRx.Unit";
    private const string NetherUtilityTypeName = "Project.Nether.NetherUtility";
    private const string NetherPartyModelTypeName = "Project.Nether.NetherPartyModel";

    private const string FloorSelectionSceneTypeName = "Project.Nether.FloorSelection.SubScene";
    private const string FloorSelectionTypeName = "Project.Nether.FloorSelection.SubViewController";
    private const string EventPopupControllerTypeName = "Project.Nether.NetherEventPopup.NetherEventPopupController";
    private const string RecoverPopupControllerTypeName = "Project.Nether.NetherRecoverPopup.NetherRecoverPopupController";
    private const string TreasurePopupControllerTypeName = "Project.Nether.NetherTreasurePopup.NetherTreasurePopupController";
    private const string ShopPopupControllerTypeName = "Project.Nether.NetherShopPopup.NetherShopPopupController";
    private const string ShopConfirmPopupControllerTypeName =
        "Project.Nether.NetherShopConfirmPopup.NetherShopConfirmPopupController";
    private const string CodeSelectPopupControllerTypeName = "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController";
    private const string CodeListPopupControllerTypeName = "Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopupController";
    private const string CodeTransformConfirmPopupControllerTypeName =
        "Project.Nether.AbyssCodeChangePopup.AbyssCodeChangePopupController";
    private const string CodeTransformCompletePopupControllerTypeName =
        "Project.Nether.AbyssCodeChangeCompletePopup.AbyssCodeChangeCompletePopupController";
    private const string ReturnPopupControllerTypeName = "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnItemSelectionPopupController";
    private const string ReturnScrollControllerTypeName = "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnableItemScrollViewController";
    private const string ContinuePopupControllerTypeName = "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopupController";
    private const string BoostPopupControllerTypeName = "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopupController";
    private const string ContentAcquiredPopupControllerTypeName =
        "Project.Nether.NetherContentAcquiredPopup.NetherContentAcquiredPopupController";
    private const string FloorEventHintPopupControllerTypeName =
        "Project.Nether.NetherFloorEventHintBox.NetherFloorEventHintBoxPopupController";
    private static readonly NetherReturnItemPolicy ReturnItemPolicy = new();
    private static readonly NetherCheckpointReturnPreflight CheckpointReturnPreflight = new();
    private static readonly NetherFloorMasterBoundsMapper FloorMasterBoundsMapper = new();
    private static readonly NetherRuntimeActivePartyHpExtractor ActivePartyHpExtractor = new();
    private static readonly NetherRuntimeActiveCodeErosionExtractor ActiveCodeErosionExtractor = new();
    private static readonly NetherRuntimeInteractivePreEntryInputCapture InteractivePreEntryInputCapture = new();
    private readonly NetherResultSceneCoordinator _resultScene = new();
    private readonly NetherBattleResultContinuationFlow _battleResultContinuation = new();
    private readonly NetherTransitionSnapshotCache _transitionSnapshotCache = new();
    private readonly NetherNativeWaitGate _battleResultSnapshotWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeSelectionTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeReplacementPopupWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeKeepCancelTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeTransformTaskWait = new(maximumMissingPolls: 600);
    private const string NetherApiServiceTypeName = "Project.Ingame.Exploration.NetherAPIService";
    private const string ResultControllerTypeName = "Project.NetherTop.Result.SubViewController";
    private const string BottomRightViewTypeName = "Project.Ingame.BottomRightView";
    private const string PopupBaseTypeName = "Project.PopupBase";
    private const string MonoBehaviourWithUniTaskTypeName = "Absf.MonoBehaviourWithUniTask";

    private readonly object _gate = new();
    private readonly NetherPopupOwnershipRegistry _popupOwnership = new();
    private readonly NetherFloorEventSequenceTaskFlow _floorEventSequenceTaskFlow =
        new(maximumMissingPolls: 600);
    private readonly NetherRecoveredFloorEventTaskLease _recoveredFloorEventTaskLease = new();
    private readonly NetherFloorEventSequenceTaskFlow _recoveredFloorEventSequenceTaskFlow =
        new(maximumMissingPolls: 600);
    private readonly NetherContentAcquiredConfirmLease _contentAcquiredConfirmLease = new();
    private readonly NetherContentAcquiredConfirmLease _floorEventHintConfirmLease = new();
    private readonly NetherNativeWaitGate _battleStartTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherStartStatusParentCapture _startStatusParentCapture = new();
    private object? _floorSelectionController;
    private long _runtimeGeneration;
    private long _startStatusCodeGeneration;
    private object? _battleResultViewController;
    private long _battleResultCodeGeneration;
    private bool _battleResultCharactersRequired;
    private bool _continueFloorOwnerTerminated;
    private NetherPlannedAction? _floorParentAction;
    private long _floorParentGeneration;
    private PopupRegistration? _eventPopup;
    private PopupRegistration? _recoverPopup;
    private PopupRegistration? _treasurePopup;
    private PopupRegistration? _shopPopup;
    private PopupRegistration? _shopConfirmPopup;
    private long _shopConfirmParentSequence;
    private PopupRegistration? _codeSelectPopup;
    private PopupRegistration? _codeListPopup;
    private PopupRegistration? _codeTransformConfirmPopup;
    private PopupRegistration? _codeTransformCompletePopup;
    private PopupRegistration? _returnPopup;
    private PopupRegistration? _continuePopup;
    private PopupRegistration? _boostPopup;
    private PopupRegistration? _floorEventHintPopup;
    private CheckpointControllerRegistration? _returnScrollController;
    private object? _nativeActionTask;
    private object? _codeKeepCancelTask;
    private object? _codeSelectionTask;
    private object? _codeTransformTask;
    private bool _battleActive;
    private bool _battleClearObserved;
    private bool _battleCloseObserved;
    private bool _battleStartExpected;
    private object? _battleStartTask;
    private object? _battleClearTask;
    private object? _battleCloseTask;
    private NetherPlannedAction? _pendingCheckpointAction;
    private readonly NetherCheckpointNativeFlow _checkpointFlow = new();
    private readonly NetherNativeWaitGate _checkpointParentTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _checkpointTerminalTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherCheckpointPopupWaitCoordinator _checkpointPopupWait;
    private object? _checkpointParentTask;
    private object? _checkpointChildTask;
    private long _checkpointGenerationCounter;
    private long _checkpointOwnerGeneration;
    private long _checkpointMinimumSequence;
    private readonly NetherCodeSelectionNativeFlow _codeSelectionFlow = new();
    private long _popupSequence;

    public static NetherRuntimeBridge Instance { get; } = new();

    public bool HasRegisteredFloorSelection
    {
        get
        {
            lock (_gate)
                return _floorSelectionController != null;
        }
    }

    public bool FloorOwnerTerminated
    {
        get
        {
            lock (_gate)
                return _continueFloorOwnerTerminated;
        }
    }

    public long CurrentRuntimeGeneration
    {
        get
        {
            lock (_gate)
                return NetherRuntimeGenerationVisibility.ForLiveFloorSelection(
                    _floorSelectionController,
                    _runtimeGeneration
                );
        }
    }

    public bool IsExpectedNetherTopScene
    {
        get
        {
            lock (_gate)
            {
                return _floorSelectionController != null
                    && string.Equals(
                        _floorSelectionController.GetType().FullName,
                        FloorSelectionTypeName,
                        StringComparison.Ordinal
                    );
            }
        }
    }

    public bool IsBattleActive
    {
        get
        {
            lock (_gate)
                return _battleActive;
        }
    }

    public bool IsF11Busy => BattleSessionAutoSL.HasActiveNetherOperation;

    public bool IsResultObserved
    {
        get
        {
            lock (_gate)
                return _resultScene.IsResultObserved;
        }
    }

    private NetherRuntimeBridge()
    {
        _checkpointPopupWait = new NetherCheckpointPopupWaitCoordinator(this);
    }

    // Static registration entry points are deliberately small; Harmony only reports native
    // lifecycle boundaries and never makes an automation decision from a postfix.
    public static void RegisterFloorSelection(object controller) =>
        Instance.RegisterFloorSelectionCore(controller, "direct-registration");

    public static void UnregisterFloorSelection(object controller) => Instance.UnregisterFloorSelectionCore(controller);

    public static void RegisterCodePopup(object controller, object popup) => Instance.RegisterPopupCore(controller, popup, null);

    public static void RegisterReturnPopup(object controller, object popup) => Instance.RegisterPopupCore(controller, popup, null);

    public static void ObserveBattleStart() => Instance.ObserveBattleStartCore();

    public static void ObserveBattleClear() => Instance.ObserveBattleClearCore();

    public static void ObserveBattleClose() => Instance.ObserveBattleCloseCore();

    public static void ObserveBattleResultCharacters(object characters) =>
        Instance.ObserveBattleResultCharactersCore(characters);

    public static void ObserveBattleStartTask(object task) =>
        Instance.ObserveBattleTaskCore(BattleTaskKind.Start, task);

    public static void ObserveBattleClearTask(object task) =>
        Instance.ObserveBattleTaskCore(BattleTaskKind.Clear, task);

    public static void ObserveBattleCloseTask(object task) =>
        Instance.ObserveBattleTaskCore(BattleTaskKind.Close, task);

    public static void ObserveResult() => Instance.ObserveResultCore(null);

    public static void ObserveResult(object resultTask) => Instance.ObserveResultCore(resultTask);

    public static void ObserveBattleResultView(object controller, object initializeTask) =>
        Instance.ObserveBattleResultViewCore(controller, initializeTask);

    /// <summary>
    /// Captures the exact native task that owns an interactive floor popup through its final
    /// confirmation.  The earlier OnFloorClickedEventAsync return only proves that the game
    /// scheduled its UniTask.Void movement callback and is not a settlement boundary.
    /// </summary>
    public static void ObserveFloorEventSequenceTask(object controller, object sequenceTask) =>
        Instance.ObserveFloorEventSequenceTaskCore(controller, sequenceTask);

    /// <summary>
    /// Observes the exact generated confirmation task behind an Abyss code-offer Receive click.
    /// The task is started by the native controller callback and is never synthesized here.
    /// </summary>
    public static void ObserveStartStatusStateMachineEnter(object stateMachine) =>
        Instance.ObserveStartStatusStateMachineEnterCore(stateMachine);

    public static void ObserveStartStatusStateMachineExit(object stateMachine) =>
        Instance.ObserveStartStatusStateMachineExitCore(stateMachine);

    public static void ObserveCodeSelectionTask(object resultTask) => Instance.ObserveCodeSelectionTaskCore(resultTask);

    /// <summary>
    /// Observes only the exact static generated UniTask spawned by the native code-offer
    /// cancel closure.  The controller argument is used to correlate that task to the live
    /// owner/generation/sequence/epoch that invoked b__12_0.
    /// </summary>
    public static void ObserveCodeKeepCancelTask(object controller, object resultTask) =>
        Instance.ObserveCodeKeepCancelTaskCore(controller, resultTask);

    /// <summary>
    /// Correlates the generated target_type=7 conversion task with the exact Change-list
    /// controller and selected pre-conversion code.  Unrelated manual conversions are ignored.
    /// </summary>
    public static void ObserveCodeTransformTask(object controller, object beforeCodeId, object resultTask) =>
        Instance.ObserveCodeTransformTaskCore(controller, beforeCodeId, resultTask);

    internal static IEnumerable<MethodBase> GetPatchTargets()
    {
        foreach (NetherInteropPatchBinding binding in NetherLifecycleInteropBindings.All)
        {
            Type? type = ResolveLoadedType(binding.TypeName);
            if (type == null)
            {
                NetherAutoClimbController.LogDiagnostic(
                    "binding",
                    new("family", "lifecycle"),
                    new("outcome", "missing-type"),
                    new("type", binding.TypeName),
                    new("method", binding.Method.Name)
                );
                continue;
            }

            MethodInfo? method = TryResolveExactMethod(type, binding.Method, binding.Flags, out string error);
            if (method != null)
            {
                NetherAutoClimbController.LogDiagnostic(
                    "binding",
                    new("family", "lifecycle"),
                    new("outcome", "resolved"),
                    new("type", binding.TypeName),
                    new("method", method.Name)
                );
                yield return method;
            }
            else
            {
                NetherAutoClimbController.LogDiagnostic(
                    "binding",
                    new("family", "lifecycle"),
                    new("outcome", "missing-method"),
                    new("type", binding.TypeName),
                    new("method", binding.Method.Name),
                    new("detail", error)
                );
            }
        }
    }

    public bool HasRecoveredCodeOffer
    {
        get
        {
            lock (_gate)
            {
                return _startStatusCodeGeneration > 0
                    && _startStatusParentCapture.IsReady(_floorSelectionController);
            }
        }
    }

    public bool HasObservedNetherBattleResult
    {
        get
        {
            lock (_gate)
                return _battleResultContinuation.HasObservation;
        }
    }

    internal static MethodBase? GetStartStatusStateMachinePatchTarget()
    {
        NetherInteropPatchBinding binding =
            NetherLifecycleInteropBindings.StartStatusStateMachineMoveNext;
        Type? type = ResolveLoadedType(binding.TypeName);
        if (type == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "binding",
                new("family", "start-status-state-machine"),
                new("outcome", "missing-type"),
                new("type", binding.TypeName),
                new("method", binding.Method.Name)
            );
            return null;
        }

        MethodInfo? method = TryResolveExactMethod(
            type,
            binding.Method,
            binding.Flags,
            out string error
        );
        NetherAutoClimbController.LogDiagnostic(
            "binding",
            new("family", "start-status-state-machine"),
            new("outcome", method != null ? "resolved" : "missing-method"),
            new("type", binding.TypeName),
            new("method", binding.Method.Name),
            new("detail", method != null ? "exact-signature" : error)
        );
        return method;
    }

    internal static MethodBase? GetCodeSelectionTaskPatchTarget()
    {
        Type? type = ResolveLoadedType(NetherUtilityTypeName);
        if (type == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "binding",
                new("family", "code-confirm-task"),
                new("outcome", "missing-type"),
                new("type", NetherUtilityTypeName)
            );
            return null;
        }
        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            type,
            NetherCodePopupNativeBinding.ConfirmTaskBinding(CodeSelectPopupControllerTypeName),
            out string error,
            out MethodInfo? method
        );
        NetherAutoClimbController.LogDiagnostic(
            "binding",
            new("family", "code-confirm-task"),
            new("outcome", resolved ? "resolved" : "missing-method"),
            new("type", NetherUtilityTypeName),
            new("method", method?.Name ?? "confirm-generated-task"),
            new("detail", error)
        );
        return resolved ? method : null;
    }

    internal static MethodBase? GetCodeKeepCancelTaskPatchTarget()
    {
        Type? type = ResolveLoadedType(NetherUtilityTypeName);
        if (type == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "binding",
                new("family", "code-keep-task"),
                new("outcome", "missing-type"),
                new("type", NetherUtilityTypeName)
            );
            return null;
        }
        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            type,
            NetherCodePopupNativeBinding.CancelTaskBinding(CodeSelectPopupControllerTypeName),
            out string error,
            out MethodInfo? method
        );
        NetherAutoClimbController.LogDiagnostic(
            "binding",
            new("family", "code-keep-task"),
            new("outcome", resolved ? "resolved" : "missing-method"),
            new("type", NetherUtilityTypeName),
            new("method", method?.Name ?? "cancel-generated-task"),
            new("detail", error)
        );
        return resolved ? method : null;
    }

    internal static MethodBase? GetCodeTransformTaskPatchTarget()
    {
        Type? type = ResolveLoadedType(NetherUtilityTypeName);
        if (type == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "binding",
                new("family", "code-transform-task"),
                new("outcome", "missing-type"),
                new("type", NetherUtilityTypeName)
            );
            return null;
        }
        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            type,
            NetherCodeTransformNativeBinding.TransformTaskBinding(CodeListPopupControllerTypeName),
            out string error,
            out MethodInfo? method
        );
        NetherAutoClimbController.LogDiagnostic(
            "binding",
            new("family", "code-transform-task"),
            new("outcome", resolved ? "resolved" : "missing-method"),
            new("type", NetherUtilityTypeName),
            new("method", method?.Name ?? "transform-generated-task"),
            new("detail", error)
        );
        return resolved ? method : null;
    }

    internal static void ObservePatchedCall(MethodBase originalMethod, object instance, object[] arguments)
    {
        if (originalMethod == null || instance == null)
            return;

        string typeName = originalMethod.DeclaringType?.FullName ?? string.Empty;
        string methodName = originalMethod.Name;
        if (typeName == FloorSelectionSceneTypeName
            && methodName is "OnInitializeAsync" or "OnRefreshAsync" or "OnEntered")
        {
            string source = methodName switch
            {
                "OnInitializeAsync" => "subscene-initialize",
                "OnRefreshAsync" => "subscene-refresh",
                _ => "subscene-entered",
            };
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "floor-selection-scene-observed"),
                new("source", source),
                new("method", methodName),
                new("sceneType", instance.GetType().FullName ?? instance.GetType().Name),
                new("argumentCount", arguments.Length.ToString())
            );

            object? controller = null;
            string extraction = "none";
            object? argument = arguments.Length > 0 ? arguments[0] : null;
            if (argument != null
                && string.Equals(argument.GetType().FullName, FloorSelectionTypeName, StringComparison.Ordinal))
            {
                controller = argument;
                extraction = "argument-0";
            }

            bool memberReadable = false;
            object? member = null;
            if (controller == null)
            {
                memberReadable = TryReadMember(instance, "_subViewController", out member);
                if (member != null
                    && string.Equals(member.GetType().FullName, FloorSelectionTypeName, StringComparison.Ordinal))
                {
                    controller = member;
                    extraction = "scene-member";
                }
            }

            if (controller != null)
            {
                Instance.RegisterFloorSelectionCore(controller, source + ":" + extraction);
            }
            else
            {
                NetherAutoClimbController.LogDiagnostic(
                    "runtime-lifecycle",
                    new("action", "floor-selection-register-failed"),
                    new("source", source),
                    new("reason", "missing-floor-controller"),
                    new("argument0Type", argument?.GetType().FullName ?? "null"),
                    new("sceneMemberReadable", memberReadable.ToString()),
                    new("sceneMemberType", member?.GetType().FullName ?? "null")
                );
            }
            return;
        }

        if (typeName == FloorSelectionTypeName && methodName == "HandleStartEventByStatusAsync")
        {
            Instance.RegisterFloorSelectionCore(instance, "controller-status-hook");
            return;
        }

        if (typeName == FloorSelectionTypeName && methodName == "Project_ISubService_Terminate")
        {
            UnregisterFloorSelection(instance);
            return;
        }

        if (typeName == BottomRightViewTypeName)
        {
            if (methodName == "ApplyUserSettings" && arguments.Length == 1 && arguments[0] != null)
                NetherBattleSettingsNativeRegistry.Register(instance, arguments[0]);
            else if (methodName == "OnDestroy")
                NetherBattleSettingsNativeRegistry.Unregister(instance);
            return;
        }

        // Project.PopupBase declares these exact lifecycle methods in the packaged client.
        // They are the only common close boundary for all Nether popup subclasses, so an old
        // close animation can invalidate only its own registered instance.
        if (typeName == PopupBaseTypeName && methodName is "Close" or "ImmediatelyClose")
        {
            Instance.InvalidatePopupCore(instance);
            return;
        }

        if (typeName == MonoBehaviourWithUniTaskTypeName && methodName == "OnDestroy")
        {
            Instance.InvalidatePopupCore(instance);
            return;
        }

        if (typeName == ReturnScrollControllerTypeName && (methodName == "InitializeView" || methodName == "OnThumbnailClicked"))
        {
            Instance.RegisterReturnScrollCore(instance);
            return;
        }

        if (methodName != "SetupPopupEvent" || arguments.Length < 1 || arguments[0] == null)
            return;

        object? close = arguments.Length >= 2 ? arguments[1] : null;
        Instance.RegisterPopupCore(instance, arguments[0], close);
    }

    public NetherRuntimeSnapshotResult TryCaptureSnapshot()
    {
        object? floorSelection;
        lock (_gate)
            floorSelection = _floorSelectionController;

        if (floorSelection == null)
            return NetherRuntimeSnapshotResult.Failure("missing-floor-selection-controller");
        if (!TryReadMember(floorSelection, "_netherModel", out object? model) || model == null)
            return NetherRuntimeSnapshotResult.Failure("missing-floor-selection-nether-model");

        try
        {
            UserData? userData = Engine.Get<UserData>();
            if (userData == null)
                return NetherRuntimeSnapshotResult.Failure("missing-user-data");
            NetherDataStore? dataStore = userData.NetherDataStore;
            if (dataStore == null || dataStore.NetherData == null || dataStore.NetherPointData == null)
                return NetherRuntimeSnapshotResult.Failure("missing-nether-data-store");

            NetherData data = dataStore.NetherData;
            NetherPointData pointData = dataStore.NetherPointData;
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            if (masterDataStore == null)
                return NetherRuntimeSnapshotResult.Failure("missing-master-data-store");

            if (!TryReadInt(model, "MNetherId", out long netherId)
                || !TryReadInt(model, "MNetherMapId", out long mapId)
                || !TryReadInt32(model, "StatusType", out int statusValue)
                || !TryReadInt32(model, "ErosionPoint", out int erosionPoint)
                || !TryReadInt32(model, "NetherGold", out int netherGold)
                || !TryReadInt32(model, "TreasureKey", out int treasureKey))
            {
                return NetherRuntimeSnapshotResult.Failure("missing-nether-model-member");
            }

            if (!TryMapFloors(model, out IReadOnlyList<NetherFloorNode>? floors, out string mapError))
                return NetherRuntimeSnapshotResult.Failure(mapError);
            if (!TryMapCurrentFloor(
                    model,
                    data,
                    floors!,
                    out long currentFloorId,
                    out long currentNodeId,
                    out int floorLevel,
                    out int floorIndex,
                    out string floorError
                ))
            {
                return NetherRuntimeSnapshotResult.Failure(floorError);
            }
            if (!TryMapCharacters(model, out IReadOnlyList<NetherCharacterState>? characters, out string characterError))
                return NetherRuntimeSnapshotResult.Failure(characterError);
            if (!TryLoadMasterRows(masterDataStore, mapId, out MasterRows? rows, out string masterError))
                return NetherRuntimeSnapshotResult.Failure(masterError);
            if (!TryMapCodes(dataStore, rows!, out IReadOnlyList<NetherCodeState>? codes, out string codeError))
                return NetherRuntimeSnapshotResult.Failure(codeError);
            if (!TryMapAcquiredItems(dataStore, rows!, out IReadOnlyList<NetherRewardItem>? acquiredItems, out string itemError))
                return NetherRuntimeSnapshotResult.Failure(itemError);

            NetherSessionStatus status = ToSessionStatus(statusValue);
            NetherContinuationTarget? continuationTarget = status == NetherSessionStatus.Sleep
                ? TryMapContinuationTarget(masterDataStore, data, currentFloorId)
                : null;

            NetherSnapshot snapshot = new()
            {
                Status = status,
                NetherId = netherId,
                MapId = mapId,
                CurrentFloorId = currentFloorId,
                CurrentNodeId = currentNodeId,
                FloorLevel = floorLevel,
                FloorIndex = floorIndex,
                MaxFloorLevel = data.MaxFloorLevel,
                ContinuanceFloorLevel = data.ContinuanceFloorLevel,
                MasterMaxFloorLevel = rows!.Map.MaxFloorFloorNumber,
                ErosionPoint = erosionPoint,
                TicketCount = dataStore.GetTicketCount(),
                SignalCount = dataStore.GetSignalCount(),
                TreasureKeyCount = treasureKey,
                NetherGold = netherGold,
                CodeReloadCount = data.CodeReload,
                CodeCapacity = pointData.MaxNetherCode,
                LockReward = pointData.LockReward,
                ContinuationTarget = continuationTarget,
                Characters = characters!,
                Codes = codes!,
                Floors = floors!,
                AcquiredItems = acquiredItems!,
                CharacterHpHash = CreateCharacterHash(characters!),
                CodeHash = CreateCodeHash(codes!),
                MapHash = CreateMapHash(floors!),
            };
            _transitionSnapshotCache.ObserveFullSnapshot(snapshot);
            lock (_gate)
                _battleResultCharactersRequired = false;
            return NetherRuntimeSnapshotResult.Success(snapshot);
        }
        catch (Exception ex)
        {
            return NetherRuntimeSnapshotResult.Failure(
                "snapshot-map-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    /// <summary>
    /// Exposes only the exact raw fields needed by route safety: the server map node's
    /// MNetherMapFloorId is matched against MNetherMapFloors.id, then its min/max erosion rows
    /// are passed to the fail-closed mapper.  No floor order or neighboring-master inference is
    /// permitted here.
    /// </summary>
    internal static NetherFloorMasterBounds TryMapRuntimeFloorMasterBounds(
        long runtimeFloorMasterId,
        MNetherMapFloors[]? masterRows
    )
    {
        if (masterRows == null)
            return FloorMasterBoundsMapper.Map(runtimeFloorMasterId, null);

        var rawRows = new List<NetherFloorMasterBoundsRow>(masterRows.Length);
        foreach (MNetherMapFloors row in masterRows)
        {
            if (row == null)
            {
                rawRows.Add(new NetherFloorMasterBoundsRow(0, 0, 0) { HasRequiredFields = false });
                continue;
            }
            rawRows.Add(new NetherFloorMasterBoundsRow(
                row.id,
                row.min_erosion_point,
                row.max_erosion_point
            ));
        }
        return FloorMasterBoundsMapper.Map(runtimeFloorMasterId, rawRows);
    }

    /// <summary>
    /// Reads the current live FloorSelection NetherModel's PartyModel/CharacterModels health
    /// surface.  RO evidence establishes that each <c>HpRatio</c> is supplied from the
    /// authoritative <c>NetherCharacterEntity.current_hp_ratio</c> (default 1000), and that
    /// <c>IsAlive</c> is available on the same native model.  No guessed current/max field is
    /// used; missing, invalid, duplicate, or non-finite runtime values remain fail-closed.
    /// </summary>
    internal static NetherActivePartyHpSafety TryMapRuntimeActivePartyHpSafety(object? netherModel)
    {
        return ActivePartyHpExtractor.Extract(netherModel);
    }

    /// <summary>
    /// Captures exactly the runtime values consumed by the production combat route coordinator:
    /// every server-rendered floor ID is joined to MNetherMapFloors, party HP is read from the
    /// live FloorSelection NetherModel, and possession code effects are read from live store
    /// plus MNetherCodes.  No endpoint or floor action is issued here.
    /// </summary>
    public NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors)
    {
        if (floors == null)
            return UnknownRouteSafety("missing-server-floor-graph");

        object? floorSelection;
        lock (_gate)
            floorSelection = _floorSelectionController;
        if (floorSelection == null
            || !TryReadMember(floorSelection, "_netherModel", out object? netherModel)
            || netherModel == null)
        {
            return UnknownRouteSafety("missing-floor-selection-nether-model");
        }

        try
        {
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            MNetherMapFloors[]? floorMasters = masterDataStore?.GetCache<MNetherMapFloors>();
            var bounds = new Dictionary<long, NetherFloorMasterBounds>();
            foreach (NetherFloorNode floor in floors)
            {
                if (floor == null || floor.FloorId <= 0 || floor.NodeId <= 0 || !bounds.TryAdd(
                        floor.NodeId,
                        TryMapRuntimeFloorMasterBounds(floor.FloorId, floorMasters)
                    ))
                {
                    return UnknownRouteSafety("invalid-or-duplicate-runtime-floor-id");
                }
            }

            return new NetherRuntimeRouteSafetyData
            {
                FloorBoundsByFloorId = bounds,
                ActivePartyHp = TryMapRuntimeActivePartyHpSafety(netherModel),
                ActiveCodeErosion = TryCaptureActiveCodeErosionProjection(),
                Detail = string.Empty,
            };
        }
        catch (Exception ex)
        {
            return UnknownRouteSafety("route-safety-runtime-exception:" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Copies every live interactive FloorSelection model and its exact master rows into the
    /// fail-closed pre-entry evaluator.  Route selection consumes only an all-or-nothing
    /// result; partial or stale capture therefore remains unavailable rather than changing F12
    /// behaviour permissively.
    /// </summary>
    public NetherRuntimeInteractivePreEntryInputsResult TryCaptureInteractivePreEntryInputs(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings
    )
    {
        if (snapshot == null || settings == null)
            return NetherRuntimeInteractivePreEntryInputsResult.Failure("missing-interactive-preentry-snapshot-or-settings");

        object? floorSelection;
        lock (_gate)
            floorSelection = _floorSelectionController;
        if (floorSelection == null
            || !TryReadMember(floorSelection, "_netherModel", out object? netherModel)
            || netherModel == null
            || !TryReadMember(netherModel, "MapModel", out object? mapModel)
            || mapModel == null
            || !TryReadMember(mapModel, "FloorModelListPerFloorLevel", out object? perLevel)
            || perLevel == null)
        {
            return NetherRuntimeInteractivePreEntryInputsResult.Failure("missing-runtime-interactive-floor-model-list");
        }

        var expected = new Dictionary<long, NetherFloorNode>();
        foreach (NetherFloorNode node in snapshot.Floors ?? Array.Empty<NetherFloorNode>())
        {
            if (node == null || !IsInteractiveFloorKind(node.NodeType))
                continue;
            if (node.FloorId <= 0 || node.NodeId <= 0 || !expected.TryAdd(node.NodeId, node))
                return NetherRuntimeInteractivePreEntryInputsResult.Failure("invalid-or-duplicate-snapshot-interactive-floor");
        }
        if (expected.Count == 0)
            return NetherRuntimeInteractivePreEntryInputsResult.Success(new Dictionary<long, NetherRuntimeInteractivePreEntryCaptureResult>());

        MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
        MNetherMapFloors[]? mapRows = masterDataStore?.GetCache<MNetherMapFloors>();
        MNetherFloorEvents[]? eventRows = masterDataStore?.GetCache<MNetherFloorEvents>();
        MNetherFloorEventParts[]? eventPartRows = masterDataStore?.GetCache<MNetherFloorEventParts>();
        IReadOnlyList<int>? activeHp = snapshot.Characters == null
            ? null
            : snapshot.Characters.Where(character => character.IsActive).Select(character => character.HpPermille).ToArray();
        bool canCloseShop = HasExactShopCloseBinding();
        var captured = new Dictionary<long, NetherRuntimeInteractivePreEntryCaptureResult>();

        foreach (object levelFloors in EnumerateDictionaryValues(perLevel))
        {
            foreach (object floor in Enumerate(levelFloors))
            {
                if (!TryReadInt32(floor, "FloorType", out int rawFloorType))
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure("missing-runtime-interactive-floor-type");
                if (!IsInteractiveFloorKind(ToFloorNodeType(rawFloorType)))
                    continue;

                if (!TryReadRuntimeFloorNodeIdentity(floor, out long runtimeNodeId, out string identityError))
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure(identityError);

                NetherRuntimeInteractivePreEntryCaptureResult result = InteractivePreEntryInputCapture.Capture(
                    new NetherRuntimeInteractivePreEntryCaptureRequest(
                        FloorModel: floor,
                        MapFloorRows: mapRows,
                        EventRows: eventRows,
                        EventPartRows: eventPartRows,
                        CurrentErosion: snapshot.ErosionPoint,
                        ActiveHpPermille: activeHp,
                        CurrentNetherGold: snapshot.NetherGold,
                        CurrentTreasureKeys: snapshot.TreasureKeyCount,
                        Settings: settings,
                        CanCloseShop: canCloseShop
                    )
                    {
                        CurrentCodes = snapshot.Codes ?? Array.Empty<NetherCodeState>(),
                        CodeCapacity = snapshot.CodeCapacity,
                    }
                );
                if (!result.IsCaptured || result.Input == null)
                {
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure(
                        "interactive-preentry-capture:" + result.Detail
                    );
                }
                long floorMasterId = result.Input.FloorMasterId;
                if (!expected.TryGetValue(runtimeNodeId, out NetherFloorNode? snapshotNode)
                    || snapshotNode.FloorId != floorMasterId
                    || snapshotNode.NodeType != result.Input.FloorKind)
                {
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure(
                        "runtime-snapshot-interactive-floor-mismatch:node=" + runtimeNodeId + ":master=" + floorMasterId
                    );
                }
                if (!captured.TryAdd(runtimeNodeId, result))
                {
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure(
                        "duplicate-runtime-interactive-node:" + runtimeNodeId
                    );
                }
            }
        }

        if (captured.Count != expected.Count)
            return NetherRuntimeInteractivePreEntryInputsResult.Failure("missing-runtime-interactive-floor-capture");
        return NetherRuntimeInteractivePreEntryInputsResult.Success(captured);
    }

    /// <summary>
    /// Reads the live possession store and complete MNetherCodes cache for battle erosion only.
    /// This path never uses the Safe/Risk code-ID policy mapping: IDs 30024/40024 are projected
    /// solely from their exact master effect type and parameters.  It is read-only and not yet
    /// a Controller action; a failed extraction deliberately leaves the future route gate
    /// unknown rather than producing a zero modifier.
    /// </summary>
    public NetherActiveCodeErosionProjection TryCaptureActiveCodeErosionProjection()
    {
        try
        {
            UserData? userData = Engine.Get<UserData>();
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            NetherDataStore? dataStore = userData?.NetherDataStore;
            MNetherCodes[]? masterRows = masterDataStore?.GetCache<MNetherCodes>();
            if (dataStore == null)
                return NetherActiveCodeErosionProjectionMapper.Unknown("missing-nether-code-data-store");

            return ActiveCodeErosionExtractor.Extract(
                dataStore.GetPossessionNetherCodeDataEnumerable(),
                masterRows
            );
        }
        catch (Exception ex)
        {
            return NetherActiveCodeErosionProjectionMapper.Unknown(
                "active-code-erosion-extraction-exception:" + ex.GetType().Name
            );
        }
    }

    private static NetherRuntimeRouteSafetyData UnknownRouteSafety(string detail) => new()
    {
        FloorBoundsByFloorId = new Dictionary<long, NetherFloorMasterBounds>(),
        ActivePartyHp = NetherActivePartyHpSafetyMapper.Unknown(detail),
        ActiveCodeErosion = NetherActiveCodeErosionProjectionMapper.Unknown(detail),
        Detail = detail,
    };

    /// <summary>
    /// The packaged Shop controller exposes <c>SetupPopupEvent(NetherShopPopup, Action)</c>
    /// and its generated <c>b__16_0(Unit, Action)</c> invokes that Action.  We require both
    /// exact signatures before a future pre-entry ShopOff proof can claim a close capability;
    /// the later popup action still requires the actual registered Action instance.
    /// </summary>
    private static bool HasExactShopCloseBinding()
    {
        Type? controllerType = ResolveLoadedType(ShopPopupControllerTypeName);
        if (controllerType == null)
            return false;
        NetherInteropPatchBinding setupBinding = NetherLifecycleInteropBindings.All.Single(binding =>
            binding.TypeName == ShopPopupControllerTypeName
            && binding.Method.Name == "SetupPopupEvent"
        );
        if (!TryResolveExactMethod(
                controllerType,
                setupBinding.Method,
                setupBinding.Flags,
                out _,
                out _
            ))
        {
            return false;
        }

        return NetherCodePopupInteropResolver.TryResolveGeneratedCallbackTarget(
            controllerType,
            NetherLifecycleInteropBindings.ShopCloseCallback,
            out _,
            out _,
            out _
        );
    }

    private static bool IsInteractiveFloorKind(NetherFloorNodeType type) => type is
        NetherFloorNodeType.Event or NetherFloorNodeType.Recovery or NetherFloorNodeType.Shop or NetherFloorNodeType.Treasure;

    /// <summary>
    /// Captures the carry-out contract from the same live store that native
    /// HandleGameClearedIfNeededAsync reads before it opens a return popup.  This method is
    /// deliberately data-only: it does not call HandleStartEventByStatusAsync, a popup
    /// callback, or NetherApiDataStore.
    /// </summary>
    public NetherCheckpointReturnPreflightDecision PreflightContinueReturn(NetherPlannedAction action)
    {
        if (action.Kind != NetherActionKind.Continue)
            return ReturnPreflightPause("non-continue-preflight-action:" + action.Kind);
        if (action.TicketCount != 1 || action.TicketCost != 1)
        {
            return new NetherCheckpointReturnPreflightDecision
            {
                Kind = NetherCheckpointReturnPreflightKind.Pause,
                PauseReason = NetherPauseReason.InvalidConfiguration,
                Detail = "continue-preflight-requires-exact-one-ticket",
            };
        }

        try
        {
            UserData? userData = Engine.Get<UserData>();
            NetherDataStore? dataStore = userData?.NetherDataStore;
            NetherPointData? pointData = dataStore?.NetherPointData;
            if (dataStore == null || pointData == null)
                return ReturnPreflightPause("missing-live-nether-store-or-point-data");

            var preserveIds = new HashSet<long>(action.ReturnPreserveItemIds ?? Array.Empty<long>());
            int lockReward = pointData.LockReward;
            if (lockReward == 0)
            {
                // The native <= 0 LockReward branch calls its normal one-ticket Continue path
                // without creating the return popup.  Do not require irrelevant item masters.
                return CheckpointReturnPreflight.Evaluate(
                    0,
                    Array.Empty<NetherCheckpointReturnPreflightItem>(),
                    preserveIds
                );
            }
            if (lockReward < 0)
            {
                return new NetherCheckpointReturnPreflightDecision
                {
                    Kind = NetherCheckpointReturnPreflightKind.Pause,
                    PauseReason = NetherPauseReason.InvalidConfiguration,
                    Detail = "negative-live-lock-reward:" + lockReward,
                };
            }

            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            if (!TryMapAuthoritativeReturnPreflightItems(
                    dataStore,
                    masterDataStore,
                    out IReadOnlyList<NetherCheckpointReturnPreflightItem>? items,
                    out string mappingError
                ))
            {
                return ReturnPreflightPause(mappingError);
            }

            return CheckpointReturnPreflight.Evaluate(lockReward, items!, preserveIds);
        }
        catch (Exception ex)
        {
            return ReturnPreflightPause(
                "continue-return-preflight-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates()
    {
        try
        {
            UserData? userData = Engine.Get<UserData>();
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            if (userData?.NetherDataStore?.NetherData == null || masterDataStore == null)
                return NetherRuntimeCodeCandidatesResult.Failure("missing-code-candidate-data");

            NetherDataStore dataStore = userData.NetherDataStore;
            NetherData data = dataStore.NetherData;
            MNetherCodes[]? rows = masterDataStore.GetCache<MNetherCodes>();
            if (rows == null || rows.Length == 0)
                return NetherRuntimeCodeCandidatesResult.Failure("missing-m-nether-codes-cache");
            var masterById = rows.Where(row => row != null).ToDictionary(row => row.id);

            object? selectableCodeIds = data.SelectableNetherCodeIds;
            if (selectableCodeIds == null)
                return NetherRuntimeCodeCandidatesResult.Failure("missing-selectable-nether-code-ids");
            if (!NetherRuntimeEnumerableReader.TryRead(
                    selectableCodeIds,
                    out List<object> rawSelectableCodeIds,
                    out string selectableCodeIdsError
                ))
            {
                return NetherRuntimeCodeCandidatesResult.Failure(
                    "invalid-selectable-nether-code-id-collection:" + selectableCodeIdsError
                );
            }

            bool detailedLogging = Config.NetherAutoClimbDetailedLogging?.Value ?? false;
            Dictionary<long, MNetherCodes>? semanticAuditMasters = detailedLogging
                ? new Dictionary<long, MNetherCodes>()
                : null;
            var candidates = new List<NetherCodeCandidate>();
            foreach (object rawCodeId in rawSelectableCodeIds)
            {
                if (!TryConvertInt64(rawCodeId, out long codeId) || codeId <= 0)
                    return NetherRuntimeCodeCandidatesResult.Failure("invalid-selectable-nether-code-id");
                if (!masterById.TryGetValue(codeId, out MNetherCodes? row))
                    return NetherRuntimeCodeCandidatesResult.Failure("missing-m-nether-code:" + codeId);
                NetherCodeCandidate candidate = NetherCodeRuntimeSemanticMapper.MapCandidate(
                    row.id,
                    row.category,
                    row.effect_type,
                    LevelFromMaster(row),
                    row.rarity
                );
                if (NetherCodeRuntimeSemanticMapper.RequiresBoundedSemanticAudit(candidate)
                    && semanticAuditMasters != null)
                    semanticAuditMasters[row.id] = row;
                candidates.Add(candidate);
            }

            // Technique/Strength are valid ordinary rewards.  Their optional Rush/Impact,
            // party-coverage, and research labels are not present in MNetherCodes, so retain a
            // bounded semantic audit without turning those rewards into unsupported data.
            if (semanticAuditMasters != null)
            {
                foreach (object rawCode in Enumerate(dataStore.GetPossessionNetherCodeDataEnumerable()))
                {
                    if (rawCode is NetherCodeData code
                        && code != null
                        && masterById.TryGetValue(code.MNetherCodeId, out MNetherCodes? master))
                    {
                        NetherCodeState current = NetherCodeRuntimeSemanticMapper.MapState(
                            master.id,
                            master.category,
                            master.effect_type,
                            LevelFromMaster(master),
                            master.rarity
                        );
                        if (!current.IsKnown || current.EffectKind == NetherCodeEffectKind.General)
                            semanticAuditMasters[master.id] = master;
                    }
                }
                LogCodeMasterSemanticAudit(semanticAuditMasters.Values, detailedLogging);
            }

            return new NetherRuntimeCodeCandidatesResult(candidates, candidates.All(candidate => candidate.IsKnown), string.Empty);
        }
        catch (Exception ex)
        {
            return NetherRuntimeCodeCandidatesResult.Failure(
                "code-candidate-map-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    public NetherRuntimePopupResult TryGetActivePopup()
    {
        PopupRegistration? registration;
        lock (_gate)
        {
            registration = null;
            foreach (PopupRegistration? candidate in new PopupRegistration?[]
                {
                    _eventPopup,
                    _recoverPopup,
                    _treasurePopup,
                    _shopPopup,
                    _codeSelectPopup,
                    _codeListPopup,
                    _returnPopup,
                    _continuePopup,
                })
            {
                if (candidate is { IsLive: true }
                    && (registration == null || candidate.Value.Sequence > registration.Value.Sequence))
                    registration = candidate;
            }
        }
        if (registration == null)
            return NetherRuntimePopupResult.Failure("missing-active-native-popup");

        return TryMapPopupRegistration(registration.Value);
    }

    public bool BeginFloorParent(NetherPlannedAction action, long generation)
    {
        if (action.Kind != NetherActionKind.SelectFloor || generation < 1)
            return false;
        lock (_gate)
        {
            if (_floorParentAction != null)
                return false;
            _floorParentAction = action;
            _floorParentGeneration = generation;
            _floorEventSequenceTaskFlow.Reset();
            _contentAcquiredConfirmLease.Reset();
            _floorEventHintConfirmLease.Reset();
            _floorEventHintPopup = null;
            if (!_floorEventSequenceTaskFlow.Begin())
            {
                _floorParentAction = null;
                _floorParentGeneration = 0;
                return false;
            }
            _battleStartExpected = action.BattleProjection != null;
            _battleStartTask = null;
            _battleStartTaskWait.Clear();
            _popupOwnership.BeginOwner(NetherActionKind.SelectFloor, generation);
            return true;
        }
    }

    public void TerminateFloorParent()
    {
        lock (_gate)
        {
            if (_floorParentAction == null)
                return;
            ClearFloorParentCore();
        }
    }

    public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent)
    {
        PopupRegistration? registration;
        lock (_gate)
        {
            if (parent.Kind != NetherActionKind.SelectFloor
                || _floorParentAction != parent
                || _floorParentGeneration < 1
                || !_popupOwnership.TryGetOwned(NetherActionKind.SelectFloor, _floorParentGeneration, out NetherPopupOwnership ownership))
            {
                return NetherRuntimePopupResult.Failure("missing-owned-floor-popup");
            }
            registration = FindPopupRegistration(ownership);
        }
        return registration == null
            ? NetherRuntimePopupResult.Failure("owned-floor-popup-registration-lost")
            : TryMapPopupRegistration(registration.Value);
    }

    protected override bool HasMatchingOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup
    )
    {
        lock (_gate)
        {
            NetherActionKind ownerAction;
            long ownerGeneration;
            if (parent.Kind == NetherActionKind.SelectFloor
                && _floorParentAction == parent
                && _floorParentGeneration > 0)
            {
                ownerAction = NetherActionKind.SelectFloor;
                ownerGeneration = _floorParentGeneration;
            }
            else if (parent.Kind == NetherActionKind.BattleSettlement
                && _battleResultCodeGeneration > 0
                && _battleResultContinuation.HasObservation
                && !_battleResultContinuation.NextInvoked)
            {
                ownerAction = NetherActionKind.BattleSettlement;
                ownerGeneration = _battleResultCodeGeneration;
            }
            else if (parent.Kind == NetherActionKind.RecoveredCodeOffer
                && _startStatusCodeGeneration > 0
                && _startStatusParentCapture.IsReady(_floorSelectionController))
            {
                ownerAction = NetherActionKind.RecoveredCodeOffer;
                ownerGeneration = _startStatusCodeGeneration;
            }
            else
            {
                return false;
            }

            if (popup.OwnerAction != ownerAction
                || popup.OwnerGeneration != ownerGeneration
                || !_popupOwnership.TryGetOwned(
                    ownerAction,
                    ownerGeneration,
                    out NetherPopupOwnership ownership
                )
                || ownership.Sequence != popup.Sequence
                || FindPopupRegistration(ownership) == null)
            {
                return false;
            }
        }
        return true;
    }

    protected override NetherNativeActionResult InvokeOwnedEventOption(NetherPlannedAction action) =>
        SelectEventOption(action);

    protected override NetherNativeActionResult InvokeOwnedLeaveShop() => LeaveShop();

    protected override NetherNativeActionResult InvokeOwnedSelectCode(NetherPlannedAction action) =>
        SelectCode(action);

    public NetherNativeActionResult PollFloorParent()
    {
        lock (_gate)
            return PollFloorParentNativeFlow();
    }

    private NetherNativeActionResult PollFloorParentTask()
    {
        lock (_gate)
        {
            if (_floorParentAction == null)
                return NetherNativeActionResult.Completed("no-floor-parent");

            NetherNativeActionResult result = _floorEventSequenceTaskFlow.Pump(PollResultTask);
            if (result.Kind == NetherNativeActionResultKind.Completed)
                ClearFloorParentCore();
            return result;
        }
    }

    private NetherRuntimePopupResult TryMapPopupRegistration(PopupRegistration registration)
    {
        string controllerType = registration.Controller.GetType().FullName ?? string.Empty;
        long decisionEpoch = 0;
        if (controllerType == CodeSelectPopupControllerTypeName)
        {
            lock (_gate)
            {
                decisionEpoch = GetOwnedPopupDecisionEpoch(new NetherOwnedPopupStageOwner(
                    registration.OwnerAction,
                    registration.OwnerGeneration,
                    registration.Sequence,
                    0
                ));
            }
        }
        try
        {
            NetherRuntimePopupResult mapped = controllerType switch
            {
                CodeSelectPopupControllerTypeName => NetherRuntimePopupResult.Success(new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer }),
                CodeListPopupControllerTypeName => TryMapCodeListPopup(registration),
                ContinuePopupControllerTypeName => NetherRuntimePopupResult.Success(new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Continue }),
                ReturnPopupControllerTypeName => NetherRuntimePopupResult.Success(new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.ReturnItems }),
                EventPopupControllerTypeName => TryMapEventPopup(registration, NetherRuntimePopupKind.Event, (int)NetherFloorNodeType.Event),
                RecoverPopupControllerTypeName => TryMapEventPopup(registration, NetherRuntimePopupKind.Recovery, (int)NetherFloorNodeType.Recovery),
                TreasurePopupControllerTypeName => TryMapEventPopup(registration, NetherRuntimePopupKind.Treasure, (int)NetherFloorNodeType.Treasure),
                ShopPopupControllerTypeName => TryMapShopPopup(registration),
                _ => NetherRuntimePopupResult.Failure("unsupported-native-popup-controller:" + controllerType),
            };
            return !mapped.IsSuccess
                ? mapped
                : NetherRuntimePopupResult.Success(mapped.Popup! with
                {
                    OwnerAction = registration.OwnerAction,
                    OwnerGeneration = registration.OwnerGeneration,
                    Sequence = registration.Sequence,
                    DecisionEpoch = decisionEpoch,
                });
        }
        catch (Exception ex)
        {
            return NetherRuntimePopupResult.Failure("popup-map-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    public NetherNativeActionResult Reconcile()
    {
        return BeginGetOnlyRefresh();
    }

    /// <summary>
    /// Binds the public native datastore sync flow, whose packaged ISIL is the no-Start chain
    /// <c>SyncNetherDataAsync → RequestNetherAsyncInternal → RequestNetherAsync → Apply</c>.
    /// It is intentionally not the floor controller's CreateNetherModelAsync, which contains
    /// a NotPlayed branch to RequestNetherStartAsync.
    /// </summary>
    public NetherNativeActionResult BeginGetOnlyRefresh()
    {
        UserData? userData = Engine.Get<UserData>();
        NetherDataStore? dataStore = userData?.NetherDataStore;
        if (dataStore == null)
            return NetherNativeActionResult.BindingUnavailable("missing-live-nether-data-store");

        return TryInvokeExact(
            dataStore,
            NetherReadOnlyReconcileNativeBinding.SyncDescriptor,
            new object[] { new CancellationToken() },
            "read-only-nether-sync"
        );
    }

    public NetherNativeActionResult PollGetOnlyRefresh() => PollNativeFlow();

    public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot()
    {
        NetherRuntimeSnapshotResult captured = TryCaptureSnapshot();
        if (!captured.IsSuccess
            && string.Equals(
                captured.Detail,
                "missing-floor-selection-controller",
                StringComparison.Ordinal
            ))
        {
            captured = TryCaptureTransitionSnapshot();
        }
        return captured.IsSuccess
            ? NetherReadOnlySnapshotResult.Success(captured.Snapshot!)
            : NetherReadOnlySnapshotResult.Failure(captured.Detail);
    }

    public NetherRuntimeSnapshotResult TryCaptureBattleResultCodeSnapshot()
    {
        NetherReadOnlySnapshotResult captured = TryCaptureAppliedSnapshot();
        return captured.IsSuccess
            ? NetherRuntimeSnapshotResult.Success(captured.Snapshot!)
            : NetherRuntimeSnapshotResult.Failure(captured.Detail);
    }

    public NetherRuntimePopupResult TryGetBattleResultCodePopup()
    {
        PopupRegistration? registration;
        long generation;
        lock (_gate)
        {
            registration = _codeSelectPopup;
            generation = _battleResultCodeGeneration;
        }
        if (registration is not PopupRegistration candidate
            || !candidate.IsLive
            || generation <= 0
            || candidate.OwnerAction != NetherActionKind.BattleSettlement
            || candidate.OwnerGeneration != generation)
        {
            return NetherRuntimePopupResult.Failure(
                "missing-live-battle-result-code-popup:generation=" + generation
            );
        }
        return TryMapPopupRegistration(candidate);
    }

    public NetherNativeActionResult InvokeBattleResultCode(
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    )
    {
        NetherNativeActionResult result = InvokeOwnedPopup(
            new NetherPlannedAction(NetherActionKind.BattleSettlement),
            popup,
            action
        );
        NetherAutoClimbController.LogDiagnostic(
            "battle-result-code-native",
            new("stage", "invoke"),
            new("action", action.Kind.ToString()),
            new("codeId", action.CodeId.ToString()),
            new("replaceCodeId", action.ReplaceCodeId.ToString()),
            new("ownerGeneration", popup.OwnerGeneration.ToString()),
            new("sequence", popup.Sequence.ToString()),
            new("decisionEpoch", popup.DecisionEpoch.ToString()),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail)
        );
        return result;
    }

    public NetherBattleResultCodeNativeStep PollBattleResultCodeNative()
    {
        lock (_gate)
        {
            NetherOwnedPopupStageParentGate staged = PumpOwnedPopupStagesBeforeParent();
            if (!staged.MayPollParent)
            {
                if (staged.Native.Kind == NetherNativeActionResultKind.Started
                    && string.Equals(
                        staged.Native.Detail,
                        "code-reload-fresh-offer-ready",
                        StringComparison.Ordinal
                    ))
                {
                    return LogBattleResultCodeNativeStep(
                        NetherBattleResultCodeNativeStep.ReloadReady(staged.Native.Detail)
                    );
                }
                return LogBattleResultCodeNativeStep(
                    staged.Native.Kind == NetherNativeActionResultKind.Started
                        ? NetherBattleResultCodeNativeStep.Pending(staged.Native.Detail)
                        : staged.Native.Kind == NetherNativeActionResultKind.BindingUnavailable
                            ? NetherBattleResultCodeNativeStep.BindingUnavailable(staged.Native.Detail)
                            : NetherBattleResultCodeNativeStep.Faulted(staged.Native.Detail)
                );
            }

            if (_codeSelectionFlow.Stage is not (
                    NetherCodeSelectionNativeStage.Idle
                    or NetherCodeSelectionNativeStage.Completed
                ) || _codeSelectionTask != null)
            {
                NetherNativeActionResult selected = PollCodeSelectionFlow();
                return LogBattleResultCodeNativeStep(selected.Kind switch
                {
                    NetherNativeActionResultKind.Started =>
                        NetherBattleResultCodeNativeStep.Pending(selected.Detail),
                    NetherNativeActionResultKind.Completed =>
                        NetherBattleResultCodeNativeStep.Completed(selected.Detail),
                    NetherNativeActionResultKind.BindingUnavailable =>
                        NetherBattleResultCodeNativeStep.BindingUnavailable(selected.Detail),
                    _ => NetherBattleResultCodeNativeStep.Faulted(selected.Detail),
                });
            }

            return LogBattleResultCodeNativeStep(
                NetherBattleResultCodeNativeStep.Completed("battle-result-code-native-terminal")
            );
        }
    }

    public NetherRuntimeSnapshotResult TryCaptureRecoveredCodeSnapshot() =>
        TryCaptureSnapshot();

    public NetherRuntimeCodeCandidatesResult TryGetRecoveredCodeCandidates() =>
        TryGetCodeCandidates();

    public NetherRuntimePopupResult TryGetRecoveredCodePopup()
    {
        PopupRegistration? registration;
        long generation;
        lock (_gate)
        {
            registration = _codeSelectPopup;
            generation = _startStatusCodeGeneration;
        }
        if (registration is not PopupRegistration candidate
            || !candidate.IsLive
            || generation <= 0
            || candidate.OwnerAction != NetherActionKind.RecoveredCodeOffer
            || candidate.OwnerGeneration != generation)
        {
            return NetherRuntimePopupResult.Failure(
                "missing-live-recovered-code-popup:generation=" + generation
            );
        }
        return TryMapPopupRegistration(candidate);
    }

    public NetherNativeActionResult InvokeRecoveredCode(
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    )
    {
        NetherNativeActionResult result = InvokeOwnedPopup(
            new NetherPlannedAction(NetherActionKind.RecoveredCodeOffer),
            popup,
            action
        );
        NetherAutoClimbController.LogDiagnostic(
            "recovered-code-native",
            new("stage", "invoke"),
            new("action", action.Kind.ToString()),
            new("codeId", action.CodeId.ToString()),
            new("replaceCodeId", action.ReplaceCodeId.ToString()),
            new("ownerGeneration", popup.OwnerGeneration.ToString()),
            new("sequence", popup.Sequence.ToString()),
            new("decisionEpoch", popup.DecisionEpoch.ToString()),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail)
        );
        return result;
    }

    public NetherBattleResultCodeNativeStep PollRecoveredCodeNative()
    {
        NetherBattleResultCodeNativeStep step = PollBattleResultCodeNative();
        NetherAutoClimbController.LogDiagnostic(
            "recovered-code-native",
            new("stage", "poll"),
            new("outcome", step.Kind.ToString()),
            new("detail", step.Detail)
        );
        return step;
    }

    public NetherNativeActionResult PollRecoveredCodeParent()
    {
        object? task;
        long generation;
        lock (_gate)
        {
            generation = _startStatusCodeGeneration;
            if (generation <= 0
                || !_startStatusParentCapture.TryGetParentTask(
                    _floorSelectionController,
                    out task
                ))
            {
                return NetherNativeActionResult.BindingUnavailable(
                    "recovered-code-parent-owner-lost"
                );
            }
        }

        NetherNativeActionResult result = PollResultTask(task!);
        NetherAutoClimbController.LogDiagnostic(
            "recovered-code-parent",
            new("ownerGeneration", generation.ToString()),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail)
        );
        return result;
    }

    public NetherNativeActionResult BeginRecoveredCodeRefresh()
    {
        NetherNativeActionResult result = BeginGetOnlyRefresh();
        NetherAutoClimbController.LogDiagnostic(
            "recovered-code-refresh",
            new("stage", "begin"),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail)
        );
        return result;
    }

    public NetherNativeActionResult PollRecoveredCodeRefresh()
    {
        NetherNativeActionResult result = PollGetOnlyRefresh();
        NetherAutoClimbController.LogDiagnostic(
            "recovered-code-refresh",
            new("stage", "poll"),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail)
        );
        return result;
    }

    public NetherRuntimeSnapshotResult TryCaptureRecoveredCodeAppliedSnapshot() =>
        TryCaptureSnapshot();

    public void CompleteRecoveredCodeOffer()
    {
        long generation;
        lock (_gate)
        {
            generation = _startStatusCodeGeneration;
            ClearRecoveredCodeOfferCore();
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "recovered-code-owner-completed"),
            new("ownerGeneration", generation.ToString())
        );
    }

    private NetherRuntimeSnapshotResult TryCaptureTransitionSnapshot()
    {
        try
        {
            UserData? userData = Engine.Get<UserData>();
            NetherDataStore? dataStore = userData?.NetherDataStore;
            NetherData? data = dataStore?.NetherData;
            NetherPointData? pointData = dataStore?.NetherPointData;
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            if (dataStore == null || data == null || pointData == null || masterDataStore == null)
                return NetherRuntimeSnapshotResult.Failure("missing-transition-datastore-or-master");
            if (!TryLoadMasterRows(
                    masterDataStore,
                    data.MNetherMapId,
                    out MasterRows? rows,
                    out string masterError
                ))
            {
                return NetherRuntimeSnapshotResult.Failure("transition-master:" + masterError);
            }
            if (!TryMapCodes(
                    dataStore,
                    rows!,
                    out IReadOnlyList<NetherCodeState>? codes,
                    out string codeError
                ))
            {
                return NetherRuntimeSnapshotResult.Failure("transition-codes:" + codeError);
            }
            if (!TryMapAcquiredItems(
                    dataStore,
                    rows!,
                    out IReadOnlyList<NetherRewardItem>? items,
                    out string itemError
                ))
            {
                return NetherRuntimeSnapshotResult.Failure("transition-items:" + itemError);
            }

            bool requireFreshCharacters;
            lock (_gate)
                requireFreshCharacters = _battleResultCharactersRequired;
            NetherSessionStatus status = ToSessionStatus((int)data.Status);
            var state = new NetherAuthoritativeTransitionState
            {
                Status = status,
                NetherId = data.MNetherId,
                MapId = data.MNetherMapId,
                CurrentFloorId = data.MNetherMapFloorId,
                FloorLevel = data.FloorLevel,
                FloorIndex = data.FloorIndex,
                MaxFloorLevel = data.MaxFloorLevel,
                ContinuanceFloorLevel = data.ContinuanceFloorLevel,
                ErosionPoint = data.ErosionPoint,
                TicketCount = dataStore.GetTicketCount(),
                SignalCount = dataStore.GetSignalCount(),
                TreasureKeyCount = data.TreasureKey,
                NetherGold = data.NetherGold,
                CodeReloadCount = data.CodeReload,
                CodeCapacity = pointData.MaxNetherCode,
                LockReward = pointData.LockReward,
                ContinuationTarget = status == NetherSessionStatus.Sleep
                    ? TryMapContinuationTarget(masterDataStore, data, data.MNetherMapFloorId)
                    : null,
                Codes = codes!,
                AcquiredItems = items!,
            };
            NetherRuntimeSnapshotResult result = _transitionSnapshotCache.TryCompose(
                state,
                requireFreshCharacters
            );
            NetherAutoClimbController.LogDiagnostic(
                "transition-snapshot",
                new("outcome", result.IsSuccess ? "mapped" : "failed"),
                new("status", status.ToString()),
                new("netherId", data.MNetherId.ToString()),
                new("mapId", data.MNetherMapId.ToString()),
                new("floorId", data.MNetherMapFloorId.ToString()),
                new("floorLevel", data.FloorLevel.ToString()),
                new("apiFloorIndex", data.FloorIndex.ToString()),
                new("floorResolution", status == NetherSessionStatus.Battle
                    && data.MNetherMapFloorId == 0
                    && result.IsSuccess
                        ? "unique-coordinate-fallback"
                        : "exact-master-coordinate"),
                new("resolvedFloorId", result.Snapshot?.CurrentFloorId.ToString() ?? "0"),
                new("requireFreshCharacters", requireFreshCharacters.ToString()),
                new("codeCount", codes!.Count.ToString()),
                new("itemCount", items!.Count.ToString()),
                new("detail", result.Detail)
            );
            return result;
        }
        catch (Exception ex)
        {
            return NetherRuntimeSnapshotResult.Failure(
                "transition-snapshot-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    private static NetherBattleResultCodeNativeStep LogBattleResultCodeNativeStep(
        NetherBattleResultCodeNativeStep step
    )
    {
        NetherAutoClimbController.LogDiagnostic(
            "battle-result-code-native",
            new("stage", "poll"),
            new("outcome", step.Kind.ToString()),
            new("detail", step.Detail)
        );
        return step;
    }

    public NetherNativeActionResult Invoke(NetherPlannedAction action) => action.Kind switch
    {
        NetherActionKind.Reconcile => Reconcile(),
        NetherActionKind.SelectFloor => SelectFloor(action),
        NetherActionKind.SelectEventOption => SelectEventOption(action),
        NetherActionKind.LeaveShop => LeaveShop(),
        // A buy has no safe standalone semantics: it must retain its SelectFloor parent,
        // exact popup close delegate, and child task.  Recovered/direct Wait paths therefore
        // fail closed instead of bypassing the staged owner contract.
        NetherActionKind.BuyShopItem => NetherNativeActionResult.BindingUnavailable("shop-buy-requires-owned-floor-parent"),
        NetherActionKind.SelectCode => SelectCode(action),
        // A reroll must retain the registered SelectFloor owner and same-popup epoch.  Direct
        // recovered Wait paths cannot prove that, so they pause rather than call RerollAsync.
        NetherActionKind.ReloadCode => NetherNativeActionResult.BindingUnavailable("reload-code-requires-owned-floor-parent"),
        // The generated cancel task is correlated to an owned SelectFloor parent.  Direct
        // recovered Wait state has no such task/owner evidence and must not invoke b__12_0.
        NetherActionKind.KeepCode => NetherNativeActionResult.BindingUnavailable("keep-code-requires-owned-floor-parent"),
        NetherActionKind.Continue => Continue(action),
        NetherActionKind.FinishAtCheckpoint => FinishAtCheckpoint(),
        _ => NetherNativeActionResult.Rejected("unsupported-native-action:" + action.Kind),
    };

    public bool TryBeginContinueSceneHandoff(out long ownerGeneration)
    {
        lock (_gate)
        {
            ownerGeneration = 0;
            if (_floorSelectionController == null || _runtimeGeneration < 1)
                return false;

            // This is a per-action latch.  It is reset before the native callback so the
            // following exact ISubService.Terminate belongs to this continuation, and stays
            // latched through the later new-controller registration for the coordinator.
            _continueFloorOwnerTerminated = false;
            ownerGeneration = _runtimeGeneration;
            return true;
        }
    }

    public NetherNativeActionResult PollContinueParent()
    {
        lock (_gate)
        {
            if (_pendingCheckpointAction?.Kind != NetherActionKind.Continue)
                return NetherNativeActionResult.BindingUnavailable("missing-continue-native-parent");

            // PollCheckpointFlow drives only the already-started exact controller sequence
            // (Continue/Boost/Return and its parent UniTask); it never starts a second action.
            return PollCheckpointFlow();
        }
    }

    public NetherNativeActionResult PollNativeFlow()
    {
        lock (_gate)
        {
            if (_floorParentAction != null)
                return PollFloorParentNativeFlow();

            if (_pendingCheckpointAction != null)
                return PollCheckpointFlow();

            if (_recoveredFloorEventSequenceTaskFlow.IsActive)
            {
                NetherNativeActionResult contentConfirm = ConfirmContentAcquiredPopupIfNeeded(
                    recovered: true
                );
                if (contentConfirm.Kind != NetherNativeActionResultKind.Completed)
                    return contentConfirm;

                NetherNativeActionResult hintConfirm = ConfirmFloorEventHintPopupIfNeeded(
                    recovered: true
                );
                if (hintConfirm.Kind != NetherNativeActionResultKind.Completed)
                    return hintConfirm;

                NetherNativeActionResult recovered = _recoveredFloorEventSequenceTaskFlow.Pump(
                    PollResultTask
                );
                if (recovered.Kind != NetherNativeActionResultKind.Started)
                {
                    _contentAcquiredConfirmLease.Reset();
                    _floorEventHintConfirmLease.Reset();
                }
                return recovered;
            }

            if (_codeSelectionFlow.Stage is not (NetherCodeSelectionNativeStage.Idle or NetherCodeSelectionNativeStage.Completed)
                || _codeSelectionTask != null)
            {
                return PollCodeSelectionFlow();
            }

            if (_resultScene.HasResultEvidence)
                return ToNativeResult(_resultScene.Pump(PollResultTask));

            if (_nativeActionTask != null)
            {
                NetherNativeActionResult result = PollResultTask(_nativeActionTask);
                if (result.Kind != NetherNativeActionResultKind.Started)
                    _nativeActionTask = null;
                return result;
            }
        }

        return NetherNativeActionResult.Completed("no-pending-native-flow");
    }

    private NetherNativeActionResult PollFloorParentNativeFlow()
    {
        NetherNativeActionResult contentConfirm = ConfirmContentAcquiredPopupIfNeeded(
            recovered: false
        );
        if (contentConfirm.Kind != NetherNativeActionResultKind.Completed)
            return contentConfirm;

        NetherNativeActionResult hintConfirm = ConfirmFloorEventHintPopupIfNeeded(
            recovered: false
        );
        if (hintConfirm.Kind != NetherNativeActionResultKind.Completed)
            return hintConfirm;

        NetherOwnedPopupStageParentGate ownedStage = PumpOwnedPopupStagesBeforeParent();
        if (!ownedStage.MayPollParent)
        {
            // RerollAsync retains the same popup, but its shared entry gate returns a strictly
            // newer epoch before the parent may be observed.  RuntimeFlow must redispatch
            // Select/Keep from that fresh offer first.
            return ownedStage.Native;
        }

        // Code confirmation has its own native task because the Receive callback enters a
        // replacement popup before the original OnFloorClickedEventAsync parent can finish.
        if (_codeSelectionFlow.Stage is not (NetherCodeSelectionNativeStage.Idle or NetherCodeSelectionNativeStage.Completed)
            || _codeSelectionTask != null)
        {
            NetherNativeActionResult code = PollCodeSelectionFlow();
            if (code.Kind != NetherNativeActionResultKind.Completed)
                return code;
        }

        // Event/Treasure callbacks can be UniTask.Void.  Do not treat their return as a
        // settlement: wait for the owning floor parent task below.
        if (_nativeActionTask != null)
        {
            NetherNativeActionResult child = PollResultTask(_nativeActionTask);
            if (child.Kind == NetherNativeActionResultKind.Started)
                return child;
            _nativeActionTask = null;
            if (child.Kind != NetherNativeActionResultKind.Completed)
                return child;
        }

        return PollFloorParentTask();
    }

    private NetherNativeActionResult ConfirmContentAcquiredPopupIfNeeded(bool recovered)
    {
        NetherContentAcquiredConfirmClaim claim = recovered
            ? _contentAcquiredConfirmLease.ClaimRecovered(_runtimeGeneration)
            : _contentAcquiredConfirmLease.ClaimOwned(_floorParentGeneration);
        if (claim.Kind == NetherContentAcquiredConfirmClaimKind.None)
            return NetherNativeActionResult.Completed(claim.Detail);
        if (claim.Kind != NetherContentAcquiredConfirmClaimKind.Claimed || claim.Close == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "content-acquired-confirm-rejected"),
                new("mode", recovered ? "recovered" : "owned"),
                new("sequence", claim.Sequence.ToString()),
                new("runtimeGeneration", _runtimeGeneration.ToString()),
                new("ownerGeneration", _floorParentGeneration.ToString()),
                new("detail", claim.Detail)
            );
            return NetherNativeActionResult.BindingUnavailable(claim.Detail);
        }

        NetherNativeActionResult invoked = TryInvokeNoArgumentDelegate(
            claim.Close,
            "native-content-acquired-confirm"
        );
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "content-acquired-confirm-invoked"),
            new("mode", recovered ? "recovered" : "owned"),
            new("sequence", claim.Sequence.ToString()),
            new("runtimeGeneration", _runtimeGeneration.ToString()),
            new("ownerGeneration", _floorParentGeneration.ToString()),
            new("outcome", invoked.Kind.ToString()),
            new("detail", invoked.Detail)
        );
        return invoked;
    }

    private NetherNativeActionResult ConfirmFloorEventHintPopupIfNeeded(bool recovered)
    {
        NetherContentAcquiredConfirmClaim claim = recovered
            ? _floorEventHintConfirmLease.ClaimRecovered(_runtimeGeneration)
            : _floorEventHintConfirmLease.ClaimOwned(_floorParentGeneration);
        if (claim.Kind == NetherContentAcquiredConfirmClaimKind.None)
            return NetherNativeActionResult.Completed(claim.Detail);

        PopupRegistration? registration = _floorEventHintPopup;
        if (claim.Kind != NetherContentAcquiredConfirmClaimKind.Claimed
            || claim.Close == null
            || registration is not PopupRegistration current
            || current.Sequence != claim.Sequence)
        {
            string detail = claim.Kind != NetherContentAcquiredConfirmClaimKind.Claimed
                || claim.Close == null
                    ? claim.Detail
                    : "floor-event-hint-registration-lost";
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "floor-event-hint-confirm-rejected"),
                new("mode", recovered ? "recovered" : "owned"),
                new("sequence", claim.Sequence.ToString()),
                new("runtimeGeneration", _runtimeGeneration.ToString()),
                new("ownerGeneration", _floorParentGeneration.ToString()),
                new("detail", detail)
            );
            return NetherNativeActionResult.BindingUnavailable(detail);
        }

        // The packaged callback uses IPopupService to dismiss the visual hint box.  Calling
        // only the SetupPopupEvent close argument would complete the local awaitable without
        // reproducing this service-owned UI transition.
        NetherNativeActionResult invoked = TryInvokeVersionedGeneratedCallback(
            current.Controller,
            NetherLifecycleInteropBindings.FloorEventHintDismissCallback,
            new object?[] { null, claim.Close },
            "floor-event-hint-confirm"
        );
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "floor-event-hint-confirm-invoked"),
            new("mode", recovered ? "recovered" : "owned"),
            new("sequence", claim.Sequence.ToString()),
            new("runtimeGeneration", _runtimeGeneration.ToString()),
            new("ownerGeneration", _floorParentGeneration.ToString()),
            new("outcome", invoked.Kind.ToString()),
            new("detail", invoked.Detail)
        );
        return invoked;
    }

    bool INetherOwnedPopupNativeStagePort.IsCurrentOwnedPopup(
        NetherRuntimePopupKind kind,
        NetherOwnedPopupStageOwner owner
    ) => IsCurrentOwnedPopup(kind, owner);

    private bool IsCurrentOwnedPopup(NetherRuntimePopupKind kind, NetherOwnedPopupStageOwner owner)
    {
        if (!owner.IsValid)
            return false;

        lock (_gate)
        {
            PopupRegistration? registration = kind switch
            {
                NetherRuntimePopupKind.Shop => _shopPopup,
                NetherRuntimePopupKind.CodeOffer => _codeSelectPopup,
                NetherRuntimePopupKind.CodeTransform => _codeListPopup,
                _ => null,
            };
            bool ownerIsCurrent = owner.OwnerAction switch
            {
                NetherActionKind.SelectFloor =>
                    _floorParentAction?.Kind == NetherActionKind.SelectFloor
                    && _floorParentGeneration == owner.Generation,
                NetherActionKind.BattleSettlement =>
                    _battleResultCodeGeneration == owner.Generation
                    && _battleResultContinuation.HasObservation
                    && !_battleResultContinuation.NextInvoked,
                NetherActionKind.RecoveredCodeOffer =>
                    _startStatusCodeGeneration == owner.Generation
                    && _startStatusParentCapture.IsReady(_floorSelectionController),
                _ => false,
            };
            return ownerIsCurrent
                && registration is PopupRegistration candidate
                && candidate.IsLive
                && candidate.OwnerAction == owner.OwnerAction
                && candidate.OwnerGeneration == owner.Generation
                && candidate.Sequence == owner.Sequence
                && IsCurrentFloorOwnedPopup(candidate)
                && (kind != NetherRuntimePopupKind.CodeTransform
                    || TryReadCodeListPopupType(candidate.Controller, out int popupType) && popupType == 1);
        }
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollShopPurchaseTask(
        NetherShopPurchaseCloseOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(
                NetherRuntimePopupKind.Shop,
                new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
            ))
        {
            return NetherNativeActionResult.BindingUnavailable("shop-purchase-owner-registration-lost");
        }

        if (_nativeActionTask == null)
            return NetherNativeActionResult.BindingUnavailable("shop-purchase-missing-native-task");

        NetherNativeActionResult purchase = PollResultTask(_nativeActionTask);
        if (purchase.Kind != NetherNativeActionResultKind.Started)
            _nativeActionTask = null;
        return purchase;
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeShopPurchaseConfirm(
        NetherShopPurchaseCloseOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(
                NetherRuntimePopupKind.Shop,
                new NetherOwnedPopupStageOwner(
                    owner.OwnerAction,
                    owner.Generation,
                    owner.Sequence,
                    0
                )
            ))
        {
            return NetherNativeActionResult.BindingUnavailable(
                "shop-purchase-confirm-owner-registration-lost"
            );
        }

        PopupRegistration? registration;
        long parentSequence;
        lock (_gate)
        {
            registration = _shopConfirmPopup;
            parentSequence = _shopConfirmParentSequence;
        }
        if (registration == null)
            return NetherNativeActionResult.Started("shop-purchase-confirm-awaiting-popup");
        if (!registration.Value.IsLive
            || registration.Value.OwnerAction != owner.OwnerAction
            || registration.Value.OwnerGeneration != owner.Generation
            || parentSequence != owner.Sequence)
        {
            return NetherNativeActionResult.BindingUnavailable(
                "shop-purchase-confirm-popup-owner-mismatch"
            );
        }

        NetherNativeActionResult invoked = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherLifecycleInteropBindings.ShopPurchaseConfirmCallback,
            new object?[] { null, registration.Value.Controller },
            "native-shop-purchase-confirm"
        );
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "shop-purchase-confirm-invoked"),
            new("ownerGeneration", owner.Generation.ToString()),
            new("shopSequence", owner.Sequence.ToString()),
            new("confirmSequence", registration.Value.Sequence.ToString()),
            new("outcome", invoked.Kind.ToString()),
            new("detail", invoked.Detail)
        );
        return invoked.Kind is NetherNativeActionResultKind.Started
            or NetherNativeActionResultKind.Completed
                ? NetherNativeActionResult.Completed("shop-purchase-confirm-invoked")
                : invoked;
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeExactShopClose(
        NetherShopPurchaseCloseOwner owner
    )
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _shopPopup;
        if (registration == null
            || !IsCurrentOwnedPopup(
                NetherRuntimePopupKind.Shop,
                new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
            )
            || registration.Value.Close == null)
        {
            return NetherNativeActionResult.BindingUnavailable("shop-purchase-close-owner-registration-lost");
        }

        return TryInvokeNoArgumentDelegate(registration.Value.Close, "native-shop-purchase-close");
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollCodeReloadTask(
        NetherCodeReloadEpochOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(
                NetherRuntimePopupKind.CodeOffer,
                new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
            ))
        {
            return NetherNativeActionResult.BindingUnavailable("code-reload-owner-registration-lost");
        }

        if (_nativeActionTask == null)
            return NetherNativeActionResult.BindingUnavailable("code-reload-missing-native-task");

        NetherNativeActionResult result = PollResultTask(_nativeActionTask);
        if (result.Kind != NetherNativeActionResultKind.Started)
            _nativeActionTask = null;
        return result;
    }

    NetherCodeReloadEpochRefresh INetherOwnedPopupNativeStagePort.CaptureFreshCodeReloadOffer(
        NetherCodeReloadEpochOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(
                NetherRuntimePopupKind.CodeOffer,
                new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
            ))
        {
            return new NetherCodeReloadEpochRefresh(
                owner,
                0,
                NetherRuntimeCodeCandidatesResult.Failure("missing-live-code-offer-after-reroll")
            );
        }

        NetherRuntimeSnapshotResult snapshot = TryCaptureBattleResultCodeSnapshot();
        if (!snapshot.IsSuccess)
        {
            return new NetherCodeReloadEpochRefresh(
                owner,
                0,
                NetherRuntimeCodeCandidatesResult.Failure("code-reload-snapshot:" + snapshot.Detail)
            );
        }

        return new NetherCodeReloadEpochRefresh(
            owner,
            snapshot.Snapshot!.CodeReloadCount,
            TryGetCodeCandidates()
        );
    }

    public NetherNativeActionResult PollBattleLifecycle()
    {
        lock (_gate)
        {
            if (!TryCompleteBattleTask(ref _battleStartTask, BattleTaskKind.Start, out NetherNativeActionResult result))
                return result;
            if (!TryCompleteBattleTask(ref _battleClearTask, BattleTaskKind.Clear, out result))
                return result;
            if (!TryCompleteBattleTask(ref _battleCloseTask, BattleTaskKind.Close, out result))
                return result;
            return NetherNativeActionResult.Completed("no-pending-battle-lifecycle-task");
        }
    }

    /// <summary>
    /// Polls only the exact StartQuestAsync task expected after an F12 combat floor parent.
    /// Missing registration is bounded; a captured Pending task is never retried or canceled.
    /// </summary>
    public NetherNativeActionResult PollBattleStart()
    {
        lock (_gate)
        {
            if (!_battleStartExpected)
                return NetherNativeActionResult.BindingUnavailable("battle-start-not-expected");
            if (_battleStartTask == null)
                return _battleStartTaskWait.AwaitRegistration("battle-start");

            NetherNativeActionResult result = PollResultTask(_battleStartTask);
            if (result.Kind == NetherNativeActionResultKind.Started)
                return result;

            _battleStartTask = null;
            _battleStartExpected = false;
            _battleStartTaskWait.Clear();
            if (result.Kind == NetherNativeActionResultKind.Completed)
            {
                _transitionSnapshotCache.BeginBattle();
                _battleResultCharactersRequired = false;
                _battleActive = true;
            }
            return result;
        }
    }

    public void CancelBattleStartObservation()
    {
        lock (_gate)
        {
            _battleStartExpected = false;
            _battleStartTask = null;
            _battleStartTaskWait.Clear();
        }
    }

    public NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items)
        => SelectReturnItemsCore(items, registerCheckpointChildTask: false);

    private NetherNativeActionResult SelectReturnItemsCore(
        IReadOnlyList<NetherRewardItem> items,
        bool registerCheckpointChildTask
    )
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        PopupRegistration? registration;
        CheckpointControllerRegistration? scrollRegistration;
        lock (_gate)
        {
            registration = _returnPopup;
            scrollRegistration = _returnScrollController;
        }
        object? scroll = scrollRegistration?.Controller;
        if (registration == null || scroll == null)
            return NetherNativeActionResult.BindingUnavailable("missing-return-item-popup-or-scroll");

        if (!TryGetReturnSelectionIndexes(scroll, items, out IReadOnlyList<int>? indexes, out string mappingError))
            return NetherNativeActionResult.BindingUnavailable(mappingError);

        NetherNativeMethodDescriptor selectDescriptor = new(
            "OnThumbnailClicked",
            new[] { "System.Int32" },
            "System.Void"
        );
        NetherNativeMethodDescriptor confirmDescriptor = new(
            "OnConfirmAsync",
            new[] { registration.Value.Popup.GetType().FullName ?? string.Empty },
            UniTaskTypeName
        );
        if (!TryResolveExactMethod(scroll.GetType(), selectDescriptor, InstanceFlags, out string selectError, out MethodInfo? select))
            return NetherNativeActionResult.BindingUnavailable(selectError);
        if (!TryResolveExactMethod(registration.Value.Controller.GetType(), confirmDescriptor, InstanceFlags, out string confirmError, out MethodInfo? confirm))
            return NetherNativeActionResult.BindingUnavailable(confirmError);

        try
        {
            foreach (int index in indexes!)
                select!.Invoke(scroll, new object[] { index });
            object? result = confirm!.Invoke(registration.Value.Controller, new[] { registration.Value.Popup });
            if (registerCheckpointChildTask)
                RegisterCheckpointChildTask(result);
            else
                RegisterNativeActionTask(result);
            return NetherNativeActionResult.Started("native-return-item-confirm");
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException("return-item", ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome("return-item-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private NetherNativeActionResult SelectCheckpointReturnItems(NetherPlannedAction action)
    {
        if (action.ReturnLockReward <= 0)
            return NetherNativeActionResult.Completed("no-return-items-requested");

        PopupRegistration? returnRegistration;
        CheckpointControllerRegistration? scrollRegistration;
        lock (_gate)
        {
            returnRegistration = _returnPopup;
            scrollRegistration = _returnScrollController;
        }
        object? scroll = scrollRegistration?.Controller;
        if (returnRegistration is not PopupRegistration returnPopup
            || !IsCurrentCheckpointPopup(returnPopup, NetherCheckpointPopupKind.Return)
            || scrollRegistration == null || scroll == null
            || !IsCurrentCheckpointRegistration(scrollRegistration.Value, NetherCheckpointPopupKind.ReturnScroll))
            return NetherNativeActionResult.BindingUnavailable("missing-return-scroll-for-pristine-map");

        if (!TryMapPristineReturnItems(scroll, out IReadOnlyList<NetherRewardItem>? items, out string mappingError))
            return NetherNativeActionResult.BindingUnavailable(mappingError);

        // These are the exact private fields populated by the native return controller and
        // scroll view from NetherPointData.LockReward.  Compare both before OnConfirmAsync,
        // because that native method is the branch that calls RequestNetherContinueAsync.
        if (!TryReadInt32(returnPopup.Controller, "_maxSelectedCount", out int popupSelectionLimit)
            || !TryReadInt32(scroll, "_maxSelectedCount", out int scrollSelectionLimit))
        {
            return NetherNativeActionResult.BindingUnavailable("missing-return-popup-or-scroll-selection-limit");
        }
        if (popupSelectionLimit != scrollSelectionLimit)
        {
            return NetherNativeActionResult.BindingUnavailable(
                "return-popup-scroll-selection-limit-mismatch:" + popupSelectionLimit + ":" + scrollSelectionLimit
            );
        }
        if (action.ReturnPreflightSelectionLimit <= 0
            || action.ReturnPreflightSelectionLimit != action.ReturnLockReward
            || string.IsNullOrEmpty(action.ReturnExpectedPristineHash)
            || action.ReturnPreflightWholeEntrySelection.Count != action.ReturnPreflightSelectionLimit)
        {
            return NetherNativeActionResult.BindingUnavailable("missing-or-invalid-return-preflight-contract");
        }
        if (!TryMapFreshReturnPreflightItems(
                items!,
                out IReadOnlyList<NetherCheckpointReturnPreflightItem>? freshPreflightItems,
                out string preflightMappingError
            ))
        {
            return NetherNativeActionResult.BindingUnavailable(preflightMappingError);
        }

        var planned = new NetherCheckpointReturnPreflightDecision
        {
            Kind = NetherCheckpointReturnPreflightKind.Ready,
            SelectionLimit = action.ReturnPreflightSelectionLimit,
            ExpectedPristineHash = action.ReturnExpectedPristineHash,
            WholeEntrySelection = action.ReturnPreflightWholeEntrySelection,
        };
        NetherCheckpointReturnPreflightDecision verified = CheckpointReturnPreflight.VerifyFreshPopup(
            planned,
            popupSelectionLimit,
            freshPreflightItems!,
            new HashSet<long>(action.ReturnPreserveItemIds)
        );
        if (!CheckpointReturnPreflight.CanConfirmReturnPopup(verified))
        {
            return NetherNativeActionResult.BindingUnavailable(
                "return-popup-preflight-mismatch:" + verified.PauseReason + ":" + verified.Detail
            );
        }

        var preserveIds = new HashSet<long>(action.ReturnPreserveItemIds);
        NetherReturnItemSelection selection = ReturnItemPolicy.Select(items!, action.ReturnLockReward, preserveIds);
        if (selection.Kind == NetherReturnItemSelectionKind.Pause)
        {
            return NetherNativeActionResult.BindingUnavailable(
                "return-popup-policy:" + selection.PauseReason + ":" + selection.Detail
            );
        }

        return SelectReturnItemsCore(selection.Items, registerCheckpointChildTask: true);
    }

    public bool TryConsumeBattleClear()
    {
        lock (_gate)
        {
            bool observed = _battleClearObserved;
            _battleClearObserved = false;
            return observed;
        }
    }

    public bool TryConsumeBattleClose()
    {
        lock (_gate)
        {
            bool observed = _battleCloseObserved;
            _battleCloseObserved = false;
            return observed;
        }
    }

    public bool TryConsumeResultSuccess()
    {
        lock (_gate)
        {
            NetherResultSceneStep result = _resultScene.Pump(PollResultTask);
            if (result.Kind != NetherResultSceneStepKind.Succeeded)
                return false;
            _battleStartTask = null;
            _battleClearTask = null;
            _battleCloseTask = null;
            _pendingCheckpointAction = null;
            ClearCheckpointNativeFlow();
            return true;
        }
    }

    public NetherNativeActionResult PollResultFlow()
    {
        lock (_gate)
            return ToNativeResult(_resultScene.Pump(PollResultTask));
    }

    public NetherBattleResultContinuationStep PollBattleResultContinuation(bool allowInvoke)
    {
        lock (_gate)
        {
            NetherBattleResultContinuationStep step = _battleResultContinuation.Pump(
                PollResultTask,
                controller => TryInvokeVersionedGeneratedCallback(
                    controller,
                    NetherBattleResultNextNativeBinding.NextCallbackInterop,
                    new object?[] { null },
                    "battle-result-next"
                ),
                _floorSelectionController != null,
                _runtimeGeneration,
                allowInvoke
            );
            if (step.Kind != NetherBattleResultContinuationStepKind.Completed)
                return step;

            NetherRuntimeSnapshotResult snapshot = TryCaptureSnapshot();
            if (!snapshot.IsSuccess)
            {
                NetherNativeActionResult wait = _battleResultSnapshotWait.AwaitRegistration(
                    "battle-result-rebound-snapshot"
                );
                if (wait.Kind == NetherNativeActionResultKind.Started)
                {
                    return new(
                        NetherBattleResultContinuationStepKind.AwaitingFloorRebind,
                        "battle-result-rebound-snapshot:" + snapshot.Detail
                    );
                }

                _battleResultContinuation.Reset();
                return new(
                    NetherBattleResultContinuationStepKind.BindingUnavailable,
                    wait.Detail + ":" + snapshot.Detail
                );
            }

            NetherRuntimePopupResult popup = snapshot.Snapshot!.Status == NetherSessionStatus.Wait
                ? TryGetActivePopup()
                : new NetherRuntimePopupResult(null, string.Empty);
            if (!NetherBattleResultReboundReadiness.IsReady(
                    snapshot.Snapshot.Status,
                    popup.IsSuccess
                ))
            {
                NetherNativeActionResult wait = _battleResultSnapshotWait.AwaitRegistration(
                    "battle-result-rebound-modal"
                );
                if (wait.Kind == NetherNativeActionResultKind.Started)
                {
                    return new(
                        NetherBattleResultContinuationStepKind.AwaitingFloorRebind,
                        "battle-result-rebound-modal:" + popup.Detail
                    );
                }

                _battleResultContinuation.Reset();
                return new(
                    NetherBattleResultContinuationStepKind.BindingUnavailable,
                    wait.Detail + ":" + popup.Detail
                );
            }

            _battleResultSnapshotWait.Clear();
            _battleResultContinuation.Reset();
            return step with { Snapshot = snapshot.Snapshot };
        }
    }

    public void ClearRegistrations()
    {
        lock (_gate)
        {
            _floorSelectionController = null;
            _runtimeGeneration = 0;
            _startStatusParentCapture.Clear();
            _startStatusCodeGeneration = 0;
            _battleResultViewController = null;
            _battleResultCodeGeneration = 0;
            _battleResultCharactersRequired = false;
            _continueFloorOwnerTerminated = false;
            _floorParentAction = null;
            _floorParentGeneration = 0;
            _floorEventSequenceTaskFlow.Reset();
            _recoveredFloorEventTaskLease.Reset();
            _recoveredFloorEventSequenceTaskFlow.Reset();
            _contentAcquiredConfirmLease.Reset();
            _floorEventHintConfirmLease.Reset();
            _popupOwnership.Clear();
            _eventPopup = null;
            _recoverPopup = null;
            _treasurePopup = null;
            _shopPopup = null;
            _shopConfirmPopup = null;
            _shopConfirmParentSequence = 0;
            _codeSelectPopup = null;
            _codeListPopup = null;
            _codeTransformConfirmPopup = null;
            _codeTransformCompletePopup = null;
            _returnPopup = null;
            _continuePopup = null;
            _boostPopup = null;
            _floorEventHintPopup = null;
            _returnScrollController = null;
            _nativeActionTask = null;
            ResetOwnedPopupStages();
            ClearCodeKeepCancelFlow();
            ClearCodeSelectionFlow();
            ClearCodeTransformFlow();
            _resultScene.Reset();
            _battleResultContinuation.Reset();
            _battleResultSnapshotWait.Clear();
            _battleActive = false;
            _battleClearObserved = false;
            _battleCloseObserved = false;
            _battleStartExpected = false;
            _battleStartTask = null;
            _battleStartTaskWait.Clear();
            _battleClearTask = null;
            _battleCloseTask = null;
            ClearCheckpointNativeFlow();
            _checkpointGenerationCounter = 0;
            _popupSequence = 0;
        }
        _transitionSnapshotCache.Clear();
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new NetherAutoClimbDiagnosticField("action", "clear-registrations")
        );
    }

    private void RegisterFloorSelectionCore(object controller, string source)
    {
        if (controller == null)
            return;
        bool replaced;
        long generation;
        lock (_gate)
        {
            // HandleStartEventByStatusAsync can be observed repeatedly on the same live
            // controller.  Only an actual controller replacement advances the handoff
            // generation; a stale re-observation cannot satisfy a Continue rebind.
            replaced = !ReferenceEquals(_floorSelectionController, controller);
            if (replaced)
            {
                if (_battleResultCodeGeneration > 0)
                    ClearBattleResultCodeOwnerCore();
                ClearRecoveredCodeOfferCore();
                _runtimeGeneration = checked(_runtimeGeneration + 1);
                _recoveredFloorEventTaskLease.Reset();
                _recoveredFloorEventSequenceTaskFlow.Reset();
                _contentAcquiredConfirmLease.Reset();
                _floorEventHintConfirmLease.Reset();
                _floorEventHintPopup = null;
            }
            _floorSelectionController = controller;
            generation = _runtimeGeneration;
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", replaced ? "floor-selection-registered" : "floor-selection-reobserved"),
            new("generation", generation.ToString()),
            new("type", controller.GetType().FullName ?? controller.GetType().Name),
            new("source", source)
        );
    }

    private void UnregisterFloorSelectionCore(object controller)
    {
        if (controller == null)
            return;
        bool terminatedCurrentOwner = false;
        lock (_gate)
        {
            if (ReferenceEquals(_floorSelectionController, controller))
            {
                // FloorSelection is not the Result owner.  Normal Finish tears this scene
                // down before Result's CreateNetherResultModelAsync task is registered; clear
                // only floor-owned callbacks/tasks and retain Result evidence for the global
                // scene coordinator to poll.
                _floorSelectionController = null;
                ClearRecoveredCodeOfferCore();
                _recoveredFloorEventTaskLease.Reset();
                _recoveredFloorEventSequenceTaskFlow.Reset();
                _contentAcquiredConfirmLease.Reset();
                _floorEventHintConfirmLease.Reset();
                if (_pendingCheckpointAction?.Kind == NetherActionKind.Continue)
                {
                    // Continue owns a parent task which is expected to survive this old scene
                    // teardown.  Record the exact owner terminal boundary but do not clear its
                    // checkpoint parent/task/popup evidence here.
                    _continueFloorOwnerTerminated = true;
                }
                ClearFloorParentCore();
                _popupOwnership.Clear();
                _eventPopup = null;
                _recoverPopup = null;
                _treasurePopup = null;
                _shopPopup = null;
                _shopConfirmPopup = null;
                _shopConfirmParentSequence = 0;
                _codeSelectPopup = null;
                _codeListPopup = null;
                _floorEventHintPopup = null;
                ClearCodeSelectionFlow();
                if (_pendingCheckpointAction == null)
                {
                    _returnPopup = null;
                    _continuePopup = null;
                    _boostPopup = null;
                    _returnScrollController = null;
                    _nativeActionTask = null;
                }
                _resultScene.ObserveFloorSelectionTerminated();
                terminatedCurrentOwner = true;
            }
        }
        // Do not hold the bridge lock while the controller performs an exact persisted-settings
        // restore.  Continue/Result own their separate task evidence; this notification merely
        // marks the native scene boundary for the Auto/speed lease.
        if (terminatedCurrentOwner)
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "floor-selection-unregistered"),
                new("type", controller.GetType().FullName ?? controller.GetType().Name),
                new("continuePending", (_pendingCheckpointAction?.Kind == NetherActionKind.Continue).ToString())
            );
            NetherAutoClimbController.OnNetherFloorSelectionTerminated();
        }
        else
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "floor-selection-unregister-ignored"),
                new("reason", "stale-owner"),
                new("type", controller.GetType().FullName ?? controller.GetType().Name)
            );
        }
    }

    private void RegisterPopupCore(object controller, object popup, object? close)
    {
        if (controller == null || popup == null)
            return;

        string typeName = controller.GetType().FullName ?? string.Empty;
        bool isTransformSupportPopup = typeName is CodeTransformConfirmPopupControllerTypeName
            or CodeTransformCompletePopupControllerTypeName;
        bool isShopConfirmPopup = typeName == ShopConfirmPopupControllerTypeName;
        bool isContentAcquiredPopup = typeName == ContentAcquiredPopupControllerTypeName;
        bool isFloorEventHintPopup = typeName == FloorEventHintPopupControllerTypeName;
        bool isBattleResultCodePopup = typeName is (
            CodeSelectPopupControllerTypeName or CodeListPopupControllerTypeName
        );
        bool isFloorChildPopup = isTransformSupportPopup
            || isShopConfirmPopup
            || isContentAcquiredPopup
            || isFloorEventHintPopup;
        NetherActionKind ownerAction;
        long ownerGeneration;
        long sequence;
        bool recognized = true;
        bool recoveredTaskBound = false;
        bool contentConfirmBound = false;
        bool hintConfirmBound = false;
        lock (_gate)
        {
            ownerAction = NetherActionKind.None;
            ownerGeneration = 0;
            sequence = checked(++_popupSequence);
            if (_floorParentAction != null && _floorParentGeneration > 0)
            {
                if (isFloorChildPopup)
                {
                    // Confirmation/completion and the content-acquired acknowledgement are
                    // children of the already-owned floor sequence.  They must not replace
                    // the decision popup's dispatch identity in the registry.
                    ownerAction = NetherActionKind.SelectFloor;
                    ownerGeneration = _floorParentGeneration;
                    long childSequence = _popupOwnership.ReserveChildSequence(
                        ownerAction,
                        ownerGeneration
                    );
                    if (childSequence > 0)
                        sequence = childSequence;
                }
                else
                {
                    NetherPopupOwnership ownership = _popupOwnership.Register(
                        popup,
                        NetherActionKind.SelectFloor,
                        _floorParentGeneration
                    );
                    if (ownership.Sequence > 0)
                    {
                        ownerAction = ownership.OwnerAction;
                        ownerGeneration = ownership.Generation;
                        sequence = ownership.Sequence;
                    }
                }
                _popupSequence = Math.Max(_popupSequence, sequence);
            }
            else if (isBattleResultCodePopup
                && _battleResultCodeGeneration > 0
                && _battleResultContinuation.HasObservation
                && !_battleResultContinuation.NextInvoked)
            {
                NetherPopupOwnership ownership = _popupOwnership.Register(
                    popup,
                    NetherActionKind.BattleSettlement,
                    _battleResultCodeGeneration
                );
                if (ownership.Sequence > 0)
                {
                    ownerAction = ownership.OwnerAction;
                    ownerGeneration = ownership.Generation;
                    sequence = ownership.Sequence;
                    _popupSequence = Math.Max(_popupSequence, sequence);
                }
            }
            else if (isBattleResultCodePopup
                && _startStatusParentCapture.HasCandidateFor(_floorSelectionController))
            {
                bool parentPending = true;
                if (_startStatusParentCapture.TryGetObservedParentTask(
                        _floorSelectionController,
                        out object? observedParentTask
                    ))
                {
                    parentPending = PollResultTask(observedParentTask!).Kind
                        == NetherNativeActionResultKind.Started;
                }

                if (parentPending
                    && _startStatusParentCapture.TryAttachPopup(_floorSelectionController!))
                {
                    if (_startStatusCodeGeneration <= 0)
                    {
                        _startStatusCodeGeneration = _popupOwnership.BeginOwner(
                            NetherActionKind.RecoveredCodeOffer
                        );
                        ResetOwnedPopupStages();
                        ClearCodeSelectionFlow();
                        ClearCodeKeepCancelFlow();
                    }
                    NetherPopupOwnership ownership = _popupOwnership.Register(
                        popup,
                        NetherActionKind.RecoveredCodeOffer,
                        _startStatusCodeGeneration
                    );
                    if (ownership.Sequence > 0)
                    {
                        ownerAction = ownership.OwnerAction;
                        ownerGeneration = ownership.Generation;
                        sequence = ownership.Sequence;
                        _popupSequence = Math.Max(_popupSequence, sequence);
                    }
                }
                else if (!parentPending && _startStatusCodeGeneration <= 0)
                {
                    _startStatusParentCapture.Clear();
                }
            }
            else if (_pendingCheckpointAction is NetherPlannedAction checkpointAction
                && _checkpointOwnerGeneration > 0
                && typeName is ContinuePopupControllerTypeName or BoostPopupControllerTypeName or ReturnPopupControllerTypeName)
            {
                ownerAction = checkpointAction.Kind;
                ownerGeneration = _checkpointOwnerGeneration;
            }
            PopupRegistration registration = new(controller, popup, close, sequence, ownerAction, ownerGeneration, IsLive: true);
            switch (typeName)
            {
                case EventPopupControllerTypeName:
                    _eventPopup = registration;
                    break;
                case RecoverPopupControllerTypeName:
                    _recoverPopup = registration;
                    break;
                case TreasurePopupControllerTypeName:
                    _treasurePopup = registration;
                    break;
                case ShopPopupControllerTypeName:
                    _shopPopup = registration;
                    break;
                case ShopConfirmPopupControllerTypeName:
                    _shopConfirmPopup = registration;
                    _shopConfirmParentSequence = _shopPopup is PopupRegistration shop
                        && shop.IsLive
                        && shop.OwnerAction == ownerAction
                        && shop.OwnerGeneration == ownerGeneration
                            ? shop.Sequence
                            : 0;
                    break;
                case CodeSelectPopupControllerTypeName:
                    _codeSelectPopup = registration;
                    break;
                case CodeListPopupControllerTypeName:
                    _codeListPopup = registration;
                    if (TryReadCodeListPopupType(controller, out int codeListType) && codeListType == 1)
                    {
                        _codeTransformConfirmPopup = null;
                        _codeTransformCompletePopup = null;
                        _codeTransformTask = null;
                        _codeTransformTaskWait.Clear();
                    }
                    break;
                case CodeTransformConfirmPopupControllerTypeName:
                    _codeTransformConfirmPopup = registration;
                    break;
                case CodeTransformCompletePopupControllerTypeName:
                    _codeTransformCompletePopup = registration;
                    break;
                case ReturnPopupControllerTypeName:
                    _returnPopup = registration;
                    break;
                case ContinuePopupControllerTypeName:
                    _continuePopup = registration;
                    break;
                case BoostPopupControllerTypeName:
                    _boostPopup = registration;
                    break;
                case ContentAcquiredPopupControllerTypeName:
                    contentConfirmBound = _contentAcquiredConfirmLease.Register(
                        popup,
                        close,
                        sequence,
                        ownerAction,
                        ownerGeneration,
                        _runtimeGeneration
                    );
                    break;
                case FloorEventHintPopupControllerTypeName:
                    _floorEventHintPopup = registration;
                    hintConfirmBound = _floorEventHintConfirmLease.Register(
                        popup,
                        close,
                        sequence,
                        ownerAction,
                        ownerGeneration,
                        _runtimeGeneration
                    );
                    break;
                default:
                    recognized = false;
                    break;
            }
            if (recognized
                && ownerAction == NetherActionKind.None
                && typeName is EventPopupControllerTypeName
                    or RecoverPopupControllerTypeName
                    or TreasurePopupControllerTypeName)
            {
                recoveredTaskBound = _recoveredFloorEventTaskLease.BindPopup(popup, sequence);
            }
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", recognized ? "popup-registered" : "popup-unrecognized"),
            new("type", typeName),
            new("sequence", sequence.ToString()),
            new("owner", ownerAction.ToString()),
            new("ownerGeneration", ownerGeneration.ToString()),
            new("recoveredTaskBound", recoveredTaskBound.ToString()),
            new("contentConfirmBound", contentConfirmBound.ToString()),
            new("hintConfirmBound", hintConfirmBound.ToString()),
            new("hasClose", (close != null).ToString())
        );
    }

    private void InvalidatePopupCore(object popup)
    {
        if (popup == null)
            return;
        lock (_gate)
        {
            _recoveredFloorEventTaskLease.InvalidatePopup(popup);
            _contentAcquiredConfirmLease.InvalidatePopup(popup);
            _floorEventHintConfirmLease.InvalidatePopup(popup);
            InvalidatePopup(ref _eventPopup, popup);
            InvalidatePopup(ref _recoverPopup, popup);
            InvalidatePopup(ref _treasurePopup, popup);
            InvalidatePopup(ref _shopPopup, popup);
            if (_shopConfirmPopup is PopupRegistration shopConfirm
                && ReferenceEquals(shopConfirm.Popup, popup))
            {
                _shopConfirmParentSequence = 0;
            }
            InvalidatePopup(ref _shopConfirmPopup, popup);
            InvalidatePopup(ref _codeSelectPopup, popup);
            InvalidatePopup(ref _codeListPopup, popup);
            InvalidatePopup(ref _codeTransformConfirmPopup, popup);
            InvalidatePopup(ref _codeTransformCompletePopup, popup);
            InvalidatePopup(ref _returnPopup, popup);
            InvalidatePopup(ref _continuePopup, popup);
            InvalidatePopup(ref _boostPopup, popup);
            InvalidatePopup(ref _floorEventHintPopup, popup);
            if (_returnScrollController is CheckpointControllerRegistration scroll
                && ReferenceEquals(scroll.Controller, popup))
            {
                _returnScrollController = null;
            }
        }
    }

    private void InvalidatePopup(ref PopupRegistration? registration, object popup)
    {
        if (registration is not PopupRegistration candidate || !ReferenceEquals(candidate.Popup, popup))
            return;
        _popupOwnership.Invalidate(candidate.Popup, candidate.Sequence);
        registration = null;
    }

    private PopupRegistration? FindPopupRegistration(NetherPopupOwnership ownership)
    {
        foreach (PopupRegistration? candidate in new PopupRegistration?[]
            {
                _eventPopup,
                _recoverPopup,
                _treasurePopup,
                _shopPopup,
                _codeSelectPopup,
                _codeListPopup,
                _returnPopup,
                _continuePopup,
                _boostPopup,
            })
        {
            if (candidate is { IsLive: true }
                && candidate.Value.Sequence == ownership.Sequence
                && candidate.Value.OwnerAction == ownership.OwnerAction
                && candidate.Value.OwnerGeneration == ownership.Generation
                && ReferenceEquals(candidate.Value.Popup, ownership.Popup))
            {
                return candidate;
            }
        }
        return null;
    }

    private bool IsCurrentFloorOwnedPopup(PopupRegistration? registration)
    {
        if (registration is not PopupRegistration candidate || !candidate.IsLive)
            return false;
        return candidate.OwnerAction switch
        {
            NetherActionKind.SelectFloor =>
                _floorParentAction?.Kind == NetherActionKind.SelectFloor
                && candidate.OwnerGeneration == _floorParentGeneration,
            NetherActionKind.BattleSettlement =>
                candidate.OwnerGeneration == _battleResultCodeGeneration
                && _battleResultContinuation.HasObservation
                && !_battleResultContinuation.NextInvoked,
            NetherActionKind.RecoveredCodeOffer =>
                candidate.OwnerGeneration == _startStatusCodeGeneration
                && _startStatusParentCapture.IsReady(_floorSelectionController),
            NetherActionKind.None => _floorParentAction == null
                && _battleResultCodeGeneration == 0
                && _startStatusCodeGeneration == 0,
            _ => false,
        };
    }

    private bool IsCurrentCheckpointRegistration(
        CheckpointControllerRegistration registration,
        NetherCheckpointPopupKind kind
    ) => registration.IsLive
        && registration.OwnerAction == _pendingCheckpointAction?.Kind
        && registration.OwnerGeneration == _checkpointOwnerGeneration
        && registration.Sequence > _checkpointMinimumSequence
        && kind == NetherCheckpointPopupKind.ReturnScroll;

    private void ClearFloorParentCore()
    {
        long generation = _floorParentGeneration;
        _popupOwnership.InvalidateOwner(NetherActionKind.SelectFloor, generation);
        ClearFloorPopup(ref _eventPopup, generation);
        ClearFloorPopup(ref _recoverPopup, generation);
        ClearFloorPopup(ref _treasurePopup, generation);
        ClearFloorPopup(ref _shopPopup, generation);
        ClearFloorPopup(ref _shopConfirmPopup, generation);
        _shopConfirmParentSequence = 0;
        ClearFloorPopup(ref _codeSelectPopup, generation);
        ClearFloorPopup(ref _codeListPopup, generation);
        ClearFloorPopup(ref _codeTransformConfirmPopup, generation);
        ClearFloorPopup(ref _codeTransformCompletePopup, generation);
        ClearFloorPopup(ref _floorEventHintPopup, generation);
        _floorParentAction = null;
        _floorParentGeneration = 0;
        _floorEventSequenceTaskFlow.Reset();
        _contentAcquiredConfirmLease.Reset();
        _floorEventHintConfirmLease.Reset();
        ResetOwnedPopupStages();
        ClearCodeKeepCancelFlow();
        ClearCodeTransformFlow();
    }

    private void ClearBattleResultCodeOwnerCore()
    {
        long generation = _battleResultCodeGeneration;
        if (generation > 0)
            _popupOwnership.InvalidateOwner(NetherActionKind.BattleSettlement, generation);
        ClearBattleResultPopup(ref _codeSelectPopup, generation);
        ClearBattleResultPopup(ref _codeListPopup, generation);
        _battleResultViewController = null;
        _battleResultCodeGeneration = 0;
        ResetOwnedPopupStages();
        ClearCodeSelectionFlow();
        ClearCodeKeepCancelFlow();
    }

    private void ClearRecoveredCodeOfferCore()
    {
        long generation = _startStatusCodeGeneration;
        if (generation > 0)
            _popupOwnership.InvalidateOwner(NetherActionKind.RecoveredCodeOffer, generation);
        ClearRecoveredCodePopup(ref _codeSelectPopup, generation);
        ClearRecoveredCodePopup(ref _codeListPopup, generation);
        _startStatusParentCapture.Clear();
        _startStatusCodeGeneration = 0;
        ResetOwnedPopupStages();
        ClearCodeSelectionFlow();
        ClearCodeKeepCancelFlow();
    }

    private static void ClearRecoveredCodePopup(
        ref PopupRegistration? registration,
        long generation
    )
    {
        if (registration is PopupRegistration candidate
            && candidate.OwnerAction == NetherActionKind.RecoveredCodeOffer
            && candidate.OwnerGeneration == generation)
        {
            registration = null;
        }
    }

    private static void ClearBattleResultPopup(
        ref PopupRegistration? registration,
        long generation
    )
    {
        if (registration is PopupRegistration candidate
            && candidate.OwnerAction == NetherActionKind.BattleSettlement
            && candidate.OwnerGeneration == generation)
        {
            registration = null;
        }
    }

    private static void ClearFloorPopup(ref PopupRegistration? registration, long generation)
    {
        if (registration is PopupRegistration candidate
            && candidate.OwnerAction == NetherActionKind.SelectFloor
            && candidate.OwnerGeneration == generation)
        {
            registration = null;
        }
    }

    private void RegisterReturnScrollCore(object controller)
    {
        if (controller == null)
            return;
        lock (_gate)
        {
            long sequence = checked(++_popupSequence);
            NetherActionKind ownerAction = NetherActionKind.None;
            long ownerGeneration = 0;
            if (_pendingCheckpointAction is NetherPlannedAction action && _checkpointOwnerGeneration > 0)
            {
                ownerAction = action.Kind;
                ownerGeneration = _checkpointOwnerGeneration;
            }
            _returnScrollController = new CheckpointControllerRegistration(
                controller,
                sequence,
                ownerAction,
                ownerGeneration,
                IsLive: true
            );
        }
    }

    private void ObserveBattleStartCore()
    {
        _transitionSnapshotCache.BeginBattle();
        lock (_gate)
        {
            _battleActive = true;
            _battleResultCharactersRequired = false;
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new NetherAutoClimbDiagnosticField("action", "battle-start-observed")
        );
    }

    private void ObserveBattleClearCore()
    {
        lock (_gate)
        {
            _battleActive = false;
            _battleClearObserved = true;
            _battleCloseObserved = false;
            _battleResultCharactersRequired = true;
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new NetherAutoClimbDiagnosticField("action", "battle-clear-observed")
        );
    }

    private void ObserveBattleCloseCore()
    {
        lock (_gate)
        {
            _battleActive = false;
            _battleCloseObserved = true;
            _battleClearObserved = false;
            _battleResultCharactersRequired = false;
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new NetherAutoClimbDiagnosticField("action", "battle-close-observed")
        );
    }

    private void ObserveBattleTaskCore(BattleTaskKind kind, object task)
    {
        if (task == null)
            return;
        bool startExpected;
        lock (_gate)
        {
            startExpected = _battleStartExpected;
            switch (kind)
            {
                case BattleTaskKind.Start:
                    _battleStartTask = task;
                    if (_battleStartExpected)
                        _battleStartTaskWait.ObserveRegistration();
                    break;
                case BattleTaskKind.Clear:
                    _battleClearTask = task;
                    break;
                case BattleTaskKind.Close:
                    _battleCloseTask = task;
                    break;
            }
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "battle-task-captured"),
            new("method", kind switch
            {
                BattleTaskKind.Start => "StartQuestAsync",
                BattleTaskKind.Clear => "ClearQuestAsync",
                _ => "CloseQuestAsync",
            }),
            new("taskType", task.GetType().FullName ?? task.GetType().Name),
            new("startExpected", startExpected.ToString())
        );
    }

    private void ObserveBattleResultCharactersCore(object characters)
    {
        var mapped = new List<NetherCharacterState>();
        string detail = string.Empty;
        try
        {
            foreach (object rawCharacter in Enumerate(characters))
            {
                if (rawCharacter is not NetherCharacterEntity character
                    || character.m_character_id <= 0
                    || character.current_hp_ratio is < 0 or > 1000)
                {
                    detail = "invalid-nether-clear-character";
                    mapped.Clear();
                    break;
                }
                mapped.Add(new NetherCharacterState(
                    character.m_character_id,
                    character.current_hp_ratio,
                    character.current_hp_ratio > 0
                ));
            }
            if (mapped.Count == 0 && detail.Length == 0)
                detail = "empty-nether-clear-characters";
            if (detail.Length == 0
                && mapped.GroupBy(character => character.CharacterId).Any(group => group.Count() != 1))
            {
                detail = "duplicate-nether-clear-character";
            }
        }
        catch (Exception ex)
        {
            detail = "nether-clear-character-map-exception:" + ex.GetType().Name;
            mapped.Clear();
        }

        bool accepted = detail.Length == 0
            && _transitionSnapshotCache.ObserveBattleResultCharacters(mapped);
        if (!accepted && detail.Length == 0)
            detail = "nether-clear-character-cache-rejected";
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "battle-result-characters-observed"),
            new("accepted", accepted.ToString()),
            new("count", mapped.Count.ToString()),
            new("hp", mapped.Count == 0
                ? "none"
                : string.Join(",", mapped.OrderBy(character => character.CharacterId).Select(
                    character => character.CharacterId + ":" + character.HpPermille
                ))),
            new("detail", detail)
        );
    }

    private void ObserveResultCore(object? resultTask)
    {
        lock (_gate)
            _resultScene.ObserveResultTask(resultTask);
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "result-task-observed"),
            new("hasTask", (resultTask != null).ToString()),
            new("taskType", resultTask?.GetType().FullName ?? "none")
        );
    }

    private void ObserveBattleResultViewCore(object controller, object initializeTask)
    {
        if (controller == null || initializeTask == null)
            return;

        long baseline;
        long codeGeneration;
        bool replaced;
        lock (_gate)
        {
            baseline = _runtimeGeneration;
            replaced = !ReferenceEquals(_battleResultViewController, controller);
            if (replaced)
            {
                ClearBattleResultCodeOwnerCore();
                _battleResultViewController = controller;
                _battleResultCodeGeneration = _popupOwnership.BeginOwner(
                    NetherActionKind.BattleSettlement
                );
                ResetOwnedPopupStages();
                ClearCodeSelectionFlow();
                ClearCodeKeepCancelFlow();
            }
            codeGeneration = _battleResultCodeGeneration;
            _battleResultContinuation.Observe(controller, initializeTask, baseline);
            _battleResultSnapshotWait.Clear();
        }
        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "battle-result-view-observed"),
            new("controllerType", controller.GetType().FullName ?? controller.GetType().Name),
            new("taskType", initializeTask.GetType().FullName ?? initializeTask.GetType().Name),
            new("floorGenerationBeforeResult", baseline.ToString()),
            new("codeOwnerGeneration", codeGeneration.ToString()),
            new("ownerReplaced", replaced.ToString())
        );
    }

    private void ObserveFloorEventSequenceTaskCore(object controller, object sequenceTask)
    {
        if (controller == null || sequenceTask == null)
            return;

        bool accepted;
        long generation;
        long floorId;
        string mode;
        string detail;
        lock (_gate)
        {
            generation = _floorParentAction != null
                ? _floorParentGeneration
                : _runtimeGeneration;
            floorId = _floorParentAction?.FloorId ?? 0;
            if (!ReferenceEquals(controller, _floorSelectionController))
            {
                accepted = false;
                mode = "ignored";
                detail = "controller-not-current";
            }
            else if (_floorParentAction != null && _floorParentGeneration > 0)
            {
                accepted = _floorEventSequenceTaskFlow.ObserveEventSequenceTask(sequenceTask);
                mode = "owned-floor-parent";
                detail = accepted ? "exact-sequence-task" : "sequence-task-not-accepted";
            }
            else if (_runtimeGeneration > 0)
            {
                accepted = _recoveredFloorEventTaskLease.ObserveSequence(
                    controller,
                    _runtimeGeneration,
                    sequenceTask,
                    _popupSequence
                );
                if (accepted)
                {
                    _contentAcquiredConfirmLease.Reset();
                    _floorEventHintConfirmLease.Reset();
                }
                mode = "recovered-candidate";
                detail = accepted
                    ? "awaiting-correlated-popup"
                    : "recovered-sequence-not-accepted";
            }
            else
            {
                accepted = false;
                mode = "ignored";
                detail = "missing-floor-runtime-generation";
            }
        }

        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "floor-event-sequence-task-captured"),
            new("accepted", accepted.ToString()),
            new("controllerType", controller.GetType().FullName ?? controller.GetType().Name),
            new("taskType", sequenceTask.GetType().FullName ?? sequenceTask.GetType().Name),
            new("mode", mode),
            new("generation", generation.ToString()),
            new("floorId", floorId.ToString()),
            new("detail", detail)
        );
    }

    private void ObserveStartStatusStateMachineEnterCore(object stateMachine)
    {
        if (stateMachine == null)
            return;

        if (!TryReadMember(stateMachine, "__4__this", out object? controller)
            || controller == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "start-status-state-machine-enter"),
                new("accepted", "False"),
                new("stateMachineType", stateMachine.GetType().FullName ?? stateMachine.GetType().Name),
                new("detail", "missing-generated-controller-property")
            );
            return;
        }

        RegisterFloorSelectionCore(controller, "start-status-state-machine-enter");

        bool accepted;
        bool adoptedExistingPopup = false;
        long generation;
        string detail;
        lock (_gate)
        {
            if (!ReferenceEquals(controller, _floorSelectionController))
            {
                accepted = false;
                detail = "controller-is-not-current-floor-selection";
            }
            else if (_floorParentAction != null
                || _battleResultCodeGeneration > 0
                || _pendingCheckpointAction != null)
            {
                accepted = false;
                detail = "another-native-owner-is-active";
            }
            else
            {
                accepted = _startStatusParentCapture.ObserveStateMachineEnter(
                    stateMachine,
                    controller
                );
                adoptedExistingPopup = accepted && TryAdoptRecoveredCodePopupCore();
                detail = accepted
                    ? adoptedExistingPopup
                        ? "captured-and-adopted-existing-code-popup"
                        : "captured-before-popup-registration"
                    : "attached-parent-rejected-unrelated-state-machine";
            }
            generation = _startStatusCodeGeneration;
        }

        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "start-status-state-machine-enter"),
            new("accepted", accepted.ToString()),
            new("controllerType", controller.GetType().FullName ?? controller.GetType().Name),
            new("stateMachineType", stateMachine.GetType().FullName ?? stateMachine.GetType().Name),
            new("runtimeGeneration", _runtimeGeneration.ToString()),
            new("ownerGeneration", generation.ToString()),
            new("adoptedExistingPopup", adoptedExistingPopup.ToString()),
            new("detail", detail)
        );
    }

    private void ObserveStartStatusStateMachineExitCore(object stateMachine)
    {
        if (stateMachine == null)
            return;

        if (!TryReadMember(stateMachine, "__t__builder", out object? builder)
            || builder == null
            || !TryReadMember(builder, "Task", out object? parentTask)
            || parentTask == null)
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "start-status-state-machine-exit"),
                new("accepted", "False"),
                new("stateMachineType", stateMachine.GetType().FullName ?? stateMachine.GetType().Name),
                new("detail", "missing-generated-builder-task")
            );
            return;
        }

        bool accepted;
        bool adoptedExistingPopup;
        bool ready;
        long generation;
        lock (_gate)
        {
            accepted = _startStatusParentCapture.ObserveStateMachineExit(
                stateMachine,
                parentTask
            );
            adoptedExistingPopup = accepted && TryAdoptRecoveredCodePopupCore();
            ready = _startStatusParentCapture.IsReady(_floorSelectionController);
            generation = _startStatusCodeGeneration;
        }

        NetherAutoClimbController.LogDiagnostic(
            "runtime-lifecycle",
            new("action", "start-status-state-machine-exit"),
            new("accepted", accepted.ToString()),
            new("stateMachineType", stateMachine.GetType().FullName ?? stateMachine.GetType().Name),
            new("taskType", parentTask.GetType().FullName ?? parentTask.GetType().Name),
            new("ownerGeneration", generation.ToString()),
            new("ready", ready.ToString()),
            new("adoptedExistingPopup", adoptedExistingPopup.ToString()),
            new("detail", accepted ? "builder-task-captured" : "stale-state-machine-exit")
        );
    }

    private bool TryAdoptRecoveredCodePopupCore()
    {
        if (_codeSelectPopup is not PopupRegistration existing
            || !existing.IsLive
            || existing.OwnerAction != NetherActionKind.None
            || !_startStatusParentCapture.HasCandidateFor(_floorSelectionController)
            || !_startStatusParentCapture.TryAttachPopup(_floorSelectionController!))
        {
            return false;
        }

        if (_startStatusCodeGeneration <= 0)
        {
            _startStatusCodeGeneration = _popupOwnership.BeginOwner(
                NetherActionKind.RecoveredCodeOffer
            );
            ResetOwnedPopupStages();
            ClearCodeSelectionFlow();
            ClearCodeKeepCancelFlow();
        }

        NetherPopupOwnership ownership = _popupOwnership.Register(
            existing.Popup,
            NetherActionKind.RecoveredCodeOffer,
            _startStatusCodeGeneration
        );
        if (ownership.Sequence <= 0)
            return false;

        _codeSelectPopup = existing with
        {
            Sequence = ownership.Sequence,
            OwnerAction = ownership.OwnerAction,
            OwnerGeneration = ownership.Generation,
        };
        _popupSequence = Math.Max(_popupSequence, ownership.Sequence);
        return true;
    }

    private void ObserveCodeSelectionTaskCore(object resultTask)
    {
        if (resultTask == null)
            return;
        lock (_gate)
        {
            // Ignore a player-driven popup outside an F12 action.  In particular, do not let a
            // stale native callback become a task for a later automatic offer.
            if (!_codeSelectionFlow.ObserveConfirmationTask())
                return;
            _codeSelectionTask = resultTask;
            _codeSelectionTaskWait.ObserveRegistration();
        }
    }

    private void ObserveCodeKeepCancelTaskCore(object controller, object resultTask)
    {
        if (controller == null || resultTask == null)
            return;
        lock (_gate)
        {
            if (OwnedPopupKeepOwner is not NetherCodeKeepCancelOwner owner
                || _codeSelectPopup is not PopupRegistration registration
                || !registration.IsLive
                || !ReferenceEquals(registration.Controller, controller)
                || registration.OwnerAction != owner.OwnerAction
                || registration.OwnerGeneration != owner.Generation
                || registration.Sequence != owner.Sequence
                || GetOwnedPopupDecisionEpoch(new NetherOwnedPopupStageOwner(
                    registration.OwnerAction,
                    registration.OwnerGeneration,
                    registration.Sequence,
                    0
                )) != owner.DecisionEpoch)
            {
                return;
            }

            if (!ObserveOwnedPopupKeepCancelTask(owner))
                return;
            _codeKeepCancelTask = resultTask;
            _codeKeepCancelTaskWait.ObserveRegistration();
        }
    }

    private void ObserveCodeTransformTaskCore(object controller, object beforeCodeId, object resultTask)
    {
        string outcome = "ignored";
        string detail = "invalid-observer-arguments";
        if (controller != null && beforeCodeId != null && resultTask != null
            && TryConvertInt64(beforeCodeId, out long observedBeforeCodeId))
        {
            lock (_gate)
            {
                if (OwnedPopupTransformOwner is not NetherCodeTransformOwner owner)
                {
                    detail = "no-active-transform-owner";
                }
                else if (_codeListPopup is not PopupRegistration registration
                    || !registration.IsLive
                    || !ReferenceEquals(registration.Controller, controller)
                    || registration.OwnerAction != owner.OwnerAction
                    || registration.OwnerGeneration != owner.Generation
                    || registration.Sequence != owner.Sequence
                    || observedBeforeCodeId != owner.ReplaceCodeId)
                {
                    detail = "transform-owner-or-before-code-mismatch";
                }
                else if (!ObserveOwnedPopupCodeTransformTask(owner))
                {
                    detail = "transform-stage-rejected-task";
                }
                else
                {
                    _codeTransformTask = resultTask;
                    _codeTransformTaskWait.ObserveRegistration();
                    outcome = "accepted";
                    detail = "beforeCodeId=" + observedBeforeCodeId;
                }
            }
        }

        NetherAutoClimbController.LogDiagnostic(
            "code-transform",
            new("stage", "task-observer"),
            new("outcome", outcome),
            new("detail", detail),
            new("controllerType", controller?.GetType().FullName ?? "null"),
            new("taskType", resultTask?.GetType().FullName ?? "null")
        );
    }

    private void ClearCodeSelectionFlow()
    {
        _codeSelectionTask = null;
        _codeSelectionTaskWait.Clear();
        _codeReplacementPopupWait.Clear();
        _codeSelectionFlow.Clear();
    }

    private void ClearCodeKeepCancelFlow()
    {
        _codeKeepCancelTask = null;
        _codeKeepCancelTaskWait.Clear();
    }

    private void ClearCodeTransformFlow()
    {
        _codeTransformTask = null;
        _codeTransformTaskWait.Clear();
        _codeTransformConfirmPopup = null;
        _codeTransformCompletePopup = null;
    }

    private NetherNativeActionResult SelectFloor(NetherPlannedAction action)
    {
        if (action.FloorLevel < 1 || action.FloorIndex < 0)
            return NetherNativeActionResult.Rejected("invalid-floor-selection");
        object? controller;
        lock (_gate)
        {
            controller = _floorSelectionController;
            if (_floorParentAction != action || _floorParentGeneration < 1)
                return NetherNativeActionResult.BindingUnavailable("missing-floor-parent-ownership");
        }
        if (controller == null)
            return NetherNativeActionResult.BindingUnavailable("missing-floor-selection-controller");

        return TryInvokeExact(
            controller,
            new NetherNativeMethodDescriptor(
                "OnFloorClickedEventAsync",
                new[] { "System.Int32", "System.Int32" },
                UniTaskTypeName
            ),
            new object[] { action.FloorLevel, action.FloorIndex },
            "select-floor",
            registerNativeActionTask: false,
            observeTask: RegisterFloorParentTask
        );
    }

    private NetherNativeActionResult SelectEventOption(NetherPlannedAction action)
    {
        if (action.OptionNumber < 1)
            return NetherNativeActionResult.Rejected("invalid-event-option");

        List<(PopupRegistration Registration, EventFlowKind Kind)> active = new();
        lock (_gate)
        {
            if (_eventPopup is PopupRegistration eventPopup && IsCurrentFloorOwnedPopup(eventPopup))
                active.Add((eventPopup, EventFlowKind.Event));
            if (_recoverPopup is PopupRegistration recoveryPopup && IsCurrentFloorOwnedPopup(recoveryPopup))
                active.Add((recoveryPopup, EventFlowKind.Recovery));
            if (_treasurePopup is PopupRegistration treasurePopup && IsCurrentFloorOwnedPopup(treasurePopup))
                active.Add((treasurePopup, EventFlowKind.Treasure));
        }
        if (active.Count == 0)
            return NetherNativeActionResult.BindingUnavailable("missing-native-event-popup");

        // Popup controllers can outlive their visual close animation.  The most recently
        // initialized exact popup is the only active flow; treating stale registrations as an
        // ambiguity would either deadlock or select a previous event.
        (PopupRegistration Registration, EventFlowKind Kind) selected = active
            .OrderByDescending(value => value.Registration.Sequence)
            .First();
        PopupRegistration registration = selected.Registration;
        string popupType = registration.Popup.GetType().FullName ?? string.Empty;
        NetherNativeMethodDescriptor select = new(
            "OnPanelSelected",
            new[] { popupType, "System.Int32" },
            "System.Void"
        );
        NetherNativeMethodDescriptor terminal = new(
            selected.Kind == EventFlowKind.Treasure ? "OnConfirm" : "ExecuteEvent",
            new[] { popupType },
            "System.Void"
        );
        if (!TryResolveExactMethod(registration.Controller.GetType(), select, InstanceFlags, out string selectError, out MethodInfo? selectMethod))
            return NetherNativeActionResult.BindingUnavailable(selectError);
        if (!TryResolveExactMethod(registration.Controller.GetType(), terminal, InstanceFlags, out string terminalError, out MethodInfo? terminalMethod))
            return NetherNativeActionResult.BindingUnavailable(terminalError);

        bool recoveredSequence = false;
        lock (_gate)
        {
            if (_floorParentAction != null)
            {
                if (!_floorEventSequenceTaskFlow.HasEventSequenceEvidence)
                    return NetherNativeActionResult.BindingUnavailable("owned-event-sequence-task-unavailable");
            }
            else
            {
                if (_floorSelectionController == null
                    || !_recoveredFloorEventTaskLease.TryClaim(
                        _floorSelectionController,
                        _runtimeGeneration,
                        registration.Popup,
                        registration.Sequence,
                        out object? recoveredTask
                    )
                    || recoveredTask == null
                    || !_recoveredFloorEventSequenceTaskFlow.BeginRecovered(recoveredTask))
                {
                    return NetherNativeActionResult.BindingUnavailable(
                        "direct-wait-event-sequence-task-unavailable"
                    );
                }
                recoveredSequence = true;
            }
        }

        try
        {
            selectMethod!.Invoke(registration.Controller, new object[] { registration.Popup, action.OptionNumber - 1 });
            terminalMethod!.Invoke(registration.Controller, new[] { registration.Popup });
            if (recoveredSequence)
            {
                NetherAutoClimbController.LogDiagnostic(
                    "runtime-lifecycle",
                    new("action", "recovered-floor-event-claimed"),
                    new("kind", selected.Kind.ToString()),
                    new("popupSequence", registration.Sequence.ToString()),
                    new("runtimeGeneration", _runtimeGeneration.ToString()),
                    new("optionNumber", action.OptionNumber.ToString())
                );
            }
            return NetherNativeActionResult.Started("native-event-option:" + selected.Kind);
        }
        catch (TargetInvocationException ex)
        {
            if (recoveredSequence)
            {
                lock (_gate)
                    _recoveredFloorEventSequenceTaskFlow.Reset();
            }
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException("select-event-option", ex));
        }
        catch (Exception ex)
        {
            if (recoveredSequence)
            {
                lock (_gate)
                    _recoveredFloorEventSequenceTaskFlow.Reset();
            }
            return NetherNativeActionResult.UnknownOutcome("select-event-option-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private NetherNativeActionResult LeaveShop()
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _shopPopup;
        if (registration == null || !IsCurrentFloorOwnedPopup(registration) || registration.Value.Close == null)
            return NetherNativeActionResult.BindingUnavailable("missing-native-shop-close-callback");

        return TryInvokeNoArgumentDelegate(registration.Value.Close, "native-shop-close");
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeShopPurchase(
        NetherOwnedPopupStageOwner owner,
        NetherPlannedAction action
    )
    {
        if (action.ContentId <= 0 || action.ContentAmount <= 0 || action.GoldCost < 0)
            return NetherNativeActionResult.Rejected("invalid-shop-content-id");
        PopupRegistration? registration;
        lock (_gate)
            registration = _shopPopup;
        if (registration == null
            || !IsCurrentOwnedPopup(NetherRuntimePopupKind.Shop, owner)
            || registration.Value.Close == null)
        {
            return NetherNativeActionResult.BindingUnavailable("missing-shop-popup");
        }
        if (!TryFindContentIndex(registration.Value.Controller, "_mNetherFloorShopContentsArray", action.ContentId, out int index, out string indexError))
            return NetherNativeActionResult.BindingUnavailable(indexError);

        NetherNativeActionResult invoked = TryInvokeExact(
            registration.Value.Controller,
            new NetherNativeMethodDescriptor(
                "OnPurchaseContentAsync",
                new[] { registration.Value.Popup.GetType().FullName ?? string.Empty, "System.Int32" },
                UniTaskTypeName
            ),
            new object[] { registration.Value.Popup, index },
            "buy-shop-item"
        );
        if (invoked.Kind != NetherNativeActionResultKind.Started)
            return invoked;

        // A boxed UniTask must be registered by TryInvokeExact before the next Pump.  If the
        // exact signature changed and gave us no observable task, the request has already
        // been attempted; retain no retry path and fail closed rather than closing/rebuying.
        lock (_gate)
        {
            if (_nativeActionTask == null)
                return NetherNativeActionResult.BindingUnavailable("shop-purchase-missing-native-task");
        }
        return invoked;
    }

    private NetherNativeActionResult SelectCode(NetherPlannedAction action)
    {
        if (action.CodeId <= 0)
            return NetherNativeActionResult.Rejected("invalid-code-id");
        if (action.ReplaceCodeId < 0 || action.ReplaceCodeId == action.CodeId)
            return NetherNativeActionResult.Rejected("invalid-code-replacement");
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeSelectPopup;
        if (registration == null || !IsCurrentFloorOwnedPopup(registration))
            return NetherNativeActionResult.BindingUnavailable("missing-code-offer-popup");
        if (!TryReadMember(registration.Value.Controller, "_mIds", out object? rawOfferIds) || rawOfferIds == null)
        {
            return NetherNativeActionResult.BindingUnavailable("missing-native-code-offer-ids");
        }

        var offerIds = new List<long>();
        foreach (object rawOfferId in Enumerate(rawOfferIds))
        {
            if (!TryConvertInt64(rawOfferId, out long offeredId) || offeredId <= 0)
                return NetherNativeActionResult.BindingUnavailable("invalid-native-code-offer-id");
            offerIds.Add(offeredId);
        }
        if (!NetherCodeOfferSelection.TryResolveIndex(offerIds, action.CodeId, out int offerIndex))
            return NetherNativeActionResult.BindingUnavailable("selected-code-not-unique-in-native-offer");

        // `b__12_3` is OnDetailClick -> OnClickDetail.  It changes the popup's selected detail
        // before the exact Receive callback below forwards controller._onConfirm(selectedId).
        NetherNativeActionResult selectDetail = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherCodePopupNativeBinding.DetailCallbackBinding(CodeSelectPopupControllerTypeName),
            new object?[] { offerIndex, registration.Value.Controller, registration.Value.Popup },
            "select-code-offer-detail"
        );
        if (selectDetail.Kind != NetherNativeActionResultKind.Started)
            return selectDetail;

        lock (_gate)
        {
            if (!_codeSelectionFlow.Begin(action.CodeId, action.ReplaceCodeId, registration.Value.Sequence))
                return NetherNativeActionResult.BindingUnavailable("code-selection-flow-already-in-flight");
            _codeSelectionTask = null;
            _codeSelectionTaskWait.Clear();
            _codeReplacementPopupWait.Clear();
        }

        // Packaged-game ISIL: b__12_0 invokes controller._onCancel; b__12_2 invokes
        // controller._onConfirm(selectedId).  Detail selection above must therefore be
        // followed by b__12_2, never the visually adjacent cancel callback.
        NetherNativeActionResult confirm = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherCodePopupNativeBinding.ConfirmCallbackBinding(CodeSelectPopupControllerTypeName),
            new object?[] { null, registration.Value.Controller },
            "select-code-offer"
        );
        if (confirm.Kind != NetherNativeActionResultKind.Started)
        {
            lock (_gate)
                ClearCodeSelectionFlow();
        }
        return confirm;
    }

    private NetherNativeActionResult PollCodeSelectionFlow()
    {
        if (_codeSelectionFlow.Stage == NetherCodeSelectionNativeStage.AwaitingConfirmationTask)
        {
            if (_codeSelectionTask == null)
                return _codeSelectionTaskWait.AwaitRegistration("code-confirmation");
            if (!_codeSelectionFlow.ObserveConfirmationTask())
                return NetherNativeActionResult.BindingUnavailable("invalid-native-code-confirmation-sequence");
        }

        if (_codeSelectionFlow.Stage == NetherCodeSelectionNativeStage.AwaitingReplacementPopup)
        {
            PopupRegistration? registration = _codeListPopup;
            if (registration == null || !_codeSelectionFlow.CanSubmitReplacement(registration.Value.Sequence))
            {
                if (_codeSelectionTask != null)
                {
                    NetherNativeActionResult taskResult = PollResultTask(_codeSelectionTask);
                    if (taskResult.Kind != NetherNativeActionResultKind.Started)
                    {
                        return taskResult.Kind == NetherNativeActionResultKind.Completed
                            ? NetherNativeActionResult.BindingUnavailable("native-code-confirmation-completed-before-replace-popup")
                            : taskResult;
                    }
                }
                return _codeReplacementPopupWait.AwaitRegistration("code-replace-popup");
            }

            NetherNativeActionResult replacement = SelectCodeReplacement(registration.Value);
            if (replacement.Kind != NetherNativeActionResultKind.Started)
                return replacement;
        }

        if (_codeSelectionFlow.Stage != NetherCodeSelectionNativeStage.AwaitingCompletion)
            return NetherNativeActionResult.BindingUnavailable("invalid-native-code-selection-stage");
        if (_codeSelectionTask == null)
            return _codeSelectionTaskWait.AwaitRegistration("code-confirmation");

        NetherNativeActionResult result = PollResultTask(_codeSelectionTask);
        if (result.Kind == NetherNativeActionResultKind.Started)
            return result;
        if (result.Kind != NetherNativeActionResultKind.Completed)
            return result;
        if (!_codeSelectionFlow.CompleteConfirmationTask())
            return NetherNativeActionResult.BindingUnavailable("invalid-native-code-completion-sequence");

        _codeSelectionTask = null;
        _codeSelectionTaskWait.Clear();
        _codeReplacementPopupWait.Clear();
        return NetherNativeActionResult.Completed("native-code-confirmation-succeeded");
    }

    private NetherNativeActionResult SelectCodeReplacement(PopupRegistration registration)
    {
        if (!IsCurrentFloorOwnedPopup(registration))
            return NetherNativeActionResult.BindingUnavailable("stale-native-code-replacement-popup");
        if (!TryReadMember(registration.Controller, "_popupType", out object? rawPopupType)
            || rawPopupType == null
            || !TryConvertInt32(rawPopupType, out int popupType)
            || popupType != 2) // Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopupType.Replace
        {
            return NetherNativeActionResult.BindingUnavailable("missing-or-nonreplace-native-code-list-popup");
        }
        if (!_codeSelectionFlow.CanSubmitReplacement(registration.Sequence))
            return _codeReplacementPopupWait.AwaitRegistration("code-replace-popup");
        if (!TryReadMember(registration.Controller, "_replaceMId", out object? rawReplacementId)
            || rawReplacementId == null
            || !TryConvertInt64(rawReplacementId, out long expectedReplacementId)
            || expectedReplacementId <= 0)
        {
            return NetherNativeActionResult.BindingUnavailable("missing-native-code-replacement-id");
        }

        // The popup's `_replaceMId` is the newly selected offer.  It must agree with the action
        // selected from the server-generated offer before we choose any code to remove.
        if (expectedReplacementId != _codeSelectionFlow.SelectedCodeId)
            return NetherNativeActionResult.BindingUnavailable("native-code-replacement-offer-mismatch");

        // `_replaceMId` is the added code; select the planned code-to-remove from the exact
        // native dictionary/tab map, then use the controller's private UI methods in the same
        // order as a player click: tab, thumbnail, Replace.
        long removeCodeId = _codeSelectionFlow.ReplacementCodeId;
        if (removeCodeId <= 0)
            return NetherNativeActionResult.BindingUnavailable("missing-pending-code-replacement-id");
        if (!TryFindCodeListSelection(registration.Controller, removeCodeId, out int tabIndex, out int modelIndex, out string mapError))
            return NetherNativeActionResult.BindingUnavailable(mapError);

        NetherNativeActionResult tab = TryInvokeExact(
            registration.Controller,
            new NetherNativeMethodDescriptor("OnChangeTab", new[] { "System.Int32" }, "System.Void"),
            new object[] { tabIndex },
            "select-code-replacement-tab"
        );
        if (tab.Kind != NetherNativeActionResultKind.Started)
            return tab;
        NetherNativeActionResult thumbnail = TryInvokeExact(
            registration.Controller,
            new NetherNativeMethodDescriptor("OnClickThumbnail", new[] { "System.Int32" }, "System.Void"),
            new object[] { modelIndex },
            "select-code-replacement-thumbnail"
        );
        if (thumbnail.Kind != NetherNativeActionResultKind.Started)
            return thumbnail;
        NetherNativeActionResult replace = TryInvokeExact(
            registration.Controller,
            new NetherNativeMethodDescriptor("OnClickReplace", Array.Empty<string>(), "System.Void"),
            Array.Empty<object>(),
            "confirm-code-replacement"
        );
        if (replace.Kind != NetherNativeActionResultKind.Started)
            return replace;
        if (!_codeSelectionFlow.SubmitReplacement(registration.Sequence))
            return NetherNativeActionResult.BindingUnavailable("invalid-native-code-replacement-sequence");
        _codeReplacementPopupWait.ObserveRegistration();
        return NetherNativeActionResult.Started("native-code-replacement-selected");
    }

    NetherOwnedPopupCodeReloadStart INetherOwnedPopupNativeStagePort.CaptureCodeReloadStart(
        NetherOwnedPopupStageOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(NetherRuntimePopupKind.CodeOffer, owner))
            return NetherOwnedPopupCodeReloadStart.Failure("missing-code-select-popup");
        NetherRuntimeSnapshotResult snapshot = TryCaptureBattleResultCodeSnapshot();
        if (!snapshot.IsSuccess)
            return NetherOwnedPopupCodeReloadStart.Failure("code-reload-snapshot:" + snapshot.Detail);
        return new NetherOwnedPopupCodeReloadStart(
            snapshot.Snapshot!.CodeReloadCount,
            TryGetCodeCandidates(),
            string.Empty
        );
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeReload(
        NetherCodeReloadEpochOwner owner
    )
    {
        if (!IsCurrentOwnedPopup(
                NetherRuntimePopupKind.CodeOffer,
                new NetherOwnedPopupStageOwner(owner.OwnerAction, owner.Generation, owner.Sequence, 0)
            ))
        {
            return NetherNativeActionResult.BindingUnavailable("missing-code-select-popup");
        }
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeSelectPopup;
        if (registration == null)
            return NetherNativeActionResult.BindingUnavailable("missing-code-select-popup");

        NetherNativeActionResult invoke = TryInvokeExact(
            registration.Value.Controller,
            new NetherNativeMethodDescriptor(
                "RerollAsync",
                new[] { registration.Value.Popup.GetType().FullName ?? string.Empty },
                UniTaskTypeName
            ),
            new[] { registration.Value.Popup },
            "reload-code"
        );
        if (invoke.Kind != NetherNativeActionResultKind.Started)
            return invoke;

        // As with Shop purchase, this native mutation has already been attempted.  An exact
        // boxed UniTask is mandatory for safe observation; do not retry RerollAsync if a game
        // version stops exposing it through the selected signature.
        if (_nativeActionTask == null)
            return NetherNativeActionResult.BindingUnavailable("code-reload-missing-native-task");
        return invoke;
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeKeepCancel(
        NetherCodeKeepCancelOwner owner
    )
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeSelectPopup;
        if (registration == null
            || !IsCurrentOwnedPopup(
                NetherRuntimePopupKind.CodeOffer,
                new NetherOwnedPopupStageOwner(
                    owner.OwnerAction,
                    owner.Generation,
                    owner.Sequence,
                    owner.DecisionEpoch
                )
            ))
        {
            return NetherNativeActionResult.BindingUnavailable("missing-code-select-popup");
        }

        lock (_gate)
        {
            _codeKeepCancelTask = null;
            _codeKeepCancelTaskWait.Clear();
        }

        // Packaged ISIL: AbyssCodeSelectPopupController.<>c.<SetupPopupEvent>b__12_0
        // invokes controller._onCancel.  That closure calls the static generated cancel
        // sequence below with Forget(), which Harmony observes rather than inventing a task.
        NetherNativeActionResult cancel = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherCodePopupNativeBinding.CancelCallbackBinding(CodeSelectPopupControllerTypeName),
            new object?[] { null, registration.Value.Controller },
            "keep-code-offer"
        );
        if (cancel.Kind == NetherNativeActionResultKind.Started)
            return cancel;

        lock (_gate)
        {
            _codeKeepCancelTask = null;
            _codeKeepCancelTaskWait.Clear();
        }
        return cancel;
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollCodeKeepCancelTask(
        NetherCodeKeepCancelOwner owner
    )
    {
        object? task;
        lock (_gate)
        {
            task = _codeKeepCancelTask;
            if (task == null)
                return _codeKeepCancelTaskWait.AwaitRegistration("code-keep-cancel");
        }

        NetherNativeActionResult result = PollResultTask(task);
        if (result.Kind == NetherNativeActionResultKind.Completed)
        {
            lock (_gate)
            {
                _codeKeepCancelTask = null;
                _codeKeepCancelTaskWait.Clear();
            }
        }
        return result;
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeTransform(
        NetherCodeTransformOwner owner
    )
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeListPopup;
        if (registration is not PopupRegistration list
            || !list.IsLive
            || list.OwnerAction != owner.OwnerAction
            || list.OwnerGeneration != owner.Generation
            || list.Sequence != owner.Sequence
            || !TryReadCodeListPopupType(list.Controller, out int popupType)
            || popupType != 1)
        {
            return LogCodeTransformNative(
                "list-select",
                NetherNativeActionResult.BindingUnavailable("missing-owned-change-code-list-popup"),
                owner
            );
        }
        if (!TryFindCodeListSelection(
                list.Controller,
                owner.ReplaceCodeId,
                out int tabIndex,
                out int modelIndex,
                out string mapError
            ))
        {
            return LogCodeTransformNative(
                "list-select",
                NetherNativeActionResult.BindingUnavailable(mapError),
                owner
            );
        }

        lock (_gate)
        {
            _codeTransformTask = null;
            _codeTransformTaskWait.Clear();
            _codeTransformConfirmPopup = null;
            _codeTransformCompletePopup = null;
        }

        NetherNativeActionResult tab = TryInvokeExact(
            list.Controller,
            new NetherNativeMethodDescriptor("OnChangeTab", new[] { "System.Int32" }, "System.Void"),
            new object[] { tabIndex },
            "select-code-transform-tab"
        );
        if (tab.Kind != NetherNativeActionResultKind.Started)
            return LogCodeTransformNative("list-select", tab, owner);
        NetherNativeActionResult thumbnail = TryInvokeExact(
            list.Controller,
            new NetherNativeMethodDescriptor("OnClickThumbnail", new[] { "System.Int32" }, "System.Void"),
            new object[] { modelIndex },
            "select-code-transform-thumbnail"
        );
        if (thumbnail.Kind != NetherNativeActionResultKind.Started)
            return LogCodeTransformNative("list-select", thumbnail, owner);
        NetherNativeActionResult change = TryInvokeExact(
            list.Controller,
            new NetherNativeMethodDescriptor("OnClickChange", Array.Empty<string>(), "System.Void"),
            Array.Empty<object>(),
            "start-code-transform"
        );
        return LogCodeTransformNative("list-select", change, owner, "tab=" + tabIndex + ",model=" + modelIndex);
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeTransformConfirm(
        NetherCodeTransformOwner owner
    )
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeTransformConfirmPopup;
        if (registration == null)
            return AwaitCodeTransformSupportPopup("confirm", owner);
        if (!IsMatchingCodeTransformSupport(registration.Value, owner)
            || !TryReadInt(registration.Value.Controller, "_mNetherCodeId", out long popupCodeId)
            || popupCodeId != owner.ReplaceCodeId
            || !TryReadMember(registration.Value.Controller, "_onCompleted", out object? onCompleted)
            || onCompleted == null)
        {
            return LogCodeTransformNative(
                "confirm",
                NetherNativeActionResult.BindingUnavailable("invalid-code-transform-confirm-popup"),
                owner
            );
        }

        NetherNativeActionResult invoked = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherCodeTransformNativeBinding.ConfirmCallbackBinding,
            new object?[] { null, onCompleted },
            "confirm-code-transform"
        );
        if (invoked.Kind != NetherNativeActionResultKind.Started)
            return LogCodeTransformNative("confirm", invoked, owner);
        lock (_gate)
        {
            if (_codeTransformConfirmPopup is PopupRegistration current
                && current.Sequence == registration.Value.Sequence)
            {
                _codeTransformConfirmPopup = null;
            }
        }
        return LogCodeTransformNative(
            "confirm",
            NetherNativeActionResult.Completed("native-code-transform-confirmed"),
            owner
        );
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeTransformCompleteClose(
        NetherCodeTransformOwner owner
    )
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeTransformCompletePopup;
        if (registration == null)
            return AwaitCodeTransformSupportPopup("complete", owner);
        if (!IsMatchingCodeTransformSupport(registration.Value, owner)
            || registration.Value.Close == null
            || !TryReadInt(registration.Value.Controller, "_beforeMNetherCodeId", out long beforeCodeId)
            || !TryReadInt(registration.Value.Controller, "_afterMNetherCodeId", out long afterCodeId)
            || beforeCodeId != owner.ReplaceCodeId
            || afterCodeId <= 0
            || afterCodeId == beforeCodeId)
        {
            return LogCodeTransformNative(
                "complete",
                NetherNativeActionResult.BindingUnavailable("invalid-code-transform-complete-popup"),
                owner
            );
        }

        NetherNativeActionResult invoked = TryInvokeVersionedGeneratedCallback(
            registration.Value.Controller,
            NetherCodeTransformNativeBinding.CompleteCloseCallbackBinding,
            new object?[] { null, registration.Value.Popup, registration.Value.Close },
            "close-code-transform-complete"
        );
        if (invoked.Kind != NetherNativeActionResultKind.Started)
            return LogCodeTransformNative("complete", invoked, owner);
        lock (_gate)
        {
            if (_codeTransformCompletePopup is PopupRegistration current
                && current.Sequence == registration.Value.Sequence)
            {
                _codeTransformCompletePopup = null;
            }
        }
        return LogCodeTransformNative(
            "complete",
            NetherNativeActionResult.Completed("native-code-transform-complete-closed"),
            owner,
            "afterCodeId=" + afterCodeId
        );
    }

    NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollCodeTransformTask(
        NetherCodeTransformOwner owner
    )
    {
        object? task;
        lock (_gate)
        {
            if (OwnedPopupTransformOwner is not NetherCodeTransformOwner expected || expected != owner)
            {
                return LogCodeTransformNative(
                    "task-terminal",
                    NetherNativeActionResult.BindingUnavailable("code-transform-task-owner-lost"),
                    owner
                );
            }
            task = _codeTransformTask;
            if (task == null)
                return _codeTransformTaskWait.AwaitRegistration("code-transform");
        }

        NetherNativeActionResult result = PollResultTask(task);
        if (result.Kind != NetherNativeActionResultKind.Started)
        {
            lock (_gate)
            {
                _codeTransformTask = null;
                _codeTransformTaskWait.Clear();
            }
        }
        return LogCodeTransformNative("task-terminal", result, owner);
    }

    private bool IsMatchingCodeTransformSupport(
        PopupRegistration registration,
        NetherCodeTransformOwner owner
    ) => registration.IsLive
        && registration.OwnerAction == owner.OwnerAction
        && registration.OwnerGeneration == owner.Generation
        && registration.Sequence > owner.Sequence
        && _floorParentAction?.Kind == NetherActionKind.SelectFloor
        && _floorParentGeneration == owner.Generation;

    private NetherNativeActionResult AwaitCodeTransformSupportPopup(
        string stage,
        NetherCodeTransformOwner owner
    )
    {
        object? task;
        lock (_gate)
            task = _codeTransformTask;
        if (task == null)
        {
            return LogCodeTransformNative(
                stage,
                NetherNativeActionResult.Started("await-code-transform-" + stage + "-popup"),
                owner
            );
        }

        NetherNativeActionResult taskState = PollResultTask(task);
        NetherNativeActionResult result = taskState.Kind switch
        {
            NetherNativeActionResultKind.Started =>
                NetherNativeActionResult.Started("await-code-transform-" + stage + "-popup"),
            NetherNativeActionResultKind.Completed =>
                NetherNativeActionResult.BindingUnavailable("code-transform-task-completed-before-" + stage + "-popup"),
            _ => taskState,
        };
        return LogCodeTransformNative(stage, result, owner);
    }

    private static NetherNativeActionResult LogCodeTransformNative(
        string stage,
        NetherNativeActionResult result,
        NetherCodeTransformOwner owner,
        string? extra = null
    )
    {
        NetherAutoClimbController.LogDiagnostic(
            "code-transform",
            new("stage", stage),
            new("outcome", result.Kind.ToString()),
            new("detail", result.Detail),
            new("generation", owner.Generation.ToString()),
            new("sequence", owner.Sequence.ToString()),
            new("beforeCodeId", owner.ReplaceCodeId.ToString()),
            new("extra", extra ?? "-")
        );
        return result;
    }

    private NetherNativeActionResult Continue(NetherPlannedAction action)
    {
        if (action.TicketCount != 1)
            return NetherNativeActionResult.Rejected("continue-requires-exactly-one-ticket");
        return StartCheckpointNativeFlow(action);
    }

    private NetherNativeActionResult FinishAtCheckpoint() => StartCheckpointNativeFlow(
        new NetherPlannedAction(NetherActionKind.FinishAtCheckpoint)
    );

    private NetherNativeActionResult StartCheckpointNativeFlow(NetherPlannedAction action)
    {
        object? floorController;
        lock (_gate)
        {
            if (_pendingCheckpointAction != null)
                return NetherNativeActionResult.Rejected("checkpoint-native-flow-already-pending");
            if (!_checkpointFlow.Begin(action))
                return NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-start-sequence");

            long ownerGeneration = checked(_checkpointGenerationCounter + 1);
            if (!_checkpointPopupWait.Begin(action.Kind, ownerGeneration, _popupSequence))
            {
                _checkpointFlow.Clear();
                return NetherNativeActionResult.BindingUnavailable("invalid-checkpoint-popup-wait-owner");
            }

            _checkpointGenerationCounter = ownerGeneration;
            _checkpointOwnerGeneration = ownerGeneration;
            _checkpointMinimumSequence = _popupSequence;
            floorController = _floorSelectionController;
            _continuePopup = null;
            _boostPopup = null;
            _returnPopup = null;
            _returnScrollController = null;
            _checkpointParentTask = null;
            _checkpointChildTask = null;
            _checkpointParentTaskWait.Clear();
            _checkpointTerminalTaskWait.Clear();
            _pendingCheckpointAction = action;
        }
        if (floorController == null)
        {
            lock (_gate)
                ClearCheckpointNativeFlow();
            return NetherNativeActionResult.BindingUnavailable("missing-floor-selection-controller-for-checkpoint");
        }

        NetherNativeActionResult start = TryInvokeExact(
            floorController,
            new NetherNativeMethodDescriptor(
                "HandleStartEventByStatusAsync",
                new[] { "System.Boolean" },
                UniTaskTypeName
            ),
            new object[] { false },
            "checkpoint-native-flow",
            registerNativeActionTask: false,
            observeTask: RegisterCheckpointParentTask
        );
        if (start.Kind is not (NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed))
        {
            lock (_gate)
                ClearCheckpointNativeFlow();
        }
        return start;
    }

    private NetherNativeActionResult PollCheckpointFlow()
    {
        if (_pendingCheckpointAction == null)
            return NetherNativeActionResult.Completed("no-pending-checkpoint-flow");
        NetherPlannedAction action = _pendingCheckpointAction.Value;

        return _checkpointFlow.Stage switch
        {
            NetherCheckpointNativeStage.AwaitingContinuePopup => PollCheckpointContinuePopup(action),
            NetherCheckpointNativeStage.AwaitingBoostConfirmation => PollCheckpointBoostPopup(),
            NetherCheckpointNativeStage.AwaitingPristineReturnPopup => PollCheckpointReturnPopup(action),
            NetherCheckpointNativeStage.AwaitingTerminalTask => PollCheckpointTerminalTask(),
            _ => TerminalCheckpointFailure(
                NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-poll-stage:" + _checkpointFlow.Stage)
            ),
        };
    }

    private NetherNativeActionResult PollCheckpointContinuePopup(NetherPlannedAction action)
    {
        NetherCheckpointPopupWaitResult wait = WaitForCheckpointPopup(
            NetherCheckpointPopupKind.Continue,
            _continuePopup
        );
        if (wait.Kind != NetherCheckpointPopupWaitResultKind.Ready)
            return ToCheckpointWaitResult(wait);

        if (_continuePopup is not PopupRegistration registration
            || !IsCurrentCheckpointPopup(registration, NetherCheckpointPopupKind.Continue))
        {
            return TerminalCheckpointFailure(
                NetherNativeActionResult.BindingUnavailable("stale-native-checkpoint-continue-popup")
            );
        }

        NetherNativeActionResult callback;
        if (action.Kind == NetherActionKind.Continue)
        {
            if (!TryReadBoolean(registration.Controller, "_canBoost", out bool canBoost))
            {
                return TerminalCheckpointFailure(
                    NetherNativeActionResult.BindingUnavailable("missing-continue-can-boost-field")
                );
            }
            // RO ISIL: the generated <SetupPopupEvent>b__8_2 Unit callback is the exact
            // Continue entry for both _canBoost values.  When true it opens the owned Boost
            // confirmation popup; when false it proceeds to the native one-ticket parent.
            // b__8_1 is Finish/cancel and must never stand in for Continue.
            callback = TryInvokeVersionedGeneratedCallback(
                registration.Controller,
                NetherCheckpointContinueNativeBinding.ContinueCallbackInterop,
                new object?[] { null, registration.Controller },
                "continue-one-ticket"
            );
            if (callback.Kind == NetherNativeActionResultKind.Started
                && !NetherCheckpointContinueNativeBinding.SubmitContinue(_checkpointFlow, canBoost))
            {
                return TerminalCheckpointFailure(
                    NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-continue-sequence")
                );
            }
        }
        else
        {
            callback = TryInvokeVersionedGeneratedCallback(
                registration.Controller,
                NetherCheckpointContinueNativeBinding.FinishCallbackInterop,
                new object?[] { null, registration.Controller },
                "finish-at-checkpoint"
            );
            if (callback.Kind == NetherNativeActionResultKind.Started && !_checkpointFlow.SubmitFinish())
            {
                return TerminalCheckpointFailure(
                    NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-finish-sequence")
                );
            }
        }
        if (callback.Kind != NetherNativeActionResultKind.Started)
            return TerminalCheckpointFailure(callback);

        return NetherNativeActionResult.Started("native-checkpoint-callback-submitted");
    }

    private NetherNativeActionResult PollCheckpointBoostPopup()
    {
        NetherCheckpointPopupWaitResult wait = WaitForCheckpointPopup(
            NetherCheckpointPopupKind.Boost,
            _boostPopup
        );
        if (wait.Kind != NetherCheckpointPopupWaitResultKind.Ready)
            return ToCheckpointWaitResult(wait);

        if (_boostPopup is not PopupRegistration registration
            || !IsCurrentCheckpointPopup(registration, NetherCheckpointPopupKind.Boost))
        {
            return TerminalCheckpointFailure(
                NetherNativeActionResult.BindingUnavailable("stale-native-checkpoint-boost-popup")
            );
        }

        NetherNativeActionResult boost = ConfirmBoostOneTicket(registration);
        if (boost.Kind != NetherNativeActionResultKind.Started)
            return TerminalCheckpointFailure(boost);
        if (!_checkpointFlow.SubmitBoostConfirmation())
        {
            return TerminalCheckpointFailure(
                NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-boost-sequence")
            );
        }
        return NetherNativeActionResult.Started("native-checkpoint-boost-submitted");
    }

    private NetherNativeActionResult PollCheckpointReturnPopup(NetherPlannedAction action)
    {
        NetherCheckpointPopupWaitResult popup = WaitForCheckpointPopup(
            NetherCheckpointPopupKind.Return,
            _returnPopup
        );
        if (popup.Kind != NetherCheckpointPopupWaitResultKind.Ready)
            return ToCheckpointWaitResult(popup);

        NetherCheckpointPopupWaitResult scroll = WaitForCheckpointScroll();
        if (scroll.Kind != NetherCheckpointPopupWaitResultKind.Ready)
            return ToCheckpointWaitResult(scroll);

        NetherNativeActionResult select = SelectCheckpointReturnItems(action);
        if (select.Kind != NetherNativeActionResultKind.Started)
            return TerminalCheckpointFailure(select);
        if (!_checkpointFlow.SubmitReturnSelection())
        {
            return TerminalCheckpointFailure(
                NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-return-sequence")
            );
        }
        return NetherNativeActionResult.Started("native-checkpoint-return-submitted");
    }

    private NetherNativeActionResult PollCheckpointTerminalTask()
    {
        if (_checkpointChildTask != null)
        {
            NetherNativeActionResult child = PollResultTask(_checkpointChildTask);
            if (child.Kind == NetherNativeActionResultKind.Started)
                return child;
            _checkpointChildTask = null;
            if (child.Kind != NetherNativeActionResultKind.Completed)
                return TerminalCheckpointFailure(child);
        }

        NetherNativeActionResult parent = PollCheckpointParent();
        if (parent.Kind == NetherNativeActionResultKind.Started)
        {
            NetherNativeActionResult wait = _checkpointTerminalTaskWait.AwaitRegistration("checkpoint-parent-terminal");
            return wait.Kind == NetherNativeActionResultKind.Started
                ? wait
                : TerminalCheckpointFailure(wait);
        }
        _checkpointTerminalTaskWait.Clear();
        if (parent.Kind != NetherNativeActionResultKind.Completed)
            return TerminalCheckpointFailure(parent);

        CompleteCheckpointNativeFlow();
        return NetherNativeActionResult.Completed("checkpoint-native-flow-completed");
    }

    /// <summary>
    /// The popup wait coordinator's only parent capability.  This reads the exact task captured
    /// from HandleStartEventByStatusAsync; it cannot invoke a checkpoint callback or endpoint.
    /// </summary>
    public NetherNativeActionResult PollCheckpointParent()
    {
        lock (_gate)
            return PollCheckpointParentTask();
    }

    private NetherNativeActionResult PollCheckpointParentTask()
    {
        if (_checkpointParentTask == null)
            return _checkpointParentTaskWait.AwaitRegistration("checkpoint-parent");
        return PollResultTask(_checkpointParentTask);
    }

    private NetherCheckpointPopupWaitResult WaitForCheckpointPopup(
        NetherCheckpointPopupKind kind,
        PopupRegistration? registration
    )
    {
        NetherCheckpointPopupObservation? observation = registration is PopupRegistration current
            ? new NetherCheckpointPopupObservation(
                kind,
                current.OwnerAction,
                current.OwnerGeneration,
                current.Sequence,
                current.IsLive
            )
            : null;
        return _checkpointPopupWait.WaitFor(kind, observation);
    }

    private NetherCheckpointPopupWaitResult WaitForCheckpointScroll()
    {
        NetherCheckpointPopupObservation? observation = _returnScrollController is CheckpointControllerRegistration current
            ? new NetherCheckpointPopupObservation(
                NetherCheckpointPopupKind.ReturnScroll,
                current.OwnerAction,
                current.OwnerGeneration,
                current.Sequence,
                current.IsLive
            )
            : null;
        return _checkpointPopupWait.WaitFor(NetherCheckpointPopupKind.ReturnScroll, observation);
    }

    private NetherNativeActionResult ToCheckpointWaitResult(NetherCheckpointPopupWaitResult wait)
    {
        NetherNativeActionResult native = wait.Kind switch
        {
            NetherCheckpointPopupWaitResultKind.Waiting => NetherNativeActionResult.Started(wait.Detail),
            NetherCheckpointPopupWaitResultKind.ParentCanceled => NetherNativeActionResult.UnknownOutcome(wait.Detail),
            NetherCheckpointPopupWaitResultKind.ParentFaulted => NetherNativeActionResult.UnknownOutcome(wait.Detail),
            NetherCheckpointPopupWaitResultKind.ParentCompletedEarly => NetherNativeActionResult.BindingUnavailable(wait.Detail),
            NetherCheckpointPopupWaitResultKind.Stale => NetherNativeActionResult.BindingUnavailable(wait.Detail),
            _ => NetherNativeActionResult.BindingUnavailable(wait.Detail),
        };
        return native.Kind == NetherNativeActionResultKind.Started
            ? native
            : TerminalCheckpointFailure(native);
    }

    private bool IsCurrentCheckpointPopup(PopupRegistration registration, NetherCheckpointPopupKind kind) =>
        registration.IsLive
        && registration.OwnerAction == _pendingCheckpointAction?.Kind
        && registration.OwnerGeneration == _checkpointOwnerGeneration
        && registration.Sequence > _checkpointMinimumSequence
        && kind is NetherCheckpointPopupKind.Continue or NetherCheckpointPopupKind.Boost or NetherCheckpointPopupKind.Return;

    private NetherNativeActionResult TerminalCheckpointFailure(NetherNativeActionResult result)
    {
        ClearCheckpointNativeFlow();
        return result;
    }

    private void CompleteCheckpointNativeFlow()
    {
        _pendingCheckpointAction = null;
        _checkpointFlow.Complete();
        _checkpointPopupWait.Reset();
        _checkpointParentTask = null;
        _checkpointChildTask = null;
        _checkpointParentTaskWait.Clear();
        _checkpointTerminalTaskWait.Clear();
        _checkpointOwnerGeneration = 0;
        _checkpointMinimumSequence = 0;
    }

    private void ClearCheckpointNativeFlow()
    {
        _pendingCheckpointAction = null;
        _checkpointFlow.Clear();
        _checkpointPopupWait.Reset();
        _checkpointParentTask = null;
        _checkpointChildTask = null;
        _checkpointParentTaskWait.Clear();
        _checkpointTerminalTaskWait.Clear();
        _checkpointOwnerGeneration = 0;
        _checkpointMinimumSequence = 0;
        _continuePopup = null;
        _boostPopup = null;
        _returnPopup = null;
        _returnScrollController = null;
    }

    private NetherNativeActionResult ConfirmBoostOneTicket(PopupRegistration registration)
    {
        if (!NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
                registration.Controller.GetType(),
                NetherCheckpointContinueNativeBinding.BoostSetCountInterop,
                out string setError,
                out object? singleton,
                out MethodInfo? setMethod
            ))
            return NetherNativeActionResult.BindingUnavailable(setError);
        if (!NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
                registration.Controller.GetType(),
                NetherCheckpointContinueNativeBinding.BoostConfirmInterop,
                out string confirmError,
                out object? confirmSingleton,
                out MethodInfo? confirmMethod
            ))
            return NetherNativeActionResult.BindingUnavailable(confirmError);

        try
        {
            setMethod!.Invoke(singleton, new object[]
            {
                NetherCheckpointContinueNativeBinding.ExactTicketCount,
                registration.Controller,
                registration.Popup,
            });
            object? unit = CreateDefaultValue(confirmMethod!.GetParameters()[0].ParameterType);
            confirmMethod.Invoke(confirmSingleton, new[] { unit, registration.Controller, registration.Popup });
            return NetherNativeActionResult.Started("native-boost-confirm-one-ticket");
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException("confirm-boost", ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome("confirm-boost-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private static NetherNativeActionResult ToNativeResult(NetherResultSceneStep step) => step.Kind switch
    {
        NetherResultSceneStepKind.Pending => NetherNativeActionResult.Started(step.Detail),
        NetherResultSceneStepKind.Succeeded => NetherNativeActionResult.Completed(step.Detail),
        NetherResultSceneStepKind.BindingUnavailable => NetherNativeActionResult.BindingUnavailable(step.Detail),
        NetherResultSceneStepKind.Canceled => NetherNativeActionResult.UnknownOutcome(step.Detail),
        _ => NetherNativeActionResult.UnknownOutcome(step.Detail),
    };

    private static NetherNativeActionResult PollResultTask(object task)
    {
        if (!TryReadMember(task, "Status", out object? rawStatus) || rawStatus == null)
            return NetherNativeActionResult.BindingUnavailable("missing-result-task-status");
        string status = rawStatus.ToString() ?? string.Empty;
        if (string.Equals(status, "Pending", StringComparison.Ordinal))
            return NetherNativeActionResult.Started("awaiting-native-result");
        if (string.Equals(status, "Succeeded", StringComparison.Ordinal))
            return NetherNativeActionResult.Completed("native-result-succeeded");
        if (string.Equals(status, "Canceled", StringComparison.Ordinal))
            return NetherNativeActionResult.UnknownOutcome("native-result-canceled");
        if (string.Equals(status, "Faulted", StringComparison.Ordinal))
            return NetherNativeActionResult.UnknownOutcome("native-result-faulted");
        return NetherNativeActionResult.UnknownOutcome("unknown-native-result-status:" + status);
    }

    private bool TryCompleteBattleTask(ref object? task, BattleTaskKind kind, out NetherNativeActionResult result)
    {
        if (task == null)
        {
            result = NetherNativeActionResult.Completed("no-battle-task");
            return true;
        }
        result = PollResultTask(task);
        if (result.Kind == NetherNativeActionResultKind.Started)
            return false;
        task = null;
        if (result.Kind != NetherNativeActionResultKind.Completed)
            return false;

        switch (kind)
        {
            case BattleTaskKind.Start:
                _battleActive = true;
                break;
            case BattleTaskKind.Clear:
                _battleActive = false;
                _battleClearObserved = true;
                _battleCloseObserved = false;
                break;
            case BattleTaskKind.Close:
                _battleActive = false;
                _battleCloseObserved = true;
                _battleClearObserved = false;
                break;
        }
        return true;
    }

    private NetherNativeActionResult TryInvokeExact(
        object target,
        NetherNativeMethodDescriptor descriptor,
        object[] arguments,
        string action,
        bool registerNativeActionTask = true,
        Action<object?>? observeTask = null
    )
    {
        if (!TryResolveExactMethod(target.GetType(), descriptor, InstanceFlags, out string error, out MethodInfo? method))
            return NetherNativeActionResult.BindingUnavailable(error);
        try
        {
            object? result = method!.Invoke(target, arguments);
            observeTask?.Invoke(result);
            if (registerNativeActionTask)
                RegisterNativeActionTask(result);
            return NetherNativeActionResult.Started("native-" + action);
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException(action, ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome(
                "native-" + action + "-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    private void RegisterNativeActionTask(object? task)
    {
        if (task == null)
            return;
        lock (_gate)
            _nativeActionTask = task;
    }

    private void RegisterCheckpointParentTask(object? task)
    {
        if (task == null)
            return;
        lock (_gate)
        {
            if (_pendingCheckpointAction == null)
                return;
            _checkpointParentTask = task;
            _checkpointParentTaskWait.ObserveRegistration();
            _checkpointTerminalTaskWait.ObserveRegistration();
        }
    }

    private void RegisterCheckpointChildTask(object? task)
    {
        if (task == null)
            return;
        lock (_gate)
        {
            if (_pendingCheckpointAction == null)
                return;
            _checkpointChildTask = task;
        }
    }

    private void RegisterFloorParentTask(object? task)
    {
        if (task == null)
            return;
        lock (_gate)
        {
            if (_floorParentAction == null)
                return;
            _floorEventSequenceTaskFlow.ObserveClickTask(task);
        }
    }

    private static NetherNativeActionResult TryInvokeNoArgumentDelegate(object callback, string action)
    {
        NetherNativeMethodDescriptor descriptor = new("Invoke", Array.Empty<string>(), "System.Void");
        if (!TryResolveExactMethod(callback.GetType(), descriptor, InstanceFlags, out string error, out MethodInfo? invoke))
            return NetherNativeActionResult.BindingUnavailable(error);
        try
        {
            invoke!.Invoke(callback, Array.Empty<object>());
            return NetherNativeActionResult.Started(action);
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException(action, ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome(action + "-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    /// <summary>
    /// Invokes only the current packaged Code-offer generated callbacks.  Unlike the legacy
    /// checkpoint helper below, this resolver understands BepInEx sanitization and exact
    /// ObfuscatedName contracts, so cpp2il's raw <c>&lt;&gt;c</c>/<c>CancellationToken</c> names
    /// cannot accidentally select a similarly-shaped wrong native method.
    /// </summary>
    private static NetherNativeActionResult TryInvokeVersionedGeneratedCallback(
        object controller,
        NetherCodePopupInteropMethodBinding binding,
        object?[] arguments,
        string action
    )
    {
        if (!NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
                controller.GetType(),
                binding,
                out string error,
                out object? singleton,
                out MethodInfo? method
            ))
        {
            return NetherNativeActionResult.BindingUnavailable(error);
        }

        try
        {
            object?[] invokeArguments = (object?[])arguments.Clone();
            ParameterInfo[] parameters = method!.GetParameters();
            if (parameters.Length != invokeArguments.Length)
                return NetherNativeActionResult.BindingUnavailable("binding-unavailable:" + action + ":argument-count");
            for (int index = 0; index < invokeArguments.Length; index++)
            {
                if (invokeArguments[index] == null)
                    invokeArguments[index] = CreateDefaultValue(parameters[index].ParameterType);
            }
            method.Invoke(singleton, invokeArguments);
            return NetherNativeActionResult.Started("native-" + action);
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException(action, ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome(
                "native-" + action + "-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    private static bool TryResolveExactMethod(
        Type type,
        NetherNativeMethodDescriptor expected,
        BindingFlags flags,
        out string error,
        out MethodInfo? method
    ) => NetherLifecycleInteropBindings.TryResolveExactMethod(type, expected, flags, out error, out method);

    private static MethodInfo? TryResolveExactMethod(
        Type type,
        NetherNativeMethodDescriptor expected,
        BindingFlags flags,
        out string error
    ) => TryResolveExactMethod(type, expected, flags, out error, out MethodInfo? method) ? method : null;

    private static string TypeName(Type type) => type.FullName ?? type.Name;

    private static Type? ResolveLoadedType(string typeName) =>
        NetherLifecycleInteropBindings.ResolveType(AppDomain.CurrentDomain.GetAssemblies(), typeName);

    private static object? CreateDefaultValue(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static string FormatInvocationException(string action, TargetInvocationException exception)
    {
        Exception detail = exception.InnerException ?? exception;
        return "native-" + action + "-exception:" + detail.GetType().Name + ":" + detail.Message;
    }

    private static bool TryMapCurrentFloor(
        object model,
        NetherData data,
        IReadOnlyList<NetherFloorNode> floors,
        out long currentFloorId,
        out long currentNodeId,
        out int floorLevel,
        out int floorIndex,
        out string error
    )
    {
        currentFloorId = 0;
        currentNodeId = 0;
        floorLevel = 0;
        floorIndex = 0;
        if (TryReadMember(model, "CurrentFloorModel", out object? current) && current != null
            && TryReadInt(current, "MNetherMapFloorId", out currentFloorId)
            && TryReadInt32(current, "FloorLevel", out floorLevel)
            && TryReadApiFloorIndex(current, out floorIndex))
        {
            return TryResolveCurrentRuntimeNode(
                floors,
                currentFloorId,
                floorLevel,
                floorIndex,
                out currentNodeId,
                out error
            );
        }

        // Sleep/Clear can be observed after the map presentation has torn down.  NetherData is
        // still the server-owned fallback; it is not a locally incremented projection.
        currentFloorId = data.MNetherMapFloorId;
        floorLevel = data.FloorLevel;
        floorIndex = data.FloorIndex;
        if (currentFloorId > 0 && floorLevel >= 0 && floorIndex >= 0)
        {
            return TryResolveCurrentRuntimeNode(
                floors,
                currentFloorId,
                floorLevel,
                floorIndex,
                out currentNodeId,
                out error
            );
        }

        error = "missing-current-floor";
        return false;
    }

    private static bool TryResolveCurrentRuntimeNode(
        IReadOnlyList<NetherFloorNode> floors,
        long currentFloorMasterId,
        int floorLevel,
        int apiFloorIndex,
        out long currentNodeId,
        out string error
    )
    {
        var accepted = new HashSet<long>();
        if (!NetherRuntimeFloorModelValidator.TryCreateNodeId(
                currentFloorMasterId,
                floorLevel,
                uiFloorIndex: 0,
                apiFloorIndex,
                accepted,
                out currentNodeId,
                out error
            ))
        {
            return false;
        }

        long resolvedNodeId = currentNodeId;
        NetherFloorNode? match = floors?.FirstOrDefault(node => node != null && node.NodeId == resolvedNodeId);
        if (match == null)
        {
            error = "current-runtime-node-not-in-map:node=" + currentNodeId
                + ":master=" + currentFloorMasterId
                + ":level=" + floorLevel
                + ":api-index=" + apiFloorIndex;
            currentNodeId = 0;
            return false;
        }
        if (match.FloorId != currentFloorMasterId)
        {
            error = "current-runtime-node-master-mismatch:node=" + currentNodeId
                + ":snapshot-master=" + currentFloorMasterId
                + ":map-master=" + match.FloorId;
            currentNodeId = 0;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadApiFloorIndex(object floor, out int apiFloorIndex) =>
        TryReadInt32(floor, "ApiFloorIndex", out apiFloorIndex)
        || TryReadInt32(floor, "FloorPosition", out apiFloorIndex);

    private static bool TryReadRuntimeFloorNodeIdentity(
        object floor,
        out long runtimeNodeId,
        out string error
    )
    {
        runtimeNodeId = 0;
        if (!TryReadInt(floor, "MNetherMapFloorId", out long masterFloorId)
            || !TryReadInt32(floor, "FloorLevel", out int floorLevel)
            || !TryReadInt32(floor, "FloorIndex", out int uiFloorIndex)
            || !TryReadApiFloorIndex(floor, out int apiFloorIndex))
        {
            error = "missing-runtime-floor-node-identity";
            return false;
        }

        return NetherRuntimeFloorModelValidator.TryCreateNodeId(
            masterFloorId,
            floorLevel,
            uiFloorIndex,
            apiFloorIndex,
            new HashSet<long>(),
            out runtimeNodeId,
            out error
        );
    }

    private static bool TryMapFloors(
        object model,
        out IReadOnlyList<NetherFloorNode>? floors,
        out string error
    )
    {
        floors = null;
        if (!TryReadMember(model, "MapModel", out object? mapModel) || mapModel == null
            || !TryReadMember(mapModel, "FloorModelListPerFloorLevel", out object? perLevel) || perLevel == null)
        {
            error = "missing-map-floor-model-list";
            return false;
        }

        var rawFloors = new List<NetherRuntimeFloorRaw>();
        foreach (object list in EnumerateDictionaryValues(perLevel))
        {
            foreach (object floor in Enumerate(list))
            {
                if (!TryReadInt(floor, "MNetherMapFloorId", out long id)
                    || !TryReadInt32(floor, "FloorLevel", out int level)
                    || !TryReadInt32(floor, "FloorIndex", out int index)
                    || !TryReadApiFloorIndex(floor, out int apiIndex)
                    || !TryReadInt32(floor, "FloorType", out int type)
                    || !TryReadBoolean(floor, "IsSecretFloor", out bool hidden)
                    || !TryReadBoolean(floor, "IsUnlocked", out bool unlocked))
                {
                    error = "missing-floor-model-member";
                    return false;
                }
                var previous = new List<long>();
                if (!TryReadMember(floor, "MNetherMapFloorPrevIds", out object? previousIds) || previousIds == null)
                {
                    error = "missing-floor-prev-ids:" + id;
                    return false;
                }
                foreach (object rawId in Enumerate(previousIds))
                {
                    if (!TryConvertInt64(rawId, out long previousId) || previousId <= 0)
                    {
                        error = "invalid-floor-prev-id:" + id;
                        return false;
                    }
                    previous.Add(previousId);
                }

                rawFloors.Add(new NetherRuntimeFloorRaw(id, level, index, apiIndex, ToFloorNodeType(type))
                {
                    IsHidden = hidden,
                    IsUnlocked = unlocked,
                    PreviousMasterFloorIds = previous,
                    RewardTier = 0,
                    OptionalCombatCount = type is (int)NetherFloorNodeType.Battle or (int)NetherFloorNodeType.MiniBoss ? 1 : 0,
                });
            }
        }

        if (!NetherRuntimeFloorGraphMapper.TryMap(rawFloors, out IReadOnlyList<NetherFloorNode> mapped, out error))
            return false;
        floors = mapped;
        return true;
    }

    private static bool TryMapCharacters(
        object model,
        out IReadOnlyList<NetherCharacterState>? characters,
        out string error
    )
    {
        characters = null;
        if (!TryReadMember(model, "PartyModel", out object? party) || party == null
            || !TryReadMember(party, "CharacterModels", out object? rawCharacters) || rawCharacters == null)
        {
            error = "missing-nether-party-model";
            return false;
        }

        var mapped = new List<NetherCharacterState>();
        foreach (object character in Enumerate(rawCharacters))
        {
            if (!TryReadInt(character, "MCharacterId", out long characterId)
                || !TryReadDouble(character, "HpRatio", out double ratio))
            {
                error = "missing-nether-character-member";
                return false;
            }
            if (characterId <= 0 || ratio is < 0d or > 1d)
            {
                error = "invalid-nether-character-state:" + characterId;
                return false;
            }
            bool active = !TryReadBoolean(character, "IsAlive", out bool alive) || alive;
            int hpPermille = checked((int)Math.Round(ratio * 1000d, MidpointRounding.AwayFromZero));
            mapped.Add(new NetherCharacterState(characterId, hpPermille, active));
        }

        if (mapped.Count == 0)
        {
            error = "empty-nether-party";
            return false;
        }
        characters = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryLoadMasterRows(
        MasterDataStore masterDataStore,
        long mapId,
        out MasterRows? rows,
        out string error
    )
    {
        rows = null;
        MNetherMaps[]? maps = masterDataStore.GetCache<MNetherMaps>();
        MNetherCodes[]? codes = masterDataStore.GetCache<MNetherCodes>();
        MItems[]? items = masterDataStore.GetCache<MItems>();
        if (maps == null || codes == null || items == null)
        {
            error = "missing-nether-master-cache";
            return false;
        }
        MNetherMaps? map = maps.FirstOrDefault(row => row != null && row.id == mapId);
        if (map == null || map.max_floor_num < 1)
        {
            error = "missing-m-nether-map:" + mapId;
            return false;
        }

        var codeById = new Dictionary<long, MNetherCodes>();
        foreach (MNetherCodes row in codes)
        {
            if (row != null && row.id > 0)
                codeById[row.id] = row;
        }
        var itemById = new Dictionary<long, MItems>();
        foreach (MItems row in items)
        {
            if (row != null && row.id > 0)
                itemById[row.id] = row;
        }
        if (codeById.Count == 0 || itemById.Count == 0)
        {
            error = "empty-nether-master-cache";
            return false;
        }

        rows = new MasterRows(new MapMaster(map.max_floor_num), codeById, itemById);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Derives the only legal post-Continue target from server-authoritative current floor data
    /// plus the packaged master link <c>m_nether_map_floor_id_next</c>.  The map-floor master
    /// contains no guessed map ordering, so an absent/broken link intentionally yields null and
    /// the controller pauses before Continue.
    /// </summary>
    private static NetherContinuationTarget? TryMapContinuationTarget(
        MasterDataStore masterDataStore,
        NetherData data,
        long currentFloorId
    )
    {
        if (masterDataStore == null || data == null || currentFloorId <= 0 || data.ContinuanceFloorLevel < 1)
            return null;

        try
        {
            MNetherMapFloors[]? floors = masterDataStore.GetCache<MNetherMapFloors>();
            if (floors == null || floors.Length == 0)
                return null;

            MNetherMapFloors? current = floors.FirstOrDefault(row => row != null && row.id == currentFloorId);
            if (current == null || current.m_nether_map_floor_id_next <= 0)
                return null;

            MNetherMapFloors? next = floors.FirstOrDefault(row =>
                row != null && row.id == current.m_nether_map_floor_id_next
            );
            if (next == null || next.id <= 0 || next.m_nether_map_id <= 0)
                return null;

            return new NetherContinuationTarget(
                next.m_nether_map_id,
                next.id,
                data.ContinuanceFloorLevel
            );
        }
        catch
        {
            // Master/cache failures cannot be reinterpreted as an actionable continuation
            // target.  The snapshot carries null and the controller fail-closes before mutation.
            return null;
        }
    }

    private static bool TryMapCodes(
        NetherDataStore dataStore,
        MasterRows rows,
        out IReadOnlyList<NetherCodeState>? codes,
        out string error
    )
    {
        codes = null;
        var mapped = new List<NetherCodeState>();
        object? possessionCodes = dataStore.GetPossessionNetherCodeDataEnumerable();
        if (!NetherRuntimeEnumerableReader.TryRead(
                possessionCodes,
                out List<object> rawPossessionCodes,
                out string enumerationError
            ))
        {
            error = "invalid-possession-nether-code-collection:" + enumerationError;
            return false;
        }
        foreach (object rawCode in rawPossessionCodes)
        {
            if (rawCode is not NetherCodeData code)
            {
                error = "invalid-possession-nether-code-type";
                return false;
            }
            if (code == null || code.MNetherCodeId <= 0 || code.Amount < 0)
            {
                error = "invalid-possession-nether-code";
                return false;
            }
            if (!rows.CodeById.TryGetValue(code.MNetherCodeId, out MNetherCodes? master))
            {
                error = "missing-m-nether-code:" + code.MNetherCodeId;
                return false;
            }
            mapped.Add(NetherCodeRuntimeSemanticMapper.MapState(
                master.id,
                master.category,
                master.effect_type,
                code.Amount,
                master.rarity
            ));
        }
        codes = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryMapAcquiredItems(
        NetherDataStore dataStore,
        MasterRows rows,
        out IReadOnlyList<NetherRewardItem>? items,
        out string error
    )
    {
        items = null;
        var mapped = new List<NetherRewardItem>();
        foreach (NetherItemData item in dataStore.GetOtherAcquiredItemDataList())
        {
            if (item == null || item.MItemId <= 0 || item.Amount <= 0)
            {
                error = "invalid-acquired-nether-item";
                return false;
            }
            if (!rows.ItemById.TryGetValue(item.MItemId, out MItems? master))
            {
                error = "missing-m-item:" + item.MItemId;
                return false;
            }
            mapped.Add(new NetherRewardItem(item.MItemId, item.Amount)
            {
                HasMasterData = true,
                // NetherItemData has no server-return-popup rarity.  It remains explicitly
                // unverified until the post-Continue ContentModel list is available.
                HasVerifiedDropRarity = false,
                ItemType = checked((int)master.type),
                DropRarity = NetherRewardRarity.NoEffect,
                MasterRarity = master.rarity,
            });
        }
        items = mapped;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Maps precisely the data consumed by NetherReturnItemSelectionPopupController.InitializeViewAsync:
    /// live GetOtherAcquiredItemDataList entries plus MItems content type/rarity.  This is kept
    /// separate from the snapshot mapper because LockReward==0 must not require any item master.
    /// </summary>
    private static bool TryMapAuthoritativeReturnPreflightItems(
        NetherDataStore dataStore,
        MasterDataStore? masterDataStore,
        out IReadOnlyList<NetherCheckpointReturnPreflightItem>? items,
        out string error
    )
    {
        items = null;
        if (dataStore == null || masterDataStore == null)
        {
            error = "missing-return-preflight-store-or-master";
            return false;
        }

        MItems[]? masterItems = masterDataStore.GetCache<MItems>();
        if (masterItems == null || masterItems.Length == 0)
        {
            error = "missing-return-preflight-m-items-cache";
            return false;
        }
        var masterById = new Dictionary<long, MItems>();
        foreach (MItems master in masterItems)
        {
            if (master != null && master.id > 0)
                masterById[master.id] = master;
        }
        if (masterById.Count == 0)
        {
            error = "empty-return-preflight-m-items-cache";
            return false;
        }

        var mapped = new List<NetherCheckpointReturnPreflightItem>();
        foreach (NetherItemData item in dataStore.GetOtherAcquiredItemDataList())
        {
            if (item == null || item.MItemId <= 0 || item.Amount <= 0)
            {
                error = "invalid-return-preflight-acquired-item";
                return false;
            }
            if (!masterById.TryGetValue(item.MItemId, out MItems? master))
            {
                error = "missing-return-preflight-m-item:" + item.MItemId;
                return false;
            }
            if (master.type is < int.MinValue or > int.MaxValue || master.rarity < 0)
            {
                error = "invalid-return-preflight-m-item:" + item.MItemId;
                return false;
            }

            mapped.Add(new NetherCheckpointReturnPreflightItem(item.MItemId, item.Amount)
            {
                HasMasterData = true,
                HasContentData = true,
                HasRarityData = true,
                ContentType = checked((int)master.type),
                MasterRarity = master.rarity,
            });
        }

        items = mapped;
        error = string.Empty;
        return true;
    }

    private static NetherCheckpointReturnPreflightDecision ReturnPreflightPause(string detail) => new()
    {
        Kind = NetherCheckpointReturnPreflightKind.Pause,
        PauseReason = NetherPauseReason.UnknownMasterData,
        Detail = detail,
    };

    private static NetherRuntimePopupResult TryMapEventPopup(
        PopupRegistration registration,
        NetherRuntimePopupKind kind,
        int rawFloorType
    )
    {
        if (!TryReadMember(registration.Controller, "_mNetherEventPartsArray", out object? rawParts) || rawParts == null)
            return NetherRuntimePopupResult.Failure("missing-native-event-part-array");

        var options = new List<NetherEventOption>();
        int optionNumber = 1;
        foreach (object rawPart in Enumerate(rawParts))
        {
            if (rawPart is not MNetherFloorEventParts part)
                return NetherRuntimePopupResult.Failure("invalid-native-event-part-type");
            if (!TryMapEventPart(part, out IReadOnlyList<NetherEffect>? effects, out string detail))
                return NetherRuntimePopupResult.Failure("event-part:" + optionNumber + ":" + detail);
            options.Add(new NetherEventOption(optionNumber, effects!));
            optionNumber++;
        }
        if (options.Count == 0)
            return NetherRuntimePopupResult.Failure("empty-native-event-part-array");

        return NetherRuntimePopupResult.Success(new NetherRuntimePopupContext
        {
            Kind = kind,
            RawFloorType = rawFloorType,
            Options = options,
        });
    }

    private static bool TryMapEventPart(
        MNetherFloorEventParts part,
        out IReadOnlyList<NetherEffect>? effects,
        out string detail
    )
    {
        effects = null;
        detail = string.Empty;
        if (part == null || part.id <= 0)
        {
            detail = "invalid-event-part";
            return false;
        }

        var mapped = new List<NetherEffect>();
        if (!TryMapTargetEffect(part.target_type_1, part.select_parameter_1, mapped, out detail)
            || !TryMapTargetEffect(part.target_type_2, part.select_parameter_2, mapped, out detail)
            || !TryMapTargetEffect(part.target_type_3, part.select_parameter_3, mapped, out detail))
        {
            return false;
        }

        if (part.content_type != 0)
        {
            if (part.amount < 0 || part.amount > int.MaxValue)
            {
                detail = "invalid-event-content";
                return false;
            }
            NetherEffect? contentEffect = part.content_type switch
            {
                // Project.Master.ContentType.Item / NetherItem, confirmed from the packaged
                // ContentType enum.  Their actual master lookup remains in the native popup.
                30 or 31 when part.content_id > 0 => new NetherEffect(NetherEffectKind.Item, (int)part.amount)
                {
                    ContentId = part.content_id,
                },
                160 when part.content_id == 0 => new NetherEffect(NetherEffectKind.AbyssCodeOffer, (int)part.amount),
                165 => new NetherEffect(NetherEffectKind.NetherGoldGain, (int)part.amount)
                {
                    ContentId = part.content_id,
                },
                166 => new NetherEffect(NetherEffectKind.TreasureKeyGain, (int)part.amount)
                {
                    ContentId = part.content_id,
                },
                _ => null,
            };
            if (contentEffect == null)
            {
                detail = "unsupported-event-content-type:" + part.content_type;
                return false;
            }
            mapped.Add(contentEffect);
        }

        if (mapped.Count is < 1 or > 4)
        {
            detail = "invalid-event-effect-count:" + mapped.Count;
            return false;
        }
        effects = mapped;
        return true;
    }

    private static bool TryMapTargetEffect(
        int rawType,
        long parameter,
        ICollection<NetherEffect> effects,
        out string detail
    )
    {
        detail = string.Empty;
        if (rawType == 0)
            return true;
        if (parameter < 0 || parameter > int.MaxValue || rawType is < 1 or > 8)
        {
            detail = "unsupported-event-target-type-or-parameter:" + rawType;
            return false;
        }

        NetherEffectKind kind = (NetherEffectKind)rawType;
        NetherEffect effect;
        if (kind == NetherEffectKind.AbyssCodeTransform)
        {
            effect = new NetherEffect(kind, 0);
        }
        else if (kind == NetherEffectKind.Battle)
        {
            // Selecting this event option, rather than the map floor itself, starts the
            // battle.  Treat it as optional so event policy can prefer non-battle choices.
            effect = new NetherEffect(kind, (int)parameter) { IsOptionalBattle = true };
        }
        else
        {
            effect = new NetherEffect(kind, (int)parameter);
        }
        effects.Add(effect);
        return true;
    }

    private static NetherRuntimePopupResult TryMapCodeListPopup(PopupRegistration registration)
    {
        if (!TryReadCodeListPopupType(registration.Controller, out int popupType))
            return NetherRuntimePopupResult.Failure("missing-native-code-list-popup-type");

        // Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopupType:
        // Normal=0, Change=1, Replace=2.  Only Change is a separately dispatchable
        // target_type=7 stage; Replace remains internal to the CodeOffer Receive task.
        return popupType == 1
            ? NetherRuntimePopupResult.Success(new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeTransform,
            })
            : NetherRuntimePopupResult.Failure("owned-code-list-internal-popup-type:" + popupType);
    }

    private static bool TryReadCodeListPopupType(object controller, out int popupType)
    {
        popupType = -1;
        return controller != null
            && TryReadMember(controller, "_popupType", out object? rawPopupType)
            && rawPopupType != null
            && TryConvertInt32(rawPopupType, out popupType);
    }

    private static NetherRuntimePopupResult TryMapShopPopup(PopupRegistration registration)
    {
        if (!TryReadMember(registration.Controller, "_mNetherFloorShopContentsArray", out object? rawContents) || rawContents == null)
            return NetherRuntimePopupResult.Failure("missing-native-shop-content-array");

        if (!NetherRuntimeEnumerableReader.TryRead(
                rawContents,
                out List<object> rawValues,
                out string enumerationDetail
            ))
        {
            return NetherRuntimePopupResult.Failure(
                "native-shop-content-enumeration:" + enumerationDetail
            );
        }

        MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
        MItems[]? itemRows = masterDataStore?.GetCache<MItems>();
        var itemById = new Dictionary<long, NetherShopItemMaster>();
        if (itemRows != null)
        {
            foreach (MItems item in itemRows)
            {
                if (item == null || item.id <= 0)
                    continue;
                if (!itemById.TryAdd(
                        item.id,
                        new NetherShopItemMaster(
                            item.id,
                            checked((int)item.type),
                            ToRewardRarity(item.rarity)
                        )
                    ))
                {
                    return NetherRuntimePopupResult.Failure(
                        "duplicate-native-shop-item-master:" + item.id
                    );
                }
            }
        }

        var rows = new List<NetherRawShopContent>(rawValues.Count);
        foreach (object rawContent in rawValues)
        {
            if (rawContent is not MNetherFloorShopContents content)
            {
                return NetherRuntimePopupResult.Failure(
                    "invalid-native-shop-content-type:"
                        + (rawContent.GetType().FullName ?? rawContent.GetType().Name)
                );
            }
            rows.Add(new NetherRawShopContent(
                content.id,
                content.content_type,
                content.content_id,
                checked((int)content.consume_amount),
                content.consume_content_type == 165,
                checked((int)content.amount)
            ));
        }

        NetherShopContentMapResult mapped = NetherShopContentMapper.Map(rows, itemById);
        if (!mapped.IsSuccess)
            return NetherRuntimePopupResult.Failure("native-shop-map:" + mapped.Detail);

        return NetherRuntimePopupResult.Success(new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.Shop,
            ShopContents = mapped.Contents,
        });
    }

    private static int LevelFromMaster(MNetherCodes row)
    {
        // Erosion code parameters are represented in the current master as a positive value.
        // Ability assets are deliberately not coerced into a level until their effect mapping
        // has been proven; callers will see IsKnown=false for those rows.
        if (row.effect_parameter_1 is > 0 and <= int.MaxValue)
            return checked((int)row.effect_parameter_1);
        return 1;
    }

    private static void LogCodeMasterSemanticAudit(IEnumerable<MNetherCodes> rows, bool detailedLogging)
    {
        if (!detailedLogging)
            return;
        try
        {
            Project.NetherCodeAbilityAssetDataStore? abilityStore = Engine.Get<Project.NetherCodeAbilityAssetDataStore>();
            NetherCodeMasterAudit[] audits = rows
                .OrderBy(row => row.id)
                .Take(NetherCodeDiagnosticAudit.MaximumEntries)
                .Select(row => CreateCodeMasterAudit(row, abilityStore))
                .ToArray();
            string? audit = NetherCodeDiagnosticAudit.Format(detailedLogging, audits);
            if (audit != null)
                Logger.Info("[F12][NetherClimb] " + audit);
        }
        catch (Exception ex)
        {
            // Diagnostics never alter category/rarity/level selection or erosion projection.
            // The exception type is sufficient to request a focused live dump.
            Logger.Info("[F12][NetherClimb] code-master-audit=unavailable:" + ex.GetType().Name);
        }
    }

    private static NetherCodeMasterAudit CreateCodeMasterAudit(
        MNetherCodes row,
        Project.NetherCodeAbilityAssetDataStore? abilityStore
    )
    {
        long abilityId = 0;
        string effectLevelType = "unavailable";
        string scopeType = "unavailable";
        string targetType = "unavailable";
        string abilityEffectType = "unavailable";
        try
        {
            Project.IAbilityEffectData? ability = abilityStore?.GetAbilityEffectAsset(row.id);
            if (ability != null)
            {
                abilityId = ability.ID;
                effectLevelType = ability.EffectLevelType.ToString();
                scopeType = RuntimeTypeIdentifier(ability.Scope);
                targetType = RuntimeTypeIdentifier(ability.Target);
                abilityEffectType = RuntimeTypeIdentifier(ability.GetAbilityEffect(LevelFromMaster(row), 0));
            }
        }
        catch (Exception ex)
        {
            abilityEffectType = "unavailable:" + ex.GetType().Name;
        }

        return new NetherCodeMasterAudit(
            row.id,
            row.category,
            row.effect_type,
            row.effect_parameter_1,
            row.effect_parameter_2,
            row.effect_parameter_3,
            row.rarity,
            row.power,
            row.asset_id ?? string.Empty,
            abilityId,
            effectLevelType,
            scopeType,
            targetType,
            abilityEffectType
        );
    }

    private static string RuntimeTypeIdentifier(object? value) => value?.GetType().FullName ?? "null";

    private static NetherSessionStatus ToSessionStatus(int value) => Enum.IsDefined(typeof(NetherSessionStatus), value)
        ? (NetherSessionStatus)value
        : NetherSessionStatus.Unknown;

    private static NetherFloorNodeType ToFloorNodeType(int value) => Enum.IsDefined(typeof(NetherFloorNodeType), value)
        ? (NetherFloorNodeType)value
        : NetherFloorNodeType.Unknown;

    private static NetherRewardRarity ToRewardRarity(int value) => value switch
    {
        >= 5 => NetherRewardRarity.UniqueWeapon,
        4 => NetherRewardRarity.Red,
        3 => NetherRewardRarity.Gold,
        2 => NetherRewardRarity.Purple,
        1 => NetherRewardRarity.Silver,
        _ => NetherRewardRarity.NoEffect,
    };

    private static string CreateCharacterHash(IEnumerable<NetherCharacterState> characters) => string.Join(
        ";",
        characters.OrderBy(character => character.CharacterId).Select(character =>
            character.CharacterId.ToString(CultureInfo.InvariantCulture) + ":"
            + character.HpPermille.ToString(CultureInfo.InvariantCulture) + ":"
            + (character.IsActive ? "1" : "0")
        )
    );

    private static string CreateCodeHash(IEnumerable<NetherCodeState> codes) => string.Join(
        ";",
        codes.OrderBy(code => code.CodeId).Select(code =>
            code.CodeId.ToString(CultureInfo.InvariantCulture) + ":"
            + code.Level.ToString(CultureInfo.InvariantCulture) + ":"
            + ((int)code.EffectKind).ToString(CultureInfo.InvariantCulture)
        )
    );

    private static string CreateMapHash(IEnumerable<NetherFloorNode> floors) => string.Join(
        ";",
        floors.OrderBy(floor => floor.NodeId).Select(floor =>
            floor.NodeId.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.FloorId.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.FloorLevel.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.FloorIndex.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.ApiFloorIndex.ToString(CultureInfo.InvariantCulture) + ":"
            + ((int)floor.NodeType).ToString(CultureInfo.InvariantCulture) + ":"
            + (floor.IsHidden ? "1" : "0") + ":"
            + (floor.IsUnlocked ? "1" : "0") + ":"
            + floor.RewardTier.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.OptionalCombatCount.ToString(CultureInfo.InvariantCulture) + ":"
            + string.Join(",", floor.PreviousFloorIds.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)))
        )
    );

    private static bool TryMapPristineReturnItems(
        object scroll,
        out IReadOnlyList<NetherRewardItem>? items,
        out string error
    )
    {
        items = null;
        if (!TryReadMember(scroll, "_contentModelList", out object? contentModels) || contentModels == null)
        {
            error = "missing-return-content-model-list";
            return false;
        }

        MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
        MItems[]? masterItems = masterDataStore?.GetCache<MItems>();
        if (masterItems == null || masterItems.Length == 0)
        {
            error = "missing-return-m-items-cache";
            return false;
        }
        var masterById = masterItems
            .Where(item => item != null && item.id > 0)
            .ToDictionary(item => item.id);
        if (masterById.Count == 0)
        {
            error = "empty-return-m-items-cache";
            return false;
        }

        var mapped = new List<NetherRewardItem>();
        foreach (object model in Enumerate(contentModels))
        {
            if (model is not ContentModel content || !TryReadContentItem(content, out long itemId, out int amount))
            {
                error = "invalid-pristine-return-content-model";
                return false;
            }
            if (!masterById.TryGetValue(itemId, out MItems? master))
            {
                error = "missing-return-m-item:" + itemId;
                return false;
            }
            if (master.type is < int.MinValue or > int.MaxValue || master.rarity < 0)
            {
                error = "invalid-return-m-item:" + itemId;
                return false;
            }

            mapped.Add(new NetherRewardItem(itemId, amount)
            {
                HasMasterData = true,
                HasVerifiedDropRarity = true,
                ItemType = checked((int)master.type),
                DropRarity = ToRewardRarity((int)content.ContentRarity),
                MasterRarity = master.rarity,
            });
        }
        if (mapped.Count == 0)
        {
            error = "empty-pristine-return-content-model-list";
            return false;
        }

        items = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryMapFreshReturnPreflightItems(
        IReadOnlyList<NetherRewardItem> items,
        out IReadOnlyList<NetherCheckpointReturnPreflightItem>? mappedItems,
        out string error
    )
    {
        mappedItems = null;
        if (items == null)
        {
            error = "missing-fresh-return-preflight-items";
            return false;
        }

        var mapped = new List<NetherCheckpointReturnPreflightItem>();
        foreach (NetherRewardItem item in items)
        {
            if (item == null)
            {
                error = "null-fresh-return-preflight-item";
                return false;
            }
            mapped.Add(new NetherCheckpointReturnPreflightItem(item.ItemId, item.Amount)
            {
                HasMasterData = item.HasMasterData,
                HasContentData = item.ItemType >= 0,
                HasRarityData = item.MasterRarity >= 0,
                ContentType = item.ItemType,
                MasterRarity = item.MasterRarity,
            });
        }

        mappedItems = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryGetReturnSelectionIndexes(
        object scroll,
        IReadOnlyList<NetherRewardItem> wanted,
        out IReadOnlyList<int>? indexes,
        out string error
    )
    {
        indexes = null;
        if (!TryReadMember(scroll, "_contentModelList", out object? contentModels) || contentModels == null)
        {
            error = "missing-return-content-model-list";
            return false;
        }
        var available = new List<(int Index, long ItemId, int Amount)>();
        int index = 0;
        foreach (object model in Enumerate(contentModels))
        {
            if (model is not ContentModel content || !TryReadContentItem(content, out long itemId, out int amount))
            {
                error = "missing-return-content-item-mapping";
                return false;
            }
            available.Add((index, itemId, amount));
            index++;
        }

        var selected = new List<int>();
        foreach (NetherRewardItem wantedItem in wanted)
        {
            int found = available.FindIndex(item => item.ItemId == wantedItem.ItemId && item.Amount == wantedItem.Amount);
            if (found < 0)
            {
                error = "unmapped-return-item:" + wantedItem.ItemId + ":" + wantedItem.Amount;
                return false;
            }
            selected.Add(available[found].Index);
            available.RemoveAt(found);
        }
        indexes = selected;
        error = string.Empty;
        return true;
    }

    private static bool TryFindContentIndex(object controller, string fieldName, long contentId, out int index, out string error)
    {
        index = -1;
        if (!TryReadMember(controller, fieldName, out object? contents) || contents == null)
        {
            error = "missing-shop-content-array";
            return false;
        }
        int candidateIndex = 0;
        foreach (object content in Enumerate(contents))
        {
            if (!TryReadInt(content, "id", out long id))
            {
                error = "missing-shop-content-id";
                return false;
            }
            if (id == contentId)
            {
                index = candidateIndex;
                error = string.Empty;
                return true;
            }
            candidateIndex++;
        }
        error = "unmapped-shop-content:" + contentId;
        return false;
    }

    private static bool TryFindCodeListSelection(
        object controller,
        long codeId,
        out int tabIndex,
        out int modelIndex,
        out string error
    )
    {
        tabIndex = -1;
        modelIndex = -1;
        if (!TryReadMember(controller, "_modelDictionary", out object? modelDictionary) || modelDictionary == null
            || !TryReadMember(controller, "TabIndexes", out object? tabIndexes) || tabIndexes == null)
        {
            error = "missing-code-list-model-dictionary";
            return false;
        }

        foreach (object entry in Enumerate(modelDictionary))
        {
            if (!TryReadMember(entry, "Key", out object? rawCategory) || rawCategory == null
                || !TryReadMember(entry, "Value", out object? models) || models == null
                || !TryConvertInt32(rawCategory, out int category))
            {
                error = "invalid-code-list-model-dictionary";
                return false;
            }

            int itemIndex = 0;
            foreach (object model in Enumerate(models))
            {
                if (!TryReadCodeId(model, out long candidateId))
                {
                    error = "missing-code-thumbnail-id";
                    return false;
                }
                if (candidateId == codeId)
                {
                    if (!TryGetDictionaryValue(tabIndexes, category, out object? rawTab) || rawTab == null || !TryConvertInt32(rawTab, out tabIndex))
                    {
                        error = "missing-code-list-tab-index:" + category;
                        return false;
                    }
                    modelIndex = itemIndex;
                    error = string.Empty;
                    return true;
                }
                itemIndex++;
            }
        }

        error = "unmapped-code-thumbnail:" + codeId;
        return false;
    }

    private static bool TryReadContentItem(ContentModel model, out long itemId, out int amount)
    {
        itemId = 0;
        amount = 0;
        itemId = model.ContentId;
        amount = model.Amount;
        return itemId > 0 && amount > 0;
    }

    private static bool TryReadCodeId(object model, out long codeId)
    {
        foreach (string name in new[] { "MNetherCodeId", "mNetherCodeId", "CodeId", "Id" })
        {
            if (TryReadInt(model, name, out codeId) && codeId > 0)
                return true;
        }
        codeId = 0;
        return false;
    }

    private static bool TryGetDictionaryValue(object dictionary, int key, out object? value)
    {
        value = null;
        foreach (object entry in Enumerate(dictionary))
        {
            if (TryReadMember(entry, "Key", out object? rawKey) && rawKey != null
                && TryConvertInt32(rawKey, out int currentKey) && currentKey == key
                && TryReadMember(entry, "Value", out value))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<object> EnumerateDictionaryValues(object dictionary)
    {
        foreach (object entry in Enumerate(dictionary))
        {
            if (TryReadMember(entry, "Value", out object? value) && value != null)
                yield return value;
        }
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        if (!NetherRuntimeEnumerableReader.TryRead(collection, out List<object> values, out _))
            yield break;
        foreach (object value in values)
            yield return value;
    }

    private static bool TryReadMember(object target, string name, out object? value)
    {
        value = null;
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(target);
            return true;
        }
        MethodInfo? getter = type.GetMethod("get_" + name, InstanceFlags, null, Type.EmptyTypes, null);
        if (getter != null)
        {
            value = getter.Invoke(target, Array.Empty<object>());
            return true;
        }
        FieldInfo? field = type.GetField(name, InstanceFlags)
            ?? type.GetField("<" + name + ">k__BackingField", InstanceFlags);
        if (field != null)
        {
            value = field.GetValue(target);
            return true;
        }
        return false;
    }

    private static bool TryReadInt(object target, string name, out long value)
    {
        value = 0;
        return TryReadMember(target, name, out object? raw) && raw != null && TryConvertInt64(raw, out value);
    }

    private static bool TryReadInt32(object target, string name, out int value)
    {
        value = 0;
        if (!TryReadInt(target, name, out long raw) || raw is < int.MinValue or > int.MaxValue)
            return false;
        value = (int)raw;
        return true;
    }

    private static bool TryReadDouble(object target, string name, out double value)
    {
        value = 0;
        if (!TryReadMember(target, name, out object? raw) || raw == null)
            return false;
        try
        {
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadBoolean(object target, string name, out bool value)
    {
        value = false;
        if (!TryReadMember(target, name, out object? raw) || raw == null)
            return false;
        try
        {
            value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConvertInt64(object raw, out long value)
    {
        value = 0;
        try
        {
            value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConvertInt32(object raw, out int value)
    {
        value = 0;
        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private enum EventFlowKind
    {
        Event,
        Recovery,
        Treasure,
    }

    private enum BattleTaskKind
    {
        Start,
        Clear,
        Close,
    }

    private readonly record struct PopupRegistration(
        object Controller,
        object Popup,
        object? Close,
        long Sequence,
        NetherActionKind OwnerAction,
        long OwnerGeneration,
        bool IsLive
    );

    private readonly record struct CheckpointControllerRegistration(
        object Controller,
        long Sequence,
        NetherActionKind OwnerAction,
        long OwnerGeneration,
        bool IsLive
    );

    private readonly record struct MapMaster(int MaxFloorFloorNumber);

    private sealed record MasterRows(
        MapMaster Map,
        IReadOnlyDictionary<long, MNetherCodes> CodeById,
        IReadOnlyDictionary<long, MItems> ItemById
    );
}
