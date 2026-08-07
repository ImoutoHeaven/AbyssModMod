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
            NetherAutoClimbController.OnBattleSettingsAccessorRegistered();
            NetherAutoClimbController.Toggle();
            Assert.True(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);

            // The first native Wait popup is an actual Event, not a generic code shortcut.
            bridge.CurrentSnapshot = bridge.WaitForInteractivePopup;
            bridge.ActivePopup = bridge.InteractivePopup;
            NetherAutoClimbController.Update();
            Assert.Equal(new[] { NetherActionKind.SelectEventOption }, bridge.Invocations);
            Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, NetherAutoClimbController.Phase);

            // The native action terminal is not considered server success until the exact
            // GET-only refresh applies the post-event snapshot.
            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);

            // The production RouteSafety coordinator chooses the Battle node and stores its
            // immutable projection before the native SelectFloor parent starts.
            NetherAutoClimbController.Update();
            Assert.Equal(
                new[] { NetherActionKind.SelectEventOption, NetherActionKind.SelectFloor },
                bridge.Invocations
            );
            Assert.Equal(1, bridge.BeginFloorParentCount);
            Assert.Equal(0, bridge.OwnedPopupInvokeCount);

            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(NetherSessionStatus.Battle, bridge.CurrentSnapshot.Status);

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

            // A second battle uses the same production Controller/lease lifecycle.  It must
            // acquire and restore again instead of relying on a session-global restore bit.
            bridge.CurrentSnapshot = bridge.SecondBattleOrigin;
            NetherAutoClimbController.Update();
            Assert.Equal(2, bridge.Invocations.Count(action => action == NetherActionKind.SelectFloor));
            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
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
            Assert.Equal(1, bridge.Invocations.Count(action => action == NetherActionKind.SelectEventOption));
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
            bridge.CurrentSnapshot = bridge.WaitForInteractivePopup;
            bridge.ActivePopup = bridge.InteractivePopup;
            NetherAutoClimbController.Update();
            Assert.Single(bridge.Invocations);

            NetherAutoClimbController.Toggle(); // F12 off while native Event task is pending.
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
        public List<NetherActionKind> Invocations { get; } = new();
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

        public NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors) => new()
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
        ) => NetherRuntimeInteractivePreEntryInputsResult.Failure("e2e-no-route-interactive-master-needed");

        public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates() =>
            NetherRuntimeCodeCandidatesResult.Failure("e2e-no-code-popup");

        public NetherRuntimePopupResult TryGetActivePopup() => ActivePopup == null
            ? NetherRuntimePopupResult.Failure("no-live-popup")
            : NetherRuntimePopupResult.Success(ActivePopup);

        public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent) =>
            NetherRuntimePopupResult.Failure("no-owned-popup");

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
            return NetherNativeActionResult.BindingUnavailable("unexpected-owned-popup-in-e2e");
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
                    CurrentSnapshot = BattleSnapshot;
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
            _floorParentPending = false;
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
        public int AcquireCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public NetherBattleSettingsLeasePhase Phase { get; private set; } = NetherBattleSettingsLeasePhase.Empty;
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
