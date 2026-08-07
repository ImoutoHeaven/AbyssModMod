#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Absf;
using Cysharp.Threading.Tasks;
using HarmonyLib;
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
internal interface INetherRuntimeBridge : INetherRuntimeParentDriver, INetherReadOnlyReconcileDriver, INetherBattleSettlementDriver
{
    bool HasRegisteredFloorSelection { get; }

    bool IsBattleActive { get; }

    bool IsResultObserved { get; }

    NetherRuntimeSnapshotResult TryCaptureSnapshot();

    NetherRuntimeCodeCandidatesResult TryGetCodeCandidates();

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

    NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items);

    bool TryConsumeResultSuccess();

    NetherNativeActionResult PollResultFlow();

    void ClearRegistrations();
}

/// <summary>
/// Reflection-only bindings for the versioned IL2CPP game surface.  The bridge has no
/// fallback to NetherApiDataStore: exact name, arity, parameter type and return type are
/// selected before an action is invoked.  This keeps a patched client from turning an
/// unknown build into a raw-request automation client.
/// </summary>
internal sealed class NetherRuntimeBridge : INetherRuntimeBridge
{
    private const string UniTaskTypeName = "Cysharp.Threading.Tasks.UniTask";
    private const string UnitTypeName = "UniRx.Unit";
    private const string NetherUtilityTypeName = "Project.Nether.NetherUtility";
    private const string NetherPartyModelTypeName = "Project.Nether.NetherPartyModel";

    private const string FloorSelectionTypeName = "Project.Nether.FloorSelection.SubViewController";
    private const string EventPopupControllerTypeName = "Project.Nether.NetherEventPopup.NetherEventPopupController";
    private const string RecoverPopupControllerTypeName = "Project.Nether.NetherRecoverPopup.NetherRecoverPopupController";
    private const string TreasurePopupControllerTypeName = "Project.Nether.NetherTreasurePopup.NetherTreasurePopupController";
    private const string ShopPopupControllerTypeName = "Project.Nether.NetherShopPopup.NetherShopPopupController";
    private const string CodeSelectPopupControllerTypeName = "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController";
    private const string CodeListPopupControllerTypeName = "Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopupController";
    private const string ReturnPopupControllerTypeName = "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnItemSelectionPopupController";
    private const string ReturnScrollControllerTypeName = "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnableItemScrollViewController";
    private const string ContinuePopupControllerTypeName = "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopupController";
    private const string BoostPopupControllerTypeName = "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopupController";
    private static readonly NetherReturnItemPolicy ReturnItemPolicy = new();
    private readonly NetherNativeWaitGate _resultTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeSelectionTaskWait = new(maximumMissingPolls: 600);
    private readonly NetherNativeWaitGate _codeReplacementPopupWait = new(maximumMissingPolls: 600);
    private const string NetherApiServiceTypeName = "Project.Ingame.Exploration.NetherAPIService";
    private const string ResultControllerTypeName = "Project.NetherTop.Result.SubViewController";
    private const string BottomRightViewTypeName = "Project.Ingame.BottomRightView";
    private const string PopupBaseTypeName = "Project.PopupBase";
    private const string MonoBehaviourWithUniTaskTypeName = "Absf.MonoBehaviourWithUniTask";

    private readonly object _gate = new();
    private readonly NetherPopupOwnershipRegistry _popupOwnership = new();
    private readonly NetherNativeWaitGate _floorParentTaskWait = new(maximumMissingPolls: 600);
    private object? _floorSelectionController;
    private NetherPlannedAction? _floorParentAction;
    private long _floorParentGeneration;
    private object? _floorParentTask;
    private PopupRegistration? _eventPopup;
    private PopupRegistration? _recoverPopup;
    private PopupRegistration? _treasurePopup;
    private PopupRegistration? _shopPopup;
    private PopupRegistration? _codeSelectPopup;
    private PopupRegistration? _codeListPopup;
    private PopupRegistration? _returnPopup;
    private PopupRegistration? _continuePopup;
    private PopupRegistration? _boostPopup;
    private object? _returnScrollController;
    private object? _nativeActionTask;
    private object? _codeSelectionTask;
    private bool _battleActive;
    private bool _battleClearObserved;
    private bool _battleCloseObserved;
    private bool _awaitingBoostConfirmation;
    private bool _resultObserved;
    private object? _resultTask;
    private object? _battleStartTask;
    private object? _battleClearTask;
    private object? _battleCloseTask;
    private NetherPlannedAction? _pendingCheckpointAction;
    private bool _checkpointCallbackSubmitted;
    private bool _checkpointReturnSubmitted;
    private readonly NetherCheckpointNativeFlow _checkpointFlow = new();
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
                return _resultObserved;
        }
    }

    private NetherRuntimeBridge() { }

    // Static registration entry points are deliberately small; Harmony only reports native
    // lifecycle boundaries and never makes an automation decision from a postfix.
    public static void RegisterFloorSelection(object controller) => Instance.RegisterFloorSelectionCore(controller);

    public static void UnregisterFloorSelection(object controller) => Instance.UnregisterFloorSelectionCore(controller);

    public static void RegisterCodePopup(object controller, object popup) => Instance.RegisterPopupCore(controller, popup, null);

    public static void RegisterReturnPopup(object controller, object popup) => Instance.RegisterPopupCore(controller, popup, null);

    public static void ObserveBattleStart() => Instance.ObserveBattleStartCore();

    public static void ObserveBattleClear() => Instance.ObserveBattleClearCore();

    public static void ObserveBattleClose() => Instance.ObserveBattleCloseCore();

    public static void ObserveBattleTask(MethodBase originalMethod, object task) => Instance.ObserveBattleTaskCore(originalMethod, task);

    public static void ObserveResult() => Instance.ObserveResultCore(null);

    public static void ObserveResult(object resultTask) => Instance.ObserveResultCore(resultTask);

    /// <summary>
    /// Observes the exact generated confirmation task behind an Abyss code-offer Receive click.
    /// The task is started by the native controller callback and is never synthesized here.
    /// </summary>
    public static void ObserveCodeSelectionTask(object resultTask) => Instance.ObserveCodeSelectionTaskCore(resultTask);

    internal static IEnumerable<MethodBase> GetPatchTargets()
    {
        foreach (NativePatchBinding binding in PatchBindings)
        {
            Type? type = AccessTools.TypeByName(binding.TypeName);
            if (type == null)
                continue;

            MethodInfo? method = TryResolveExactMethod(type, binding.Method, binding.Flags, out _);
            if (method != null)
                yield return method;
        }
    }

    internal static IEnumerable<MethodBase> GetBattleTaskPatchTargets()
    {
        foreach (NativePatchBinding binding in PatchBindings.Where(binding => binding.TypeName == NetherApiServiceTypeName))
        {
            Type? type = AccessTools.TypeByName(binding.TypeName);
            if (type == null)
                continue;
            MethodInfo? method = TryResolveExactMethod(type, binding.Method, binding.Flags, out _);
            if (method != null)
                yield return method;
        }
    }

    internal static MethodBase? GetCodeSelectionTaskPatchTarget()
    {
        Type? type = AccessTools.TypeByName(NetherUtilityTypeName);
        if (type == null)
            return null;
        return TryResolveExactMethod(
            type,
            new NetherNativeMethodDescriptor(
                "<OpenAbyssCodeSelectPopupIfNeededAsync>g__HandleConfirmSequenceAsync|19_2",
                new[]
                {
                    CodeSelectPopupControllerTypeName,
                    "System.Int64",
                    NetherPartyModelTypeName,
                    "System.Threading.CancellationToken",
                },
                UniTaskTypeName
            ),
            StaticFlags,
            out _
        );
    }

    internal static void ObservePatchedCall(MethodBase originalMethod, object instance, object[] arguments)
    {
        if (originalMethod == null || instance == null)
            return;

        string typeName = originalMethod.DeclaringType?.FullName ?? string.Empty;
        string methodName = originalMethod.Name;
        if (typeName == FloorSelectionTypeName && methodName == "HandleStartEventByStatusAsync")
        {
            RegisterFloorSelection(instance);
            return;
        }

        if (typeName == FloorSelectionTypeName && methodName == "Project.ISubService.Terminate")
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

            if (!TryMapCurrentFloor(model, data, out long currentFloorId, out int floorLevel, out int floorIndex, out string floorError))
                return NetherRuntimeSnapshotResult.Failure(floorError);
            if (!TryMapFloors(model, out IReadOnlyList<NetherFloorNode>? floors, out string mapError))
                return NetherRuntimeSnapshotResult.Failure(mapError);
            if (!TryMapCharacters(model, out IReadOnlyList<NetherCharacterState>? characters, out string characterError))
                return NetherRuntimeSnapshotResult.Failure(characterError);
            if (!TryLoadMasterRows(masterDataStore, mapId, out MasterRows? rows, out string masterError))
                return NetherRuntimeSnapshotResult.Failure(masterError);
            if (!TryMapCodes(dataStore, rows!, out IReadOnlyList<NetherCodeState>? codes, out string codeError))
                return NetherRuntimeSnapshotResult.Failure(codeError);
            if (!TryMapAcquiredItems(dataStore, rows!, out IReadOnlyList<NetherRewardItem>? acquiredItems, out string itemError))
                return NetherRuntimeSnapshotResult.Failure(itemError);

            NetherSnapshot snapshot = new()
            {
                Status = ToSessionStatus(statusValue),
                NetherId = netherId,
                MapId = mapId,
                CurrentFloorId = currentFloorId,
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
                Characters = characters!,
                Codes = codes!,
                Floors = floors!,
                AcquiredItems = acquiredItems!,
                CharacterHpHash = CreateCharacterHash(characters!),
                CodeHash = CreateCodeHash(codes!),
                MapHash = CreateMapHash(floors!),
            };
            return NetherRuntimeSnapshotResult.Success(snapshot);
        }
        catch (Exception ex)
        {
            return NetherRuntimeSnapshotResult.Failure(
                "snapshot-map-exception:" + ex.GetType().Name + ":" + ex.Message
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

            bool detailedLogging = Config.NetherAutoClimbDetailedLogging?.Value ?? false;
            Dictionary<long, MNetherCodes>? unmappedMasters = detailedLogging
                ? new Dictionary<long, MNetherCodes>()
                : null;
            var candidates = new List<NetherCodeCandidate>();
            foreach (object rawCodeId in Enumerate(selectableCodeIds))
            {
                if (!TryConvertInt64(rawCodeId, out long codeId) || codeId <= 0)
                    return NetherRuntimeCodeCandidatesResult.Failure("invalid-selectable-nether-code-id");
                if (!masterById.TryGetValue(codeId, out MNetherCodes? row))
                    return NetherRuntimeCodeCandidatesResult.Failure("missing-m-nether-code:" + codeId);
                (NetherCodeEffectKind kind, bool known) = MapCodeEffect(row.id, row.effect_type);
                if (!known && unmappedMasters != null)
                    unmappedMasters[row.id] = row;
                candidates.Add(new NetherCodeCandidate(row.id, kind, LevelFromMaster(row))
                {
                    IsKnown = known,
                    Rarity = row.rarity,
                    PartyCoverage = 0,
                    IsResearchOnly = false,
                });
            }

            // A full portfolio can pause before an offer is chosen.  Include its unknown master
            // rows in the same strictly bounded diagnostic sample, but only while detailed
            // logging is explicitly enabled.
            if (unmappedMasters != null)
            {
                foreach (object rawCode in Enumerate(dataStore.GetPossessionNetherCodeDataEnumerable()))
                {
                    if (rawCode is NetherCodeData code
                        && code != null
                        && masterById.TryGetValue(code.MNetherCodeId, out MNetherCodes? master))
                    {
                        (NetherCodeEffectKind _, bool currentCodeKnown) = MapCodeEffect(master.id, master.effect_type);
                        if (!currentCodeKnown)
                            unmappedMasters[master.id] = master;
                    }
                }
                LogUnknownCodeMasterAudit(unmappedMasters.Values, detailedLogging);
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
            _floorParentTask = null;
            _floorParentTaskWait.Clear();
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

    public NetherNativeActionResult InvokeOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    )
    {
        if (parent.Kind != NetherActionKind.SelectFloor || popup == null)
            return NetherNativeActionResult.BindingUnavailable("invalid-owned-popup-parent");
        lock (_gate)
        {
            if (_floorParentAction != parent || _floorParentGeneration < 1
                || popup.OwnerAction != NetherActionKind.SelectFloor
                || popup.OwnerGeneration != _floorParentGeneration
                || !_popupOwnership.TryGetOwned(NetherActionKind.SelectFloor, _floorParentGeneration, out NetherPopupOwnership ownership)
                || ownership.Sequence != popup.Sequence
                || FindPopupRegistration(ownership) == null)
            {
                return NetherNativeActionResult.BindingUnavailable("missing-matching-owned-popup");
            }
        }

        return action.Kind switch
        {
            NetherActionKind.SelectEventOption => SelectEventOption(action),
            NetherActionKind.LeaveShop => LeaveShop(),
            NetherActionKind.BuyShopItem => BuyShopItem(action),
            NetherActionKind.SelectCode => SelectCode(action),
            NetherActionKind.ReloadCode => ReloadCode(),
            _ => NetherNativeActionResult.Rejected("unsupported-owned-popup-action:" + action.Kind),
        };
    }

    public NetherNativeActionResult PollFloorParent()
    {
        lock (_gate)
            return PollFloorParentNativeFlow();
    }

    private NetherNativeActionResult PollFloorParentTask()
    {
        object? task;
        lock (_gate)
        {
            if (_floorParentAction == null)
                return NetherNativeActionResult.Completed("no-floor-parent");
            task = _floorParentTask;
            if (task == null)
                return _floorParentTaskWait.AwaitRegistration("floor-parent");
        }

        NetherNativeActionResult result = PollResultTask(task);
        if (result.Kind != NetherNativeActionResultKind.Completed)
            return result;

        lock (_gate)
        {
            // `OnFloorClickedEventAsync` is the parent proof for Event/Treasure's inner
            // UniTask.Void callbacks.  A completed child click alone never reaches here.
            ClearFloorParentCore();
        }
        return result;
    }

    private NetherRuntimePopupResult TryMapPopupRegistration(PopupRegistration registration)
    {
        string controllerType = registration.Controller.GetType().FullName ?? string.Empty;
        try
        {
            NetherRuntimePopupResult mapped = controllerType switch
            {
                CodeSelectPopupControllerTypeName => NetherRuntimePopupResult.Success(new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer }),
                CodeListPopupControllerTypeName => NetherRuntimePopupResult.Failure("owned-code-list-is-not-code-offer"),
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
        return captured.IsSuccess
            ? NetherReadOnlySnapshotResult.Success(captured.Snapshot!)
            : NetherReadOnlySnapshotResult.Failure(captured.Detail);
    }

    public NetherNativeActionResult Invoke(NetherPlannedAction action) => action.Kind switch
    {
        NetherActionKind.Reconcile => Reconcile(),
        NetherActionKind.SelectFloor => SelectFloor(action),
        NetherActionKind.SelectEventOption => SelectEventOption(action),
        NetherActionKind.LeaveShop => LeaveShop(),
        NetherActionKind.BuyShopItem => BuyShopItem(action),
        NetherActionKind.SelectCode => SelectCode(action),
        NetherActionKind.ReloadCode => ReloadCode(),
        NetherActionKind.Continue => Continue(action),
        NetherActionKind.FinishAtCheckpoint => FinishAtCheckpoint(),
        _ => NetherNativeActionResult.Rejected("unsupported-native-action:" + action.Kind),
    };

    public NetherNativeActionResult PollNativeFlow()
    {
        lock (_gate)
        {
            if (_floorParentAction != null)
                return PollFloorParentNativeFlow();

            // A one-ticket continue can open a boost confirmation before it creates the return
            // popup.  Confirm its native UI first; selecting a stale/early return list would
            // otherwise race the server continuation response.
            if (_awaitingBoostConfirmation)
            {
                if (_boostPopup == null)
                    return NetherNativeActionResult.Started("awaiting-native-boost-popup");

                NetherNativeActionResult boost = ConfirmBoostOneTicket(_boostPopup.Value);
                if (boost.Kind == NetherNativeActionResultKind.Started)
                {
                    if (!_checkpointFlow.SubmitBoostConfirmation())
                        return NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-boost-sequence");
                    _awaitingBoostConfirmation = false;
                }
                return boost;
            }

            if (_pendingCheckpointAction != null)
            {
                NetherNativeActionResult checkpoint = PollCheckpointFlow();
                if (checkpoint.Kind != NetherNativeActionResultKind.Completed || _pendingCheckpointAction == null)
                    return checkpoint;
            }

            if (_codeSelectionFlow.Stage is not (NetherCodeSelectionNativeStage.Idle or NetherCodeSelectionNativeStage.Completed)
                || _codeSelectionTask != null)
            {
                return PollCodeSelectionFlow();
            }

            if (_resultTask != null)
                return PollResultTask(_resultTask);

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

    public NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        PopupRegistration? registration;
        object? scroll;
        lock (_gate)
        {
            registration = _returnPopup;
            scroll = _returnScrollController;
        }
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

        object? scroll;
        lock (_gate)
            scroll = _returnScrollController;
        if (scroll == null)
            return NetherNativeActionResult.BindingUnavailable("missing-return-scroll-for-pristine-map");

        if (!TryMapPristineReturnItems(scroll, out IReadOnlyList<NetherRewardItem>? items, out string mappingError))
            return NetherNativeActionResult.BindingUnavailable(mappingError);

        var preserveIds = new HashSet<long>(action.ReturnPreserveItemIds);
        NetherReturnItemSelection selection = ReturnItemPolicy.Select(items!, action.ReturnLockReward, preserveIds);
        if (selection.Kind == NetherReturnItemSelectionKind.Pause)
        {
            return NetherNativeActionResult.BindingUnavailable(
                "return-popup-policy:" + selection.PauseReason + ":" + selection.Detail
            );
        }

        return SelectReturnItems(selection.Items);
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
            if (_resultTask == null)
                return false;
            NetherNativeActionResult result = PollResultTask(_resultTask);
            if (result.Kind != NetherNativeActionResultKind.Completed)
                return false;
            _resultTask = null;
            _resultTaskWait.Clear();
            _battleStartTask = null;
            _battleClearTask = null;
            _battleCloseTask = null;
            _pendingCheckpointAction = null;
            _checkpointCallbackSubmitted = false;
            _checkpointReturnSubmitted = false;
            _checkpointFlow.Clear();
            _resultObserved = true;
            return true;
        }
    }

    public NetherNativeActionResult PollResultFlow()
    {
        lock (_gate)
        {
            if (_resultTask == null)
                return _resultTaskWait.AwaitRegistration("result");
            NetherNativeActionResult result = PollResultTask(_resultTask);
            if (result.Kind == NetherNativeActionResultKind.Completed)
            {
                _resultTask = null;
                _resultObserved = true;
                _resultTaskWait.Clear();
            }
            return result;
        }
    }

    public void ClearRegistrations()
    {
        lock (_gate)
        {
            _floorSelectionController = null;
            _floorParentAction = null;
            _floorParentGeneration = 0;
            _floorParentTask = null;
            _floorParentTaskWait.Clear();
            _popupOwnership.Clear();
            _eventPopup = null;
            _recoverPopup = null;
            _treasurePopup = null;
            _shopPopup = null;
            _codeSelectPopup = null;
            _codeListPopup = null;
            _returnPopup = null;
            _continuePopup = null;
            _boostPopup = null;
            _returnScrollController = null;
            _nativeActionTask = null;
            ClearCodeSelectionFlow();
            _awaitingBoostConfirmation = false;
            _resultTask = null;
            _resultObserved = false;
            _resultTaskWait.Clear();
            _battleActive = false;
            _battleClearObserved = false;
            _battleCloseObserved = false;
            _battleStartTask = null;
            _battleClearTask = null;
            _battleCloseTask = null;
            _pendingCheckpointAction = null;
            _checkpointCallbackSubmitted = false;
            _checkpointReturnSubmitted = false;
            _checkpointFlow.Clear();
            _popupSequence = 0;
        }
    }

    private void RegisterFloorSelectionCore(object controller)
    {
        if (controller == null)
            return;
        lock (_gate)
            _floorSelectionController = controller;
    }

    private void UnregisterFloorSelectionCore(object controller)
    {
        if (controller == null)
            return;
        lock (_gate)
        {
            if (ReferenceEquals(_floorSelectionController, controller))
            {
                // All popup/controller registrations belong to this FloorSelection scene.  Do
                // not retain a callback across scene teardown: its target may have been
                // destroyed while a UI result is still visually pending.
                ClearRegistrations();
            }
        }
    }

    private void RegisterPopupCore(object controller, object popup, object? close)
    {
        if (controller == null || popup == null)
            return;

        string typeName = controller.GetType().FullName ?? string.Empty;
        lock (_gate)
        {
            NetherActionKind ownerAction = NetherActionKind.None;
            long ownerGeneration = 0;
            long sequence = checked(++_popupSequence);
            if (_floorParentAction != null && _floorParentGeneration > 0)
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
                case CodeSelectPopupControllerTypeName:
                    _codeSelectPopup = registration;
                    break;
                case CodeListPopupControllerTypeName:
                    _codeListPopup = registration;
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
            }
        }
    }

    private void InvalidatePopupCore(object popup)
    {
        if (popup == null)
            return;
        lock (_gate)
        {
            InvalidatePopup(ref _eventPopup, popup);
            InvalidatePopup(ref _recoverPopup, popup);
            InvalidatePopup(ref _treasurePopup, popup);
            InvalidatePopup(ref _shopPopup, popup);
            InvalidatePopup(ref _codeSelectPopup, popup);
            InvalidatePopup(ref _codeListPopup, popup);
            InvalidatePopup(ref _returnPopup, popup);
            InvalidatePopup(ref _continuePopup, popup);
            InvalidatePopup(ref _boostPopup, popup);
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
        if (_floorParentAction == null)
            return true;
        return candidate.OwnerAction == NetherActionKind.SelectFloor
            && candidate.OwnerGeneration == _floorParentGeneration;
    }

    private void ClearFloorParentCore()
    {
        long generation = _floorParentGeneration;
        _popupOwnership.InvalidateOwner(NetherActionKind.SelectFloor, generation);
        ClearFloorPopup(ref _eventPopup, generation);
        ClearFloorPopup(ref _recoverPopup, generation);
        ClearFloorPopup(ref _treasurePopup, generation);
        ClearFloorPopup(ref _shopPopup, generation);
        ClearFloorPopup(ref _codeSelectPopup, generation);
        ClearFloorPopup(ref _codeListPopup, generation);
        _floorParentAction = null;
        _floorParentGeneration = 0;
        _floorParentTask = null;
        _floorParentTaskWait.Clear();
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
            _returnScrollController = controller;
    }

    private void ObserveBattleStartCore()
    {
        lock (_gate)
            _battleActive = true;
    }

    private void ObserveBattleClearCore()
    {
        lock (_gate)
        {
            _battleActive = false;
            _battleClearObserved = true;
            _battleCloseObserved = false;
        }
    }

    private void ObserveBattleCloseCore()
    {
        lock (_gate)
        {
            _battleActive = false;
            _battleCloseObserved = true;
            _battleClearObserved = false;
        }
    }

    private void ObserveBattleTaskCore(MethodBase originalMethod, object task)
    {
        if (originalMethod == null || task == null)
            return;
        lock (_gate)
        {
            switch (originalMethod.Name)
            {
                case "StartQuestAsync":
                    _battleStartTask = task;
                    break;
                case "ClearQuestAsync":
                    _battleClearTask = task;
                    break;
                case "CloseQuestAsync":
                    _battleCloseTask = task;
                    break;
            }
        }
    }

    private void ObserveResultCore(object? resultTask)
    {
        lock (_gate)
        {
            _resultObserved = true;
            if (resultTask != null)
            {
                _resultTask = resultTask;
                _resultTaskWait.ObserveRegistration();
            }
        }
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

    private void ClearCodeSelectionFlow()
    {
        _codeSelectionTask = null;
        _codeSelectionTaskWait.Clear();
        _codeReplacementPopupWait.Clear();
        _codeSelectionFlow.Clear();
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

        try
        {
            selectMethod!.Invoke(registration.Controller, new object[] { registration.Popup, action.OptionNumber - 1 });
            terminalMethod!.Invoke(registration.Controller, new[] { registration.Popup });
            return NetherNativeActionResult.Started("native-event-option:" + selected.Kind);
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException("select-event-option", ex));
        }
        catch (Exception ex)
        {
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

    private NetherNativeActionResult BuyShopItem(NetherPlannedAction action)
    {
        if (action.ContentId <= 0)
            return NetherNativeActionResult.Rejected("invalid-shop-content-id");
        PopupRegistration? registration;
        lock (_gate)
            registration = _shopPopup;
        if (registration == null || !IsCurrentFloorOwnedPopup(registration))
            return NetherNativeActionResult.BindingUnavailable("missing-shop-popup");
        if (!TryFindContentIndex(registration.Value.Controller, "_mNetherFloorShopContentsArray", action.ContentId, out int index, out string indexError))
            return NetherNativeActionResult.BindingUnavailable(indexError);

        return TryInvokeExact(
            registration.Value.Controller,
            new NetherNativeMethodDescriptor(
                "OnPurchaseContentAsync",
                new[] { registration.Value.Popup.GetType().FullName ?? string.Empty, "System.Int32" },
                UniTaskTypeName
            ),
            new object[] { registration.Value.Popup, index },
            "buy-shop-item"
        );
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
        NetherNativeActionResult selectDetail = TryInvokeGeneratedCallback(
            registration.Value.Controller,
            NetherCodePopupNativeBinding.DetailCallback,
            new[] { "System.Int32", CodeSelectPopupControllerTypeName, registration.Value.Popup.GetType().FullName ?? string.Empty },
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
        NetherNativeActionResult confirm = TryInvokeGeneratedCallback(
            registration.Value.Controller,
            NetherCodePopupNativeBinding.ConfirmDescriptor(CodeSelectPopupControllerTypeName),
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

    private NetherNativeActionResult ReloadCode()
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeSelectPopup;
        if (registration == null || !IsCurrentFloorOwnedPopup(registration))
            return NetherNativeActionResult.BindingUnavailable("missing-code-select-popup");

        return TryInvokeExact(
            registration.Value.Controller,
            new NetherNativeMethodDescriptor(
                "RerollAsync",
                new[] { registration.Value.Popup.GetType().FullName ?? string.Empty },
                UniTaskTypeName
            ),
            new[] { registration.Value.Popup },
            "reload-code"
        );
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
            floorController = _floorSelectionController;
            _continuePopup = null;
            _returnPopup = null;
            _returnScrollController = null;
            _checkpointCallbackSubmitted = false;
            _checkpointReturnSubmitted = false;
            _pendingCheckpointAction = action;
        }
        if (floorController == null)
        {
            lock (_gate)
            {
                _pendingCheckpointAction = null;
                _checkpointFlow.Clear();
            }
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
            "checkpoint-native-flow"
        );
        if (start.Kind is not (NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed))
        {
            lock (_gate)
            {
                _pendingCheckpointAction = null;
                _checkpointFlow.Clear();
            }
        }
        return start;
    }

    private NetherNativeActionResult PollCheckpointFlow()
    {
        if (_pendingCheckpointAction == null)
            return NetherNativeActionResult.Completed("no-pending-checkpoint-flow");
        NetherPlannedAction action = _pendingCheckpointAction.Value;

        PopupRegistration? registration;
        registration = _continuePopup;
        if (!_checkpointCallbackSubmitted)
        {
            if (registration == null)
                return NetherNativeActionResult.Started("awaiting-native-continue-popup");

            NetherNativeActionResult callback;
            if (action.Kind == NetherActionKind.Continue)
            {
                bool canBoost;
                if (!TryReadBoolean(registration.Value.Controller, "_canBoost", out canBoost))
                    return NetherNativeActionResult.BindingUnavailable("missing-continue-can-boost-field");
                callback = TryInvokeGeneratedCallback(
                    registration.Value.Controller,
                    "<SetupPopupEvent>b__8_2",
                    new[] { UnitTypeName, ContinuePopupControllerTypeName },
                    new object?[] { null, registration.Value.Controller },
                    "continue-one-ticket"
                );
                if (callback.Kind == NetherNativeActionResultKind.Started)
                {
                    if (!_checkpointFlow.SubmitContinue(canBoost))
                        return NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-continue-sequence");
                    _awaitingBoostConfirmation = canBoost;
                }
            }
            else
            {
                callback = TryInvokeGeneratedCallback(
                    registration.Value.Controller,
                    "<SetupPopupEvent>b__8_1",
                    new[] { UnitTypeName, ContinuePopupControllerTypeName },
                    new object?[] { null, registration.Value.Controller },
                    "finish-at-checkpoint"
                );
                if (callback.Kind == NetherNativeActionResultKind.Started && !_checkpointFlow.SubmitFinish())
                    return NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-finish-sequence");
            }
            if (callback.Kind != NetherNativeActionResultKind.Started)
                return callback;
            _checkpointCallbackSubmitted = true;
            return NetherNativeActionResult.Started("native-checkpoint-callback-submitted");
        }

        if (action.Kind == NetherActionKind.Continue && _checkpointFlow.CanSubmitReturnSelection && !_checkpointReturnSubmitted)
        {
            if (_returnPopup == null || _returnScrollController == null)
                return NetherNativeActionResult.Started("awaiting-native-return-popup-pristine-list");
            NetherNativeActionResult select = SelectCheckpointReturnItems(action);
            if (select.Kind == NetherNativeActionResultKind.Started)
            {
                if (!_checkpointFlow.SubmitReturnSelection())
                    return NetherNativeActionResult.BindingUnavailable("invalid-native-checkpoint-return-sequence");
                _checkpointReturnSubmitted = true;
            }
            return select;
        }

        if (_nativeActionTask != null)
        {
            NetherNativeActionResult task = PollResultTask(_nativeActionTask);
            if (task.Kind != NetherNativeActionResultKind.Started)
            {
                _nativeActionTask = null;
                _pendingCheckpointAction = null;
                _checkpointFlow.Complete();
            }
            return task;
        }
        _pendingCheckpointAction = null;
        _checkpointFlow.Complete();
        return NetherNativeActionResult.Completed("checkpoint-native-flow-completed");
    }

    private NetherNativeActionResult ConfirmBoostOneTicket(PopupRegistration registration)
    {
        NetherNativeMethodDescriptor setCount = new(
            "<SetupPopupEvent>b__7_2",
            new[] { "System.Int32", BoostPopupControllerTypeName, registration.Popup.GetType().FullName ?? string.Empty },
            "System.Void"
        );
        NetherNativeMethodDescriptor confirm = new(
            "<SetupPopupEvent>b__7_1",
            new[] { UnitTypeName, BoostPopupControllerTypeName, registration.Popup.GetType().FullName ?? string.Empty },
            "System.Void"
        );
        if (!TryResolveGeneratedCallback(registration.Controller.GetType(), setCount, out string setError, out object? singleton, out MethodInfo? setMethod))
            return NetherNativeActionResult.BindingUnavailable(setError);
        if (!TryResolveGeneratedCallback(registration.Controller.GetType(), confirm, out string confirmError, out object? confirmSingleton, out MethodInfo? confirmMethod))
            return NetherNativeActionResult.BindingUnavailable(confirmError);

        try
        {
            setMethod!.Invoke(singleton, new object[] { 1, registration.Controller, registration.Popup });
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

    private void RegisterFloorParentTask(object? task)
    {
        if (task == null)
            return;
        lock (_gate)
        {
            if (_floorParentAction == null)
                return;
            _floorParentTask = task;
            _floorParentTaskWait.ObserveRegistration();
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

    private static NetherNativeActionResult TryInvokeGeneratedCallback(
        object controller,
        string callbackName,
        IReadOnlyList<string> parameterTypeNames,
        object?[] arguments,
        string action
    )
    {
        NetherNativeMethodDescriptor descriptor = new(callbackName, parameterTypeNames, "System.Void");
        return TryInvokeGeneratedCallback(controller, descriptor, arguments, action);
    }

    private static NetherNativeActionResult TryInvokeGeneratedCallback(
        object controller,
        NetherNativeMethodDescriptor descriptor,
        object?[] arguments,
        string action
    )
    {
        if (!TryResolveGeneratedCallback(controller.GetType(), descriptor, out string error, out object? singleton, out MethodInfo? method))
            return NetherNativeActionResult.BindingUnavailable(error);
        try
        {
            object?[] invokeArguments = (object?[])arguments.Clone();
            for (int index = 0; index < invokeArguments.Length; index++)
            {
                if (invokeArguments[index] != null)
                    continue;
                invokeArguments[index] = CreateDefaultValue(method!.GetParameters()[index].ParameterType);
            }
            method!.Invoke(singleton, invokeArguments);
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

    private static bool TryResolveGeneratedCallback(
        Type controllerType,
        NetherNativeMethodDescriptor descriptor,
        out string error,
        out object? singleton,
        out MethodInfo? method
    )
    {
        singleton = null;
        method = null;
        Type? holder = controllerType.GetNestedType("<>c", BindingFlags.NonPublic);
        if (holder == null)
        {
            error = "binding-unavailable:" + controllerType.FullName + ":missing-generated-holder";
            return false;
        }
        FieldInfo? holderField = holder.GetField("<>9", StaticFlags);
        singleton = holderField?.GetValue(null);
        if (singleton == null)
        {
            error = "binding-unavailable:" + controllerType.FullName + ":missing-generated-singleton";
            return false;
        }
        // `<>c.<>9` is a singleton instance; its compiler-generated callback methods are
        // instance methods.  Resolving them as static produced a silent zero-match on the
        // packaged client and incorrectly made checkpoint flow unavailable.
        return TryResolveExactMethod(holder, descriptor, InstanceFlags, out error, out method);
    }

    private static bool TryResolveExactMethod(
        Type type,
        NetherNativeMethodDescriptor expected,
        BindingFlags flags,
        out string error,
        out MethodInfo? method
    )
    {
        method = null;
        MethodInfo[] candidates = type
            .GetMethods(flags)
            .Where(candidate => string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal))
            .ToArray();
        NetherNativeMethodDescriptor[] descriptors = candidates.Select(Describe).ToArray();
        NetherNativeBindingSelection selection = NetherNativeMethodBindingSelector.Select(expected, descriptors);
        if (selection.ResultKind != NetherNativeActionResultKind.Started || selection.Method == null)
        {
            error = "binding-unavailable:" + (type.FullName ?? type.Name) + ":" + expected.Name + ":" + selection.Detail;
            return false;
        }

        int selectedIndex = Array.FindIndex(descriptors, descriptor => ReferenceEquals(descriptor, selection.Method));
        if (selectedIndex < 0)
        {
            error = "binding-unavailable:" + (type.FullName ?? type.Name) + ":" + expected.Name + ":selection-lost";
            return false;
        }
        method = candidates[selectedIndex];
        error = string.Empty;
        return true;
    }

    private static MethodInfo? TryResolveExactMethod(
        Type type,
        NetherNativeMethodDescriptor expected,
        BindingFlags flags,
        out string error
    ) => TryResolveExactMethod(type, expected, flags, out error, out MethodInfo? method) ? method : null;

    private static NetherNativeMethodDescriptor Describe(MethodInfo method) => new(
        method.Name,
        method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)).ToArray(),
        TypeName(method.ReturnType)
    );

    private static string TypeName(Type type) => type.FullName ?? type.Name;

    private static object? CreateDefaultValue(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static string FormatInvocationException(string action, TargetInvocationException exception)
    {
        Exception detail = exception.InnerException ?? exception;
        return "native-" + action + "-exception:" + detail.GetType().Name + ":" + detail.Message;
    }

    private static bool TryMapCurrentFloor(
        object model,
        NetherData data,
        out long currentFloorId,
        out int floorLevel,
        out int floorIndex,
        out string error
    )
    {
        currentFloorId = 0;
        floorLevel = 0;
        floorIndex = 0;
        if (TryReadMember(model, "CurrentFloorModel", out object? current) && current != null
            && TryReadInt(current, "MNetherMapFloorId", out currentFloorId)
            && TryReadInt32(current, "FloorLevel", out floorLevel)
            && TryReadInt32(current, "FloorIndex", out floorIndex))
        {
            error = string.Empty;
            return true;
        }

        // Sleep/Clear can be observed after the map presentation has torn down.  NetherData is
        // still the server-owned fallback; it is not a locally incremented projection.
        currentFloorId = data.MNetherMapFloorId;
        floorLevel = data.FloorLevel;
        floorIndex = data.FloorIndex;
        if (currentFloorId > 0 && floorLevel > 0 && floorIndex >= 0)
        {
            error = string.Empty;
            return true;
        }

        error = "missing-current-floor";
        return false;
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

        var mapped = new List<NetherFloorNode>();
        var ids = new HashSet<long>();
        foreach (object list in EnumerateDictionaryValues(perLevel))
        {
            foreach (object floor in Enumerate(list))
            {
                if (!TryReadInt(floor, "MNetherMapFloorId", out long id)
                    || !TryReadInt32(floor, "FloorLevel", out int level)
                    || !TryReadInt32(floor, "FloorIndex", out int index)
                    || !TryReadInt32(floor, "FloorType", out int type)
                    || !TryReadBoolean(floor, "IsSecretFloor", out bool hidden)
                    || !TryReadBoolean(floor, "IsUnlocked", out bool unlocked))
                {
                    error = "missing-floor-model-member";
                    return false;
                }
                if (id <= 0 || level < 1 || index < 0 || !ids.Add(id))
                {
                    error = "invalid-or-duplicate-floor-model";
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

                mapped.Add(new NetherFloorNode(id, level, index, ToFloorNodeType(type))
                {
                    IsHidden = hidden,
                    IsUnlocked = unlocked,
                    PreviousFloorIds = previous,
                    RewardTier = 0,
                    OptionalCombatCount = type is (int)NetherFloorNodeType.Battle or (int)NetherFloorNodeType.MiniBoss ? 1 : 0,
                });
            }
        }

        if (mapped.Count == 0)
        {
            error = "empty-map-floor-model-list";
            return false;
        }
        floors = mapped;
        error = string.Empty;
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

    private static bool TryMapCodes(
        NetherDataStore dataStore,
        MasterRows rows,
        out IReadOnlyList<NetherCodeState>? codes,
        out string error
    )
    {
        codes = null;
        var mapped = new List<NetherCodeState>();
        foreach (object rawCode in Enumerate(dataStore.GetPossessionNetherCodeDataEnumerable()))
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
            (NetherCodeEffectKind kind, bool known) = MapCodeEffect(master.id, master.effect_type);
            mapped.Add(new NetherCodeState(master.id, kind, code.Amount)
            {
                IsKnown = known,
                Rarity = master.rarity,
                PartyCoverage = 0,
                IsResearchOnly = false,
            });
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
            if (part.content_id <= 0 || part.amount < 0 || part.amount > int.MaxValue)
            {
                detail = "invalid-event-content";
                return false;
            }
            NetherEffect? contentEffect = part.content_type switch
            {
                // Project.Master.ContentType.Item / NetherItem, confirmed from the packaged
                // ContentType enum.  Their actual master lookup remains in the native popup.
                30 or 31 => new NetherEffect(NetherEffectKind.Item, (int)part.amount)
                {
                    ContentId = part.content_id,
                },
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

        if (mapped.Count is < 1 or > 3)
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
        if (kind == NetherEffectKind.AbyssCodeChanged)
        {
            if (parameter <= 0)
            {
                detail = "missing-event-replacement-code";
                return false;
            }
            effect = new NetherEffect(kind, 0) { ReplacementCodeId = parameter };
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

    private static NetherRuntimePopupResult TryMapShopPopup(PopupRegistration registration)
    {
        if (!TryReadMember(registration.Controller, "_mNetherFloorShopContentsArray", out object? rawContents) || rawContents == null)
            return NetherRuntimePopupResult.Failure("missing-native-shop-content-array");

        MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
        MItems[]? itemRows = masterDataStore?.GetCache<MItems>();
        var itemById = itemRows == null
            ? new Dictionary<long, MItems>()
            : itemRows.Where(item => item != null && item.id > 0).ToDictionary(item => item.id);

        var mapped = new List<NetherShopContent>();
        foreach (object rawContent in Enumerate(rawContents))
        {
            if (rawContent is not MNetherFloorShopContents content || content.id <= 0 || content.content_id <= 0
                || content.amount is <= 0 or > int.MaxValue || content.consume_amount is < 0 or > int.MaxValue)
            {
                return NetherRuntimePopupResult.Failure("invalid-native-shop-content");
            }

            bool usesNetherGold = content.consume_content_type == 165;
            MItems? item = null;
            bool known = usesNetherGold && content.content_type is 30 or 31 && itemById.TryGetValue(content.content_id, out item);
            mapped.Add(new NetherShopContent(
                content.id,
                content.content_id,
                known ? checked((int)item!.type) : 0,
                known ? ToRewardRarity(item!.rarity) : NetherRewardRarity.NoEffect,
                checked((int)content.consume_amount),
                usesNetherGold,
                checked((int)content.amount),
                known
            ));
        }

        return NetherRuntimePopupResult.Success(new NetherRuntimePopupContext
        {
            Kind = NetherRuntimePopupKind.Shop,
            ShopContents = mapped,
        });
    }

    private static (NetherCodeEffectKind Kind, bool Known) MapCodeEffect(long codeId, int effectType)
    {
        // The two documented code IDs are authoritative.  Other ability-derived Safe/Risk/
        // Rush/Impact classifications need a fully decoded ability asset and therefore remain
        // unknown rather than being guessed from localized text or an ID range.
        if (codeId == 30024)
            return (NetherCodeEffectKind.Safe, true);
        if (codeId == 40024)
            return (NetherCodeEffectKind.Risk, true);
        return effectType switch
        {
            6 => (NetherCodeEffectKind.ErosionAdditionUp, true),
            7 => (NetherCodeEffectKind.ErosionAdditionDown, true),
            8 => (NetherCodeEffectKind.ErosionRateUp, true),
            9 => (NetherCodeEffectKind.ErosionRateDown, true),
            _ => (NetherCodeEffectKind.Unknown, false),
        };
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

    private static void LogUnknownCodeMasterAudit(IEnumerable<MNetherCodes> rows, bool detailedLogging)
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
            // A diagnostic can never make an unknown code look safe or break the fail-closed
            // decision.  The exception type is sufficient to request a focused live dump.
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
        floors.OrderBy(floor => floor.FloorId).Select(floor =>
            floor.FloorId.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.FloorLevel.ToString(CultureInfo.InvariantCulture) + ":"
            + floor.FloorIndex.ToString(CultureInfo.InvariantCulture) + ":"
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
        if (collection is IEnumerable enumerable)
        {
            foreach (object? value in enumerable)
            {
                if (value != null)
                    yield return value;
            }
            yield break;
        }

        MethodInfo? getEnumerator = collection.GetType().GetMethod(
            "GetEnumerator",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (getEnumerator == null)
            yield break;
        object? enumerator = getEnumerator.Invoke(collection, Array.Empty<object>());
        if (enumerator == null)
            yield break;
        MethodInfo? moveNext = enumerator.GetType().GetMethod(
            "MoveNext",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (moveNext == null)
            yield break;
        while (moveNext.Invoke(enumerator, Array.Empty<object>()) is bool hasNext && hasNext)
        {
            if (TryReadMember(enumerator, "Current", out object? current) && current != null)
                yield return current;
        }
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
    private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly NativePatchBinding[] PatchBindings =
    {
        new(
            FloorSelectionTypeName,
            new NetherNativeMethodDescriptor("HandleStartEventByStatusAsync", new[] { "System.Boolean" }, UniTaskTypeName),
            InstanceFlags
        ),
        new(
            FloorSelectionTypeName,
            new NetherNativeMethodDescriptor("Project.ISubService.Terminate", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            BottomRightViewTypeName,
            new NetherNativeMethodDescriptor(
                "ApplyUserSettings",
                new[] { "Project.Ingame.IIngameUserSettings" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            BottomRightViewTypeName,
            new NetherNativeMethodDescriptor("OnDestroy", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            PopupBaseTypeName,
            new NetherNativeMethodDescriptor("Close", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            PopupBaseTypeName,
            new NetherNativeMethodDescriptor("ImmediatelyClose", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            MonoBehaviourWithUniTaskTypeName,
            new NetherNativeMethodDescriptor("OnDestroy", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            EventPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherEventPopup.NetherEventPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            RecoverPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherRecoverPopup.NetherRecoverPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            TreasurePopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherTreasurePopup.NetherTreasurePopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            ShopPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherShopPopup.NetherShopPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            CodeSelectPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            CodeListPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            ReturnPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnItemSelectionPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            ContinuePopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            BoostPopupControllerTypeName,
            new NetherNativeMethodDescriptor(
                "SetupPopupEvent",
                new[] { "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopup", "System.Action" },
                "System.Void"
            ),
            InstanceFlags
        ),
        new(
            ReturnScrollControllerTypeName,
            new NetherNativeMethodDescriptor("InitializeView", Array.Empty<string>(), "System.Void"),
            InstanceFlags
        ),
        new(
            ReturnScrollControllerTypeName,
            new NetherNativeMethodDescriptor("OnThumbnailClicked", new[] { "System.Int32" }, "System.Void"),
            InstanceFlags
        ),
        new(
            NetherApiServiceTypeName,
            new NetherNativeMethodDescriptor(
                "StartQuestAsync",
                new[] { TypeName(typeof(CancellationToken)) },
                TypeName(typeof(UniTask<BattleSessionStatusResponseEntity>))
            ),
            InstanceFlags
        ),
        new(
            NetherApiServiceTypeName,
            new NetherNativeMethodDescriptor(
                "ClearQuestAsync",
                new[]
                {
                    TypeName(typeof(ExplorationBattleEndRecord)),
                    TypeName(typeof(CancellationToken)),
                    "System.Boolean",
                },
                TypeName(typeof(UniTask<IFinishQuestResponseEntity>))
            ),
            InstanceFlags
        ),
        new(
            NetherApiServiceTypeName,
            new NetherNativeMethodDescriptor(
                "CloseQuestAsync",
                new[]
                {
                    TypeName(typeof(ExplorationBattleEndRecord)),
                    TypeName(typeof(CancellationToken)),
                },
                TypeName(typeof(UniTask<IFinishQuestResponseEntity>))
            ),
            InstanceFlags
        ),
    };

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

    private readonly record struct NativePatchBinding(
        string TypeName,
        NetherNativeMethodDescriptor Method,
        BindingFlags Flags
    );

    private readonly record struct MapMaster(int MaxFloorFloorNumber);

    private sealed record MasterRows(
        MapMaster Map,
        IReadOnlyDictionary<long, MNetherCodes> CodeById,
        IReadOnlyDictionary<long, MItems> ItemById
    );
}
