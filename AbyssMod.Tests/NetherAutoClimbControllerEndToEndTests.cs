#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

[CollectionDefinition("nether-controller-runtime", DisableParallelization = true)]
public sealed class NetherControllerRuntimeCollection
{
}

[Collection("nether-controller-runtime")]
public class NetherAutoClimbControllerEndToEndTests
{
    [Fact]
    public void Production_controller_drives_play_popup_battle_sleep_continue_new_segment_and_result()
    {
        var bridge = new ScriptedRuntimeBridge();
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Assert.True(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);

            // The production RouteSafety coordinator chooses the Battle node and stores its
            // immutable projection before the native SelectFloor parent starts.
            NetherAutoClimbController.Update();
            Assert.Equal(
                new[] { NetherActionKind.SelectFloor },
                bridge.Invocations
            );
            Assert.Equal(1, bridge.BeginFloorParentCount);
            Assert.Equal(0, bridge.OwnedPopupInvokeCount);

            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(NetherSessionStatus.Battle, bridge.CurrentSnapshot.Status);

            // A clean NetherTop session has no battle-only BottomRight accessor yet.  The
            // first route action must still have happened; the exact accessor appears only
            // when the first battle view exists, before automation starts that battle.
            NetherAutoClimbController.OnBattleSettingsAccessorRegistered();

            // A separate native battle clear plus a fresh read-only snapshot settles battle;
            // lease force/restore happens once for this battle, not at F12 enable time.
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.AwaitingBattle, NetherAutoClimbController.Phase);
            Assert.Equal(1, lease.AcquireCalls);
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.AwaitingBattleSettlement, NetherAutoClimbController.Phase);
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.AwaitingBattleSettlement, NetherAutoClimbController.Phase);
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, lease.RestoreCalls);
            Assert.Equal(NetherSessionStatus.Play, bridge.CurrentSnapshot.Status);

            // Destroying the cleanly restored battle-view owner must unregister it, not pause
            // map automation.  The next battle obtains a new exact owner and a new lease.
            NetherAutoClimbController.OnBattleSettingsAccessorUnregistered();
            Assert.NotEqual(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);

            // A second battle uses the same production Controller/lease lifecycle.  It must
            // acquire and restore again instead of relying on a session-global restore bit.
            bridge.CurrentSnapshot = bridge.SecondBattleOrigin;
            NetherAutoClimbController.Update();
            Assert.Equal(2, bridge.Invocations.Count(action => action == NetherActionKind.SelectFloor));
            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            NetherAutoClimbController.OnBattleSettingsAccessorRegistered();
            NetherAutoClimbController.Update();
            NetherAutoClimbController.Update();
            NetherAutoClimbController.Update();
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(2, lease.AcquireCalls);
            Assert.Equal(2, lease.RestoreCalls);

            // Sleep uses one-ticket non-boost Continue, observes the native parent, waits for
            // teardown/rebind, then performs exactly one GET-only segment reconciliation.
            bridge.CurrentSnapshot = bridge.SleepCheckpoint;
            NetherAutoClimbController.Update();
            Assert.Equal(NetherActionKind.Continue, bridge.Invocations.Last());
            Assert.Equal(1, bridge.ContinuePreflightCount);
            Assert.Equal(1, bridge.ContinueNativeInvokeCount);

            NetherAutoClimbController.Update(); // parent pending
            bridge.ContinueParentCompleted = true;
            NetherAutoClimbController.Update(); // parent terminal
            bridge.FloorOwnerTerminated = true;
            NetherAutoClimbController.Update(); // teardown
            bridge.CurrentRuntimeGeneration = 2;
            bridge.CurrentSnapshot = bridge.NewSegment;
            NetherAutoClimbController.Update(); // rebind
            NetherAutoClimbController.Update(); // GET begin
            NetherAutoClimbController.Update(); // GET terminal + Stable

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(2, bridge.CurrentSnapshot.MapId);
            Assert.Equal(20, bridge.CurrentSnapshot.CurrentFloorId);
            Assert.Equal(1, bridge.ContinueReadOnlyBeginCount);

            bridge.CurrentSnapshot = bridge.ClearResult;
            NetherAutoClimbController.Update();
            Assert.Equal(NetherAutoClimbPhase.AwaitingSceneChange, NetherAutoClimbController.Phase);
            NetherAutoClimbController.Update();

            Assert.Equal(NetherAutoClimbPhase.Completed, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.ResultPollCount);
            Assert.Equal(2, bridge.Invocations.Count(action => action == NetherActionKind.SelectFloor));
            Assert.Equal(1, bridge.Invocations.Count(action => action == NetherActionKind.Continue));
            Assert.True(bridge.FloorParentPollCount >= 2);
            Assert.True(bridge.ContinueParentPollCount >= 2);
            Assert.True(
                bridge.Trace.IndexOf("continue-preflight") < bridge.Trace.IndexOf("continue-native-invoke"),
                "native Continue must not precede authoritative carry preflight"
            );
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_blocks_fresh_route_when_persisted_lease_needs_exact_recovery()
    {
        var bridge = new ScriptedRuntimeBridge();
        var lease = new RecordingLeaseDriver(needsRecovery: true);
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Update();

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Empty(bridge.Invocations);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_pauses_raw_ordinary_code_offer_without_a_native_callback()
    {
        var bridge = new ScriptedRuntimeBridge();
        bridge.CurrentSnapshot = bridge.WaitForInteractivePopup;
        bridge.ActivePopup = new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.CodeOffer };
        bridge.CodeCandidates = new NetherRuntimeCodeCandidatesResult(
            new[]
            {
                NetherCodeRuntimeSemanticMapper.MapCandidate(
                    codeId: 51001,
                    rawCategory: (int)NetherCodeCategory.Technique,
                    effectType: 1,
                    level: 2,
                    rarity: 3
                ),
            },
            IsMasterComplete: true,
            Detail: string.Empty
        );
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Update();

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Equal(NetherPauseReason.UnknownMasterData, NetherAutoClimbController.PauseReason);
            Assert.Empty(bridge.Invocations);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_reconciles_owned_event_floor_as_one_exact_parent_transaction()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10);
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10);
        NetherSnapshot afterEvent = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 11);
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = afterEvent,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Event,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
                RawFloorType = (int)NetherFloorNodeType.Event,
                Options = new[]
                {
                    new NetherEventOption(1, new[] { new NetherEffect(NetherEffectKind.NetherGoldGain, 1) }),
                },
            },
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
        };
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();

            NetherAutoClimbController.Update(); // Play → native SelectFloor parent.
            Assert.True(
                bridge.Invocations.SequenceEqual(new[] { NetherActionKind.SelectFloor }),
                "phase=" + NetherAutoClimbController.Phase
                    + " pause=" + NetherAutoClimbController.PauseReason
                    + " invocations=" + string.Join(",", bridge.Invocations)
            );
            NetherAutoClimbController.Update(); // owned Event option; parent remains pending.
            Assert.Single(bridge.OwnedPopupActions);
            Assert.Equal(NetherActionKind.SelectEventOption, bridge.OwnedPopupActions[0].Kind);
            NetherAutoClimbController.Update(); // parent terminal → exactly one GET reconcile.
            NetherAutoClimbController.Update();
            NetherAutoClimbController.Update();

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
            Assert.Single(bridge.Invocations.Where(action => action == NetherActionKind.SelectFloor));
            Assert.Single(bridge.OwnedPopupActions);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_reconciles_owned_recovery_with_exact_heal_contract()
    {
        NetherSnapshot after = ScriptedRuntimeBridge.OwnedRouteSnapshot(
            NetherSessionStatus.Play,
            NetherFloorNodeType.Recovery,
            floorId: 2,
            gold: 10,
            hp: 520
        ) with { CharacterHpHash = "character:1:520" };

        RunOwnedFloorTransaction(
            NetherFloorNodeType.Recovery,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Recovery,
                Options = new[] { new NetherEventOption(1, new[] { new NetherEffect(NetherEffectKind.Heal, 20) }) },
            },
            new NetherFloorEventPartMasterRow(1002, 1, 20, 0, 0, 0, 0, 0, 0, 0),
            after,
            NetherActionKind.SelectEventOption
        );
    }

    [Fact]
    public void Production_controller_reconciles_owned_treasure_with_exact_key_contract()
    {
        NetherSnapshot after = ScriptedRuntimeBridge.OwnedRouteSnapshot(
            NetherSessionStatus.Play,
            NetherFloorNodeType.Treasure,
            floorId: 2,
            gold: 10,
            keys: 0
        );

        RunOwnedFloorTransaction(
            NetherFloorNodeType.Treasure,
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Treasure,
                Options = new[] { new NetherEventOption(1, new[] { new NetherEffect(NetherEffectKind.TreasureKeyUsed, 1) }) },
            },
            null,
            after,
            NetherActionKind.SelectEventOption
        );
    }

    [Fact]
    public void Production_controller_reconciles_owned_shop_leave_with_exact_parent_contract()
    {
        NetherSnapshot after = ScriptedRuntimeBridge.OwnedRouteSnapshot(
            NetherSessionStatus.Play,
            NetherFloorNodeType.Shop,
            floorId: 2,
            gold: 10
        );

        RunOwnedFloorTransaction(
            NetherFloorNodeType.Shop,
            new NetherRuntimePopupContext { Kind = NetherRuntimePopupKind.Shop },
            null,
            after,
            NetherActionKind.LeaveShop
        );
    }

    [Fact]
    public void Production_controller_reconciles_owned_shop_buy_with_exact_content_amount_and_cost()
    {
        NetherShopMode previous = AbyssMod.Config.NetherAutoClimbShopMode.Value;
        AbyssMod.Config.NetherAutoClimbShopMode.Value = NetherShopMode.EquipmentBags;
        try
        {
            NetherSnapshot after = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Play,
                NetherFloorNodeType.Shop,
                floorId: 2,
                gold: 3
            ) with
            {
                AcquiredItems = new[] { new NetherRewardItem(42, 1) },
            };
            RunOwnedFloorTransaction(
                NetherFloorNodeType.Shop,
                new NetherRuntimePopupContext
                {
                    Kind = NetherRuntimePopupKind.Shop,
                    ShopContents = new[]
                    {
                        new NetherShopContent(
                            contentId: 42,
                            itemId: 42,
                            itemType: 91,
                            rarity: NetherRewardRarity.Gold,
                            price: 7,
                            usesNetherGold: true,
                            amount: 1,
                            known: true
                        ),
                    },
                },
                null,
                after,
                NetherActionKind.BuyShopItem
            );
        }
        finally
        {
            AbyssMod.Config.NetherAutoClimbShopMode.Value = previous;
        }
    }

    [Fact]
    public void Production_controller_never_fires_a_recovered_void_event_popup_without_its_parent_task()
    {
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = new ScriptedRuntimeBridge().WaitForInteractivePopup,
            ActivePopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Event,
                OwnerAction = NetherActionKind.None,
                RawFloorType = (int)NetherFloorNodeType.Event,
                Options = new[]
                {
                    new NetherEventOption(1, new[] { new NetherEffect(NetherEffectKind.NetherGoldGain, 1) }),
                },
            },
        };
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Update();

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Equal(NetherPauseReason.BindingUnavailable, NetherAutoClimbController.PauseReason);
            Assert.Equal("direct-wait-event-parent-task-unavailable", NetherAutoClimbController.PauseDetail);
            Assert.Empty(bridge.Invocations);
            Assert.Equal(0, bridge.GetOnlyBeginCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_drains_midflight_off_without_reenable_or_duplicate_mutation()
    {
        var bridge = new ScriptedRuntimeBridge();
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.OnBattleSettingsAccessorRegistered();
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Update();
            Assert.Equal(new[] { NetherActionKind.SelectFloor }, bridge.Invocations);

            NetherAutoClimbController.Toggle(); // F12 off while native floor parent is pending.
            NetherAutoClimbController.Toggle(); // A repeat must not re-enable over evidence.
            Assert.False(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, NetherAutoClimbController.Phase);

            Pump(3);
            Assert.False(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Disabled, NetherAutoClimbController.Phase);
            Assert.Single(bridge.Invocations);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    private static void Pump(int updates)
    {
        for (int index = 0; index < updates; index++)
            NetherAutoClimbController.Update();
    }

    private static void RunOwnedFloorTransaction(
        NetherFloorNodeType kind,
        NetherRuntimePopupContext popup,
        NetherFloorEventPartMasterRow? eventPart,
        NetherSnapshot after,
        NetherActionKind expectedChild
    )
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.OwnedRouteSnapshot(
            NetherSessionStatus.Play,
            kind,
            floorId: 1,
            gold: 10
        );
        NetherSnapshot popupWait = ScriptedRuntimeBridge.OwnedRouteSnapshot(
            NetherSessionStatus.Wait,
            kind,
            floorId: 2,
            gold: 10
        );
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = after,
            OwnedPopup = popup with
            {
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) =>
                ScriptedRuntimeBridge.OwnedInteractivePreEntry(snapshot, settings, kind, eventPart),
        };
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(5);

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Single(bridge.Invocations.Where(action => action == NetherActionKind.SelectFloor));
            NetherPlannedAction child = Assert.Single(bridge.OwnedPopupActions);
            Assert.Equal(expectedChild, child.Kind);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    private sealed class ScriptedRuntimeBridge : INetherRuntimeBridge
    {
        private readonly NetherActiveCodeErosionProjection _knownCodes = new()
        {
            ErosionProjectionKnown = true,
            CodeHash = "nether-codes:none",
            ErosionEffects = Array.Empty<NetherCodeEffect>(),
        };
        private bool _eventNativePending;
        private bool _battleClearAvailable;
        private bool _floorParentPending;

        public ScriptedRuntimeBridge()
        {
            PlayBeforeInteractive = Snapshot(NetherSessionStatus.Play, mapId: 1, floorId: 1, floorLevel: 1, gold: 10, tickets: 2);
            WaitForInteractivePopup = PlayBeforeInteractive with { Status = NetherSessionStatus.Wait, MapHash = "wait-event" };
            AfterInteractive = PlayBeforeInteractive with { NetherGold = 11, MapHash = "after-event" };
            BattleSnapshot = AfterInteractive with
            {
                Status = NetherSessionStatus.Battle,
                CurrentFloorId = 2,
                FloorLevel = 2,
                FloorIndex = 2,
                MapHash = "battle-floor-2",
            };
            AfterBattle = BattleSnapshot with { Status = NetherSessionStatus.Play, MapHash = "battle-settled" };
            SecondBattleOrigin = AfterInteractive with { MapHash = "second-battle-origin" };
            SleepCheckpoint = AfterBattle with
            {
                Status = NetherSessionStatus.Sleep,
                FloorLevel = 10,
                FloorIndex = 10,
                TicketCount = 2,
                LockReward = 0,
                ContinuationTarget = new NetherContinuationTarget(2, 20, 11),
                MapHash = "sleep-checkpoint",
            };
            NewSegment = SleepCheckpoint with
            {
                Status = NetherSessionStatus.Play,
                MapId = 2,
                CurrentFloorId = 20,
                FloorLevel = 11,
                FloorIndex = 1,
                TicketCount = 1,
                ContinuationTarget = null,
                MapHash = "segment-2",
            };
            ClearResult = NewSegment with { Status = NetherSessionStatus.Clear, MapHash = "result-clear" };
            InteractivePopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Event,
                RawFloorType = (int)NetherFloorNodeType.Event,
                Options = new[]
                {
                    new NetherEventOption(1, new[] { new NetherEffect(NetherEffectKind.NetherGoldGain, 1) }),
                },
            };
            CurrentSnapshot = PlayBeforeInteractive;
        }

        public NetherSnapshot PlayBeforeInteractive { get; }
        public NetherSnapshot WaitForInteractivePopup { get; }
        public NetherSnapshot AfterInteractive { get; }
        public NetherSnapshot BattleSnapshot { get; }
        public NetherSnapshot AfterBattle { get; }
        public NetherSnapshot SecondBattleOrigin { get; }
        public NetherSnapshot SleepCheckpoint { get; }
        public NetherSnapshot NewSegment { get; }
        public NetherSnapshot ClearResult { get; }
        public NetherRuntimePopupContext InteractivePopup { get; }
        public NetherSnapshot CurrentSnapshot { get; set; }
        public NetherRuntimePopupContext? ActivePopup { get; set; }
        public NetherRuntimePopupContext? OwnedPopup { get; set; }
        public NetherSnapshot? FloorSelectionDispatchSnapshot { get; set; }
        public NetherSnapshot? OwnedPopupAfterSnapshot { get; set; }
        public NetherRuntimeRouteSafetyData? RouteSafetyOverride { get; set; }
        public Func<NetherSnapshot, NetherAutoClimbSettings, NetherRuntimeInteractivePreEntryInputsResult>? InteractivePreEntryFactory { get; set; }
        public NetherRuntimeCodeCandidatesResult CodeCandidates { get; set; } =
            NetherRuntimeCodeCandidatesResult.Failure("e2e-no-code-popup");
        public List<NetherActionKind> Invocations { get; } = new();
        public List<NetherPlannedAction> OwnedPopupActions { get; } = new();
        public int BeginFloorParentCount { get; private set; }
        public int OwnedPopupInvokeCount { get; private set; }
        public int GetOnlyBeginCount { get; private set; }
        public int GetOnlyPollCount { get; private set; }
        public int ContinuePreflightCount { get; private set; }
        public int ContinueNativeInvokeCount { get; private set; }
        public int ContinueReadOnlyBeginCount { get; private set; }
        public int ResultPollCount { get; private set; }
        public int FloorParentPollCount { get; private set; }
        public int ContinueParentPollCount { get; private set; }
        public List<string> Trace { get; } = new();
        public bool ContinueParentCompleted { get; set; }
        public bool FloorOwnerTerminated { get; set; }
        public long CurrentRuntimeGeneration { get; set; } = 1;

        public bool HasRegisteredFloorSelection => true;
        public bool IsBattleActive => CurrentSnapshot.Status == NetherSessionStatus.Battle;
        public bool IsResultObserved => CurrentSnapshot.Status == NetherSessionStatus.Clear;
        public bool IsF11Busy => false;
        public bool IsExpectedNetherTopScene => true;

        public NetherRuntimeSnapshotResult TryCaptureSnapshot() => NetherRuntimeSnapshotResult.Success(CurrentSnapshot);

        public NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors) => RouteSafetyOverride ?? new()
        {
            FloorBoundsByFloorId = new Dictionary<long, NetherFloorMasterBounds>
            {
                [2] = new NetherFloorMasterBounds(2, 0, 0, IsKnown: true, Detail: string.Empty),
                [3] = new NetherFloorMasterBounds(3, 0, 0, IsKnown: true, Detail: string.Empty),
            },
            ActivePartyHp = new NetherActivePartyHpSafety(true, 1000, string.Empty),
            ActiveCodeErosion = _knownCodes,
        };

        public NetherRuntimeInteractivePreEntryInputsResult TryCaptureInteractivePreEntryInputs(
            NetherSnapshot snapshot,
            NetherAutoClimbSettings settings
        ) => InteractivePreEntryFactory?.Invoke(snapshot, settings)
            ?? NetherRuntimeInteractivePreEntryInputsResult.Failure("e2e-no-route-interactive-master-needed");

        public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates() => CodeCandidates;

        public NetherRuntimePopupResult TryGetActivePopup() => ActivePopup == null
            ? NetherRuntimePopupResult.Failure("no-live-popup")
            : NetherRuntimePopupResult.Success(ActivePopup);

        public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent) => OwnedPopup == null
            ? NetherRuntimePopupResult.Failure("no-owned-popup")
            : NetherRuntimePopupResult.Success(OwnedPopup);

        public bool BeginFloorParent(NetherPlannedAction action, long generation)
        {
            BeginFloorParentCount++;
            Trace.Add("floor-parent-register");
            _floorParentPending = true;
            return action.Kind == NetherActionKind.SelectFloor && generation > 0;
        }

        public void TerminateFloorParent() => _floorParentPending = false;

        public NetherNativeActionResult InvokeOwnedPopup(
            NetherPlannedAction parent,
            NetherRuntimePopupContext popup,
            NetherPlannedAction action
        )
        {
            OwnedPopupInvokeCount++;
            OwnedPopupActions.Add(action);
            OwnedPopup = null;
            if (OwnedPopupAfterSnapshot != null)
                CurrentSnapshot = OwnedPopupAfterSnapshot;
            return NetherNativeActionResult.Started("native-owned-popup:" + action.Kind);
        }

        public NetherNativeActionResult Reconcile() => NetherNativeActionResult.Started("unused-direct-reconcile");

        public NetherNativeActionResult Invoke(NetherPlannedAction action)
        {
            Invocations.Add(action.Kind);
            switch (action.Kind)
            {
                case NetherActionKind.SelectEventOption:
                    // Keep the old object visible to the fake native layer.  The production
                    // Controller must follow authoritative Play state rather than replay it.
                    ActivePopup = InteractivePopup;
                    CurrentSnapshot = AfterInteractive;
                    _eventNativePending = true;
                    return NetherNativeActionResult.Started("native-event-option");
                case NetherActionKind.SelectFloor:
                    CurrentSnapshot = FloorSelectionDispatchSnapshot ?? BattleSnapshot;
                    return NetherNativeActionResult.Started("native-select-floor-parent");
                case NetherActionKind.Continue:
                    ContinueNativeInvokeCount++;
                    Trace.Add("continue-native-invoke");
                    return NetherNativeActionResult.Started("native-continue-parent");
                default:
                    return NetherNativeActionResult.BindingUnavailable("unexpected-action:" + action.Kind);
            }
        }

        public NetherNativeActionResult PollNativeFlow()
        {
            if (!_eventNativePending)
                return NetherNativeActionResult.Started("no-direct-native-terminal-yet");
            _eventNativePending = false;
            return NetherNativeActionResult.Completed("native-event-option-terminal");
        }

        public NetherNativeActionResult PollFloorParent()
        {
            FloorParentPollCount++;
            if (!_floorParentPending)
                return NetherNativeActionResult.BindingUnavailable("missing-floor-parent");
            return NetherNativeActionResult.Completed("native-floor-parent-terminal");
        }

        public NetherNativeActionResult BeginGetOnlyRefresh()
        {
            GetOnlyBeginCount++;
            if (CurrentSnapshot.Status == NetherSessionStatus.Play && CurrentSnapshot.MapId == 2)
                ContinueReadOnlyBeginCount++;
            return NetherNativeActionResult.Started("native-get-only");
        }

        public NetherNativeActionResult PollGetOnlyRefresh()
        {
            GetOnlyPollCount++;
            return NetherNativeActionResult.Completed("native-get-only-applied");
        }

        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot() =>
            NetherReadOnlySnapshotResult.Success(CurrentSnapshot);

        public NetherNativeActionResult PollBattleLifecycle()
        {
            CurrentSnapshot = AfterBattle;
            _battleClearAvailable = true;
            return NetherNativeActionResult.Completed("native-battle-clear-terminal");
        }

        public bool TryConsumeBattleClear()
        {
            bool consumed = _battleClearAvailable;
            _battleClearAvailable = false;
            return consumed;
        }

        public bool TryConsumeBattleClose() => false;

        public NetherActiveCodeErosionProjection TryCaptureActiveCodeErosionProjection() => _knownCodes;

        public bool TryBeginContinueSceneHandoff(out long ownerGeneration)
        {
            ownerGeneration = 1;
            return true;
        }

        public NetherCheckpointReturnPreflightDecision PreflightContinueReturn(NetherPlannedAction action)
        {
            ContinuePreflightCount++;
            Trace.Add("continue-preflight");
            return new NetherCheckpointReturnPreflightDecision
            {
                Kind = NetherCheckpointReturnPreflightKind.NoReturn,
                SelectionLimit = 0,
            };
        }

        public NetherNativeActionResult PollContinueParent()
        {
            ContinueParentPollCount++;
            Trace.Add("continue-parent-poll");
            return ContinueParentCompleted
                ? NetherNativeActionResult.Completed("native-continue-parent-terminal")
                : NetherNativeActionResult.Started("native-continue-parent-pending");
        }

        public NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items) =>
            NetherNativeActionResult.BindingUnavailable("no-return-expected");

        public bool TryConsumeResultSuccess() => true;

        public NetherNativeActionResult PollResultFlow()
        {
            ResultPollCount++;
            return NetherNativeActionResult.Completed("native-result-terminal");
        }

        public void ClearRegistrations()
        {
            ActivePopup = null;
            OwnedPopup = null;
            _floorParentPending = false;
        }

        internal static NetherSnapshot InteractiveRouteSnapshot(NetherSessionStatus status, long floorId, int gold) => new()
        {
            Status = status,
            NetherId = 7,
            MapId = 1,
            CurrentFloorId = floorId,
            FloorLevel = floorId == 1 ? 1 : 2,
            FloorIndex = floorId == 1 ? 1 : 2,
            MaxFloorLevel = 130,
            MasterMaxFloorLevel = 130,
            ContinuanceFloorLevel = 10,
            ErosionPoint = 20,
            TicketCount = 2,
            TreasureKeyCount = 1,
            NetherGold = gold,
            CodeReloadCount = 2,
            CodeCapacity = 3,
            Characters = new[] { new NetherCharacterState(1, 500) },
            Floors = new[]
            {
                Floor(1, 1, NetherFloorNodeType.Recovery),
                Floor(2, 2, NetherFloorNodeType.Event, new[] { 1L }),
                Floor(3, 3, NetherFloorNodeType.Boss, new[] { 2L }),
            },
            CharacterHpHash = "character:1:500",
            CodeHash = "nether-codes:none",
            MapHash = "interactive:" + status + ":" + floorId + ":" + gold,
        };

        internal static NetherRuntimeRouteSafetyData InteractiveRouteSafety() => new()
        {
            FloorBoundsByFloorId = new Dictionary<long, NetherFloorMasterBounds>
            {
                [3] = new NetherFloorMasterBounds(3, 0, 0, IsKnown: true, Detail: string.Empty),
            },
            ActivePartyHp = new NetherActivePartyHpSafety(true, 500, string.Empty),
            ActiveCodeErosion = new NetherActiveCodeErosionProjection
            {
                ErosionProjectionKnown = true,
                CodeHash = "nether-codes:none",
                ErosionEffects = Array.Empty<NetherCodeEffect>(),
            },
        };

        internal static NetherRuntimeInteractivePreEntryInputsResult InteractivePreEntry(
            NetherSnapshot snapshot,
            NetherAutoClimbSettings settings
        )
        {
            var input = new NetherInteractiveFloorPreEntrySafetyInput(
                NetherFloorNodeType.Event,
                FloorMasterId: 2,
                MapFloorRows: new[] { new NetherFloorMasterBoundsRow(2, 0, 0) },
                EventRows: new[] { new NetherFloorEventMasterRow(42, 2, 1, 1001, 0, 0, 0) },
                EventPartRows: new[]
                {
                    new NetherFloorEventPartMasterRow(
                        1001,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        // MNetherFloorEventParts.content_type: 165 maps to NetherGoldGain.
                        165,
                        1,
                        1
                    ),
                },
                CurrentErosion: snapshot.ErosionPoint,
                ActiveHpPermille: new[] { 500 },
                CurrentNetherGold: snapshot.NetherGold,
                CurrentTreasureKeys: snapshot.TreasureKeyCount,
                Settings: settings
            );
            NetherInteractiveFloorPreEntrySafetyResult safety = new NetherInteractiveFloorPreEntrySafety().Evaluate(input);
            return NetherRuntimeInteractivePreEntryInputsResult.Success(
                new Dictionary<long, NetherRuntimeInteractivePreEntryCaptureResult>
                {
                    [2] = new NetherRuntimeInteractivePreEntryCaptureResult
                    {
                        IsCaptured = true,
                        Input = input,
                        Safety = safety,
                    },
                }
            );
        }

        internal static NetherSnapshot OwnedRouteSnapshot(
            NetherSessionStatus status,
            NetherFloorNodeType targetKind,
            long floorId,
            int gold,
            int keys = 1,
            int hp = 500
        ) => new()
        {
            Status = status,
            NetherId = 7,
            MapId = 1,
            CurrentFloorId = floorId,
            FloorLevel = floorId == 1 ? 1 : 2,
            FloorIndex = floorId == 1 ? 1 : 2,
            MaxFloorLevel = 130,
            MasterMaxFloorLevel = 130,
            ContinuanceFloorLevel = 10,
            ErosionPoint = 20,
            TicketCount = 2,
            TreasureKeyCount = keys,
            NetherGold = gold,
            CodeReloadCount = 2,
            CodeCapacity = 3,
            Characters = new[] { new NetherCharacterState(1, hp) },
            Floors = new[]
            {
                Floor(1, 1, NetherFloorNodeType.Recovery),
                Floor(2, 2, targetKind, new[] { 1L }),
                Floor(3, 3, NetherFloorNodeType.Boss, new[] { 2L }),
            },
            CharacterHpHash = "character:1:" + hp,
            CodeHash = "nether-codes:none",
            MapHash = "owned:" + targetKind + ":" + status + ":" + floorId + ":" + gold + ":" + keys + ":" + hp,
        };

        internal static NetherRuntimeInteractivePreEntryInputsResult OwnedInteractivePreEntry(
            NetherSnapshot snapshot,
            NetherAutoClimbSettings settings,
            NetherFloorNodeType kind,
            NetherFloorEventPartMasterRow? eventPart = null
        )
        {
            IReadOnlyList<NetherFloorEventMasterRow>? eventRows = null;
            IReadOnlyList<NetherFloorEventPartMasterRow>? parts = null;
            if (kind is NetherFloorNodeType.Event or NetherFloorNodeType.Recovery)
            {
                if (eventPart is not NetherFloorEventPartMasterRow part)
                    return NetherRuntimeInteractivePreEntryInputsResult.Failure("missing-e2e-event-part");
                eventRows = new[] { new NetherFloorEventMasterRow(42, 2, 1, part.PartId, 0, 0, 0) };
                parts = new[] { part };
            }

            var input = new NetherInteractiveFloorPreEntrySafetyInput(
                kind,
                FloorMasterId: 2,
                MapFloorRows: new[] { new NetherFloorMasterBoundsRow(2, 0, 0) },
                EventRows: eventRows,
                EventPartRows: parts,
                CurrentErosion: snapshot.ErosionPoint,
                ActiveHpPermille: snapshot.Characters.Where(character => character.IsActive).Select(character => character.HpPermille).ToArray(),
                CurrentNetherGold: snapshot.NetherGold,
                CurrentTreasureKeys: snapshot.TreasureKeyCount,
                Settings: settings
            )
            {
                CanCloseShop = kind == NetherFloorNodeType.Shop,
            };
            NetherInteractiveFloorPreEntrySafetyResult safety = new NetherInteractiveFloorPreEntrySafety().Evaluate(input);
            return NetherRuntimeInteractivePreEntryInputsResult.Success(
                new Dictionary<long, NetherRuntimeInteractivePreEntryCaptureResult>
                {
                    [2] = new NetherRuntimeInteractivePreEntryCaptureResult
                    {
                        IsCaptured = true,
                        Input = input,
                        Safety = safety,
                    },
                }
            );
        }

        private static NetherSnapshot Snapshot(
            NetherSessionStatus status,
            long mapId,
            long floorId,
            int floorLevel,
            int gold,
            int tickets
        ) => new()
        {
            Status = status,
            NetherId = 1,
            MapId = mapId,
            CurrentFloorId = floorId,
            FloorLevel = floorLevel,
            FloorIndex = floorLevel,
            MaxFloorLevel = 130,
            MasterMaxFloorLevel = 130,
            ContinuanceFloorLevel = 10,
            ErosionPoint = 10,
            TicketCount = tickets,
            TreasureKeyCount = 1,
            NetherGold = gold,
            CodeReloadCount = 2,
            CodeCapacity = 3,
            LockReward = 0,
            Characters = new[] { new NetherCharacterState(1, 1000) },
            Codes = Array.Empty<NetherCodeState>(),
            Floors = new[]
            {
                Floor(1, 1, NetherFloorNodeType.Recovery),
                Floor(2, 2, NetherFloorNodeType.Battle, new[] { 1L }),
                Floor(3, 3, NetherFloorNodeType.Boss, new[] { 2L }),
            },
            CharacterHpHash = "character:1:1000",
            CodeHash = "nether-codes:none",
            MapHash = "map:" + mapId + ":" + floorId + ":" + floorLevel + ":" + status + ":" + gold + ":" + tickets,
        };

        private static NetherFloorNode Floor(
            long id,
            int level,
            NetherFloorNodeType type,
            IReadOnlyList<long>? previous = null
        ) => new(id, level, level, type)
        {
            IsUnlocked = true,
            PreviousFloorIds = previous ?? Array.Empty<long>(),
        };
    }

    private sealed class RecordingLeaseDriver : INetherBattleSettingsLeaseDriver
    {
        public RecordingLeaseDriver(bool needsRecovery = false)
        {
            NeedsRecovery = needsRecovery;
            Phase = needsRecovery
                ? NetherBattleSettingsLeasePhase.RestorePending
                : NetherBattleSettingsLeasePhase.Empty;
        }

        public int AcquireCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public NetherBattleSettingsLeasePhase Phase { get; private set; }
        public bool NeedsRecovery { get; private set; }

        public NetherNativeActionResult AcquireAndForce()
        {
            AcquireCalls++;
            Phase = NetherBattleSettingsLeasePhase.Forced;
            return NetherNativeActionResult.Completed("e2e-force");
        }

        public NetherNativeActionResult Restore(string reason)
        {
            RestoreCalls++;
            Phase = NetherBattleSettingsLeasePhase.Restored;
            NeedsRecovery = false;
            return NetherNativeActionResult.Completed("e2e-restore:" + reason);
        }

        public NetherNativeActionResult RecoverOnLoad()
        {
            Phase = NetherBattleSettingsLeasePhase.Restored;
            NeedsRecovery = false;
            return NetherNativeActionResult.Completed("e2e-startup-recovery");
        }

        public NetherNativeActionResult RetryRestoreAfterNativeAccessorRegistered() =>
            NetherNativeActionResult.Completed("e2e-no-retry-needed");
    }
}
