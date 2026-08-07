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
internal interface INetherRuntimeBridge
{
    bool HasRegisteredFloorSelection { get; }

    bool IsBattleActive { get; }

    bool IsResultObserved { get; }

    NetherRuntimeSnapshotResult TryCaptureSnapshot();

    NetherRuntimeCodeCandidatesResult TryGetCodeCandidates();

    NetherNativeActionResult Reconcile();

    NetherNativeActionResult Invoke(NetherPlannedAction action);

    NetherNativeActionResult PollNativeFlow();

    NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items);

    bool TryConsumeBattleClear();

    bool TryConsumeBattleClose();

    bool TryConsumeResultSuccess();

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
    private const string NetherApiServiceTypeName = "Project.Ingame.Exploration.NetherAPIService";
    private const string ResultControllerTypeName = "Project.NetherTop.Result.SubViewController";

    private readonly object _gate = new();
    private object? _floorSelectionController;
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
    private bool _battleActive;
    private bool _battleClearObserved;
    private bool _battleCloseObserved;
    private bool _awaitingBoostConfirmation;
    private bool _resultObserved;
    private object? _resultTask;

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

    public static void ObserveResult() => Instance.ObserveResultCore(null);

    public static void ObserveResult(object resultTask) => Instance.ObserveResultCore(resultTask);

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

        if (typeName == NetherApiServiceTypeName)
        {
            if (methodName == "StartQuestAsync")
                ObserveBattleStart();
            else if (methodName == "ClearQuestAsync")
                ObserveBattleClear();
            else if (methodName == "CloseQuestAsync")
                ObserveBattleClose();
            return;
        }

        if (typeName == ReturnScrollControllerTypeName && methodName == "OnThumbnailClicked")
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

            NetherData data = userData.NetherDataStore.NetherData;
            MNetherCodes[]? rows = masterDataStore.GetCache<MNetherCodes>();
            if (rows == null || rows.Length == 0)
                return NetherRuntimeCodeCandidatesResult.Failure("missing-m-nether-codes-cache");
            var masterById = rows.Where(row => row != null).ToDictionary(row => row.id);

            object? selectableCodeIds = data.SelectableNetherCodeIds;
            if (selectableCodeIds == null)
                return NetherRuntimeCodeCandidatesResult.Failure("missing-selectable-nether-code-ids");

            var candidates = new List<NetherCodeCandidate>();
            foreach (object rawCodeId in Enumerate(selectableCodeIds))
            {
                if (!TryConvertInt64(rawCodeId, out long codeId) || codeId <= 0)
                    return NetherRuntimeCodeCandidatesResult.Failure("invalid-selectable-nether-code-id");
                if (!masterById.TryGetValue(codeId, out MNetherCodes? row))
                    return NetherRuntimeCodeCandidatesResult.Failure("missing-m-nether-code:" + codeId);
                (NetherCodeEffectKind kind, bool known) = MapCodeEffect(row.id, row.effect_type);
                candidates.Add(new NetherCodeCandidate(row.id, kind, LevelFromMaster(row))
                {
                    IsKnown = known,
                    Rarity = row.rarity,
                    PartyCoverage = 0,
                    IsResearchOnly = false,
                });
            }

            return new NetherRuntimeCodeCandidatesResult(candidates, true, string.Empty);
        }
        catch (Exception ex)
        {
            return NetherRuntimeCodeCandidatesResult.Failure(
                "code-candidate-map-exception:" + ex.GetType().Name + ":" + ex.Message
            );
        }
    }

    public NetherNativeActionResult Reconcile()
    {
        object? controller;
        lock (_gate)
            controller = _floorSelectionController;
        if (controller == null)
            return NetherNativeActionResult.BindingUnavailable("missing-floor-selection-controller");

        return TryInvokeExact(
            controller,
            new NetherNativeMethodDescriptor(
                "HandleStartEventByStatusAsync",
                new[] { "System.Boolean" },
                UniTaskTypeName
            ),
            new object[] { false },
            "reconcile"
        );
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
            if (_awaitingBoostConfirmation)
            {
                if (_boostPopup == null)
                    return NetherNativeActionResult.Started("awaiting-native-boost-popup");

                NetherNativeActionResult result = ConfirmBoostOneTicket(_boostPopup.Value);
                if (result.Kind == NetherNativeActionResultKind.Started)
                    _awaitingBoostConfirmation = false;
                return result;
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
            _resultObserved = true;
            return true;
        }
    }

    public void ClearRegistrations()
    {
        lock (_gate)
        {
            _floorSelectionController = null;
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
            _awaitingBoostConfirmation = false;
            _resultTask = null;
            _resultObserved = false;
            _battleActive = false;
            _battleClearObserved = false;
            _battleCloseObserved = false;
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
                _floorSelectionController = null;
        }
    }

    private void RegisterPopupCore(object controller, object popup, object? close)
    {
        if (controller == null || popup == null)
            return;

        PopupRegistration registration = new(controller, popup, close);
        string typeName = controller.GetType().FullName ?? string.Empty;
        lock (_gate)
        {
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

    private void ObserveResultCore(object? resultTask)
    {
        lock (_gate)
        {
            _resultObserved = true;
            if (resultTask != null)
                _resultTask = resultTask;
        }
    }

    private NetherNativeActionResult SelectFloor(NetherPlannedAction action)
    {
        if (action.FloorLevel < 1 || action.FloorIndex < 0)
            return NetherNativeActionResult.Rejected("invalid-floor-selection");
        object? controller;
        lock (_gate)
            controller = _floorSelectionController;
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
            "select-floor"
        );
    }

    private NetherNativeActionResult SelectEventOption(NetherPlannedAction action)
    {
        if (action.OptionNumber < 1)
            return NetherNativeActionResult.Rejected("invalid-event-option");

        List<(PopupRegistration Registration, EventFlowKind Kind)> active = new();
        lock (_gate)
        {
            if (_eventPopup != null)
                active.Add((_eventPopup.Value, EventFlowKind.Event));
            if (_recoverPopup != null)
                active.Add((_recoverPopup.Value, EventFlowKind.Recovery));
            if (_treasurePopup != null)
                active.Add((_treasurePopup.Value, EventFlowKind.Treasure));
        }
        if (active.Count != 1)
            return NetherNativeActionResult.BindingUnavailable("ambiguous-or-missing-event-popup");

        PopupRegistration registration = active[0].Registration;
        string popupType = registration.Popup.GetType().FullName ?? string.Empty;
        NetherNativeMethodDescriptor select = new(
            "OnPanelSelected",
            new[] { popupType, "System.Int32" },
            "System.Void"
        );
        NetherNativeMethodDescriptor terminal = new(
            active[0].Kind == EventFlowKind.Treasure ? "OnConfirm" : "ExecuteEvent",
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
            return NetherNativeActionResult.Started("native-event-option:" + active[0].Kind);
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
        if (registration == null || registration.Value.Close == null)
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
        if (registration == null)
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
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeListPopup;
        if (registration == null)
            return NetherNativeActionResult.BindingUnavailable("missing-code-list-popup");
        if (!TryFindCodeListSelection(registration.Value.Controller, action.CodeId, out int tabIndex, out int modelIndex, out string mappingError))
            return NetherNativeActionResult.BindingUnavailable(mappingError);

        string terminalName = action.ReplaceCodeId > 0 ? "OnClickReplace" : "OnClickChange";
        NetherNativeMethodDescriptor tab = new("OnChangeTab", new[] { "System.Int32" }, "System.Void");
        NetherNativeMethodDescriptor select = new("OnClickThumbnail", new[] { "System.Int32" }, "System.Void");
        NetherNativeMethodDescriptor terminal = new(terminalName, Array.Empty<string>(), "System.Void");
        if (!TryResolveExactMethod(registration.Value.Controller.GetType(), tab, InstanceFlags, out string tabError, out MethodInfo? tabMethod))
            return NetherNativeActionResult.BindingUnavailable(tabError);
        if (!TryResolveExactMethod(registration.Value.Controller.GetType(), select, InstanceFlags, out string selectError, out MethodInfo? selectMethod))
            return NetherNativeActionResult.BindingUnavailable(selectError);
        if (!TryResolveExactMethod(registration.Value.Controller.GetType(), terminal, InstanceFlags, out string terminalError, out MethodInfo? terminalMethod))
            return NetherNativeActionResult.BindingUnavailable(terminalError);

        try
        {
            tabMethod!.Invoke(registration.Value.Controller, new object[] { tabIndex });
            selectMethod!.Invoke(registration.Value.Controller, new object[] { modelIndex });
            terminalMethod!.Invoke(registration.Value.Controller, Array.Empty<object>());
            return NetherNativeActionResult.Started("native-code-select:" + terminalName);
        }
        catch (TargetInvocationException ex)
        {
            return NetherNativeActionResult.UnknownOutcome(FormatInvocationException("select-code", ex));
        }
        catch (Exception ex)
        {
            return NetherNativeActionResult.UnknownOutcome("select-code-exception:" + ex.GetType().Name + ":" + ex.Message);
        }
    }

    private NetherNativeActionResult ReloadCode()
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _codeSelectPopup;
        if (registration == null)
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
        PopupRegistration? registration;
        lock (_gate)
            registration = _continuePopup;
        if (registration == null)
            return NetherNativeActionResult.BindingUnavailable("missing-continue-popup");

        bool canBoost;
        if (!TryReadBoolean(registration.Value.Controller, "_canBoost", out canBoost))
            return NetherNativeActionResult.BindingUnavailable("missing-continue-can-boost-field");
        NetherNativeActionResult result = TryInvokeGeneratedCallback(
            registration.Value.Controller,
            "<SetupPopupEvent>b__8_2",
            new[] { UnitTypeName, ContinuePopupControllerTypeName },
            new object?[] { null, registration.Value.Controller },
            "continue-one-ticket"
        );
        if (result.Kind == NetherNativeActionResultKind.Started && canBoost)
        {
            lock (_gate)
                _awaitingBoostConfirmation = true;
        }
        return result;
    }

    private NetherNativeActionResult FinishAtCheckpoint()
    {
        PopupRegistration? registration;
        lock (_gate)
            registration = _continuePopup;
        if (registration == null)
            return NetherNativeActionResult.BindingUnavailable("missing-continue-popup");

        return TryInvokeGeneratedCallback(
            registration.Value.Controller,
            "<SetupPopupEvent>b__8_1",
            new[] { UnitTypeName, ContinuePopupControllerTypeName },
            new object?[] { null, registration.Value.Controller },
            "finish-at-checkpoint"
        );
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

    private NetherNativeActionResult TryInvokeExact(
        object target,
        NetherNativeMethodDescriptor descriptor,
        object[] arguments,
        string action
    )
    {
        if (!TryResolveExactMethod(target.GetType(), descriptor, InstanceFlags, out string error, out MethodInfo? method))
            return NetherNativeActionResult.BindingUnavailable(error);
        try
        {
            object? result = method!.Invoke(target, arguments);
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
        return TryResolveExactMethod(holder, descriptor, StaticFlags, out error, out method);
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
                ItemType = checked((int)master.type),
                // NetherItemData does not expose a drop-rarity field.  The return-popup
                // mapping must supply it before a positive LockReward is confirmed.
                DropRarity = NetherRewardRarity.NoEffect,
                MasterRarity = master.rarity,
            });
        }
        items = mapped;
        error = string.Empty;
        return true;
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
            + (floor.IsUnlocked ? "1" : "0")
        )
    );

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
            if (!TryReadContentItem(model, out long itemId, out int amount))
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

    private static bool TryReadContentItem(object model, out long itemId, out int amount)
    {
        itemId = 0;
        amount = 0;
        string[] itemNames = { "MItemId", "mItemId", "ItemId", "ContentId", "Id" };
        foreach (string name in itemNames)
        {
            if (TryReadInt(model, name, out itemId))
                break;
        }
        string[] amountNames = { "Amount", "amount", "Count" };
        foreach (string name in amountNames)
        {
            if (TryReadInt(model, name, out long rawAmount) && rawAmount is > 0 and <= int.MaxValue)
            {
                amount = (int)rawAmount;
                break;
            }
        }
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

    private readonly record struct PopupRegistration(object Controller, object Popup, object? Close);

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
