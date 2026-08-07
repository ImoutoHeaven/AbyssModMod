#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public void Production_controller_probes_real_persisted_lease_before_accessor_then_releases_route_after_restore()
    {
        using var leaseHarness = new StartupLeaseHarness();
        Assert.Equal(NetherNativeActionResultKind.Completed, leaseHarness.OriginalLease.AcquireAndForce().Kind);
        Assert.True(File.Exists(leaseHarness.LeaseFilePath));

        var recoveryNative = new StartupLeaseNative(autoEnabled: true, speed: 3);
        NetherBattleSettingsLease recoveredLease = leaseHarness.CreateDetachedLease();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(recoveredLease, retryIntervalUpdates: 1);
        var bridge = new ScriptedRuntimeBridge();
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Update();

            Assert.True(lifecycle.BlocksRoute);
            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Empty(bridge.Invocations);
            Assert.Equal(0, recoveryNative.WriteCalls);

            leaseHarness.Attach(recoveredLease, recoveryNative);
            NetherAutoClimbController.OnBattleSettingsAccessorRegistered();

            Assert.False(lifecycle.BlocksRoute);
            Assert.False(recoveryNative.AutoEnabled);
            Assert.Equal(1, recoveryNative.Speed);
            Assert.Equal(1, recoveryNative.WriteCalls);
            Assert.False(File.Exists(leaseHarness.LeaseFilePath));

            NetherAutoClimbController.Toggle(); // off from paused enabled state
            NetherAutoClimbController.Toggle(); // user explicitly re-enables after recovery
            NetherAutoClimbController.Update();

            Assert.Single(bridge.Invocations.Where(action => action == NetherActionKind.SelectFloor));
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
    public void Production_controller_appends_event_then_code_popup_under_one_parent_and_one_get()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10);
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10);
        NetherSnapshot afterEvent = popupWait with { NetherGold = 15, MapHash = "event-code-wait" };
        NetherSnapshot afterCode = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 15) with
        {
            Codes = new[]
            {
                NetherCodeRuntimeSemanticMapper.MapState(
                    codeId: 30024,
                    rawCategory: (int)NetherCodeCategory.ErosionResistance,
                    effectType: 1,
                    level: 1,
                    rarity: 1
                ),
            },
            CodeHash = "code:30024",
            MapHash = "event-code-play",
        };
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
                    new NetherEventOption(1, new NetherEffect[]
                    {
                        new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                        new NetherEffect(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
                    }),
                },
            },
            CodeCandidates = new NetherRuntimeCodeCandidatesResult(
                new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapCandidate(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
                IsMasterComplete: true,
                Detail: string.Empty
            ),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
        };
        bridge.EnqueueOwnedPopup(
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 2,
            },
            afterCode
        );
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();

            NetherAutoClimbController.Update(); // SelectFloor parent.
            NetherAutoClimbController.Update(); // Event child, then its CodeOffer is live.
            Assert.Equal(new[] { NetherActionKind.SelectEventOption }, bridge.OwnedPopupActions.Select(action => action.Kind));
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            NetherAutoClimbController.Update(); // Code child; still the original parent.
            Assert.Equal(
                new[] { NetherActionKind.SelectEventOption, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(1, bridge.Invocations.Count(action => action == NetherActionKind.SelectFloor));
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            NetherAutoClimbController.Update(); // Only now may the native parent terminal.
            Assert.Equal(0, bridge.GetOnlyBeginCount);
            NetherAutoClimbController.Update(); // one GET begin
            NetherAutoClimbController.Update(); // one GET terminal

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
            Assert.Equal(2, bridge.OwnedPopupInvokeCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_rerolls_same_live_code_offer_then_redispatches_once_before_parent_get()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:before-reload" };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:before-reload" };
        NetherSnapshot afterReload = popupWait with
        {
            CodeReloadCount = 1,
            CodeHash = "codes:after-reload-offer",
            MapHash = "code-reload-wait",
        };
        NetherSnapshot afterSelect = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 10)
            with
            {
                CodeReloadCount = 1,
                CodeCapacity = 3,
                CodeHash = "codes:30024",
                Codes = new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapState(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
            };
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = afterSelect,
            CodeReloadAfterSnapshot = afterReload,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            CodeCandidates = new NetherRuntimeCodeCandidatesResult(
                new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapCandidate(
                        codeId: 40024,
                        rawCategory: (int)NetherCodeCategory.ErosionEnhancement,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
                IsMasterComplete: true,
                Detail: string.Empty
            ),
            ReloadCodeCandidates = new NetherRuntimeCodeCandidatesResult(
                new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapCandidate(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
                IsMasterComplete: true,
                Detail: string.Empty
            ),
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
            Pump(8);

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_rerolls_once_then_keeps_same_owned_offer_before_parent_get()
    {
        ScriptedRuntimeBridge bridge = CreateOneReloadKeepBridge();
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(6); // SelectFloor -> Reload e0 -> fresh e1 -> Keep -> parent pending.

            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.KeepCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3);
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_keeps_an_owned_offer_at_reload_reserve_only_after_cancel_task_and_parent_terminal()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        NetherSnapshot afterKeep = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 10)
            with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = afterKeep,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            CodeCandidates = RiskCodeCandidates(40024),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
            RequireExplicitFloorParentTerminal = true,
        };
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();

            NetherAutoClimbController.Update(); // Play → original SelectFloor parent.
            NetherAutoClimbController.Update(); // exact generated cancel callback starts.
            Assert.Equal(new[] { NetherActionKind.KeepCode }, bridge.OwnedPopupActions.Select(action => action.Kind));
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            NetherAutoClimbController.Update(); // cancel task terminal, but parent is still pending.
            Assert.Equal(0, bridge.FloorParentTerminalCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3); // parent terminal → one GET → Stable.

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_executes_two_owned_reload_epochs_then_one_select_before_parent_get()
    {
        ScriptedRuntimeBridge bridge = CreateTwoEpochReloadBridge();
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(9); // floor -> reload e0 -> refresh e1 -> reload e1 -> refresh e2 -> select -> parent pending.

            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.ReloadCode, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3); // parent terminal -> exactly one GET -> Stable.

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_executes_two_owned_reload_epochs_then_one_keep_before_parent_get()
    {
        ScriptedRuntimeBridge bridge = CreateTwoEpochReloadBridge(finalKeep: true);
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(9); // floor -> Reload e0/e1 -> exact Keep child task -> original parent still pending.

            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.ReloadCode, NetherActionKind.KeepCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3);

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_drains_owned_keep_after_off_without_replaying_cancel()
    {
        ScriptedRuntimeBridge bridge = CreateReserveKeepBridge();
        bridge.CodeKeepTaskPollResult = NetherNativeActionResult.Started("scripted-code-keep-pending");
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(3); // SelectFloor -> Keep -> pending generated cancel task.
            Assert.Equal(1, bridge.CodeKeepInvokeCount);

            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Toggle(); // no re-enable over pending owner evidence.
            Assert.False(NetherAutoClimbController.IsEnabled);

            bridge.CodeKeepTaskPollResult = NetherNativeActionResult.Completed("scripted-code-keep-terminal");
            Pump(3); // child -> original parent remains pending.
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3);
            Assert.False(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Disabled, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_pauses_keep_task_fault_without_replaying_cancel_or_starting_get()
    {
        ScriptedRuntimeBridge bridge = CreateReserveKeepBridge();
        bridge.CodeKeepTaskPollResult = NetherNativeActionResult.UnknownOutcome("scripted-code-keep-fault");
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(3); // SelectFloor -> Keep -> task fault.

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Equal(NetherPauseReason.BindingUnavailable, NetherAutoClimbController.PauseReason);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            Pump(2);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_pauses_keep_task_timeout_without_replaying_cancel_or_starting_get()
    {
        ScriptedRuntimeBridge bridge = CreateReserveKeepBridge();
        bridge.CodeKeepTaskPollResult = NetherNativeActionResult.Started("scripted-code-keep-never-terminal");
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(605); // exact coordinator's bounded 600-pump task wait expires.

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Equal(NetherPauseReason.BindingUnavailable, NetherAutoClimbController.PauseReason);
            Assert.Equal(1, bridge.CodeKeepInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_drains_two_epoch_reload_when_disabled_between_epochs_without_replay()
    {
        ScriptedRuntimeBridge bridge = CreateTwoEpochReloadBridge();
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(4); // first RerollAsync is terminal and the changed epoch-1 offer is live.
            Assert.Equal(1, bridge.CodeReloadInvokeCount);

            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Toggle(); // off→on repeat cannot replace the pending parent.
            Assert.False(NetherAutoClimbController.IsEnabled);

            Pump(5); // second reload, fresh epoch-2 select, then explicit parent remains pending.
            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.ReloadCode, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3);

            Assert.False(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Disabled, NetherAutoClimbController.Phase);
            Assert.Equal(2, bridge.CodeReloadInvokeCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_composes_event_code_battle_and_resource_effects_until_parent_battle_terminal()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10);
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10);
        NetherSnapshot afterEvent = popupWait with { NetherGold = 15, MapHash = "event-code-battle-wait" };
        NetherSnapshot afterCode = ScriptedRuntimeBridge.InteractiveRouteSnapshot(
            NetherSessionStatus.Battle,
            floorId: 2,
            gold: 15
        ) with
        {
            Codes = new[]
            {
                NetherCodeRuntimeSemanticMapper.MapState(
                    codeId: 30024,
                    rawCategory: (int)NetherCodeCategory.ErosionResistance,
                    effectType: 1,
                    level: 1,
                    rarity: 1
                ),
            },
            CodeHash = "code:30024",
            MapHash = "event-code-battle-terminal",
        };
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
                    new NetherEventOption(1, new NetherEffect[]
                    {
                        new(NetherEffectKind.NetherGoldGain, 5),
                        new(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
                        new(NetherEffectKind.Battle, 0),
                    }),
                },
            },
            CodeCandidates = SafeCodeCandidates(30024),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
            RequireExplicitFloorParentTerminal = true,
        };
        bridge.EnqueueOwnedPopup(
            new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 2,
            },
            afterCode
        );
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(4); // SelectFloor -> Event -> Code -> original parent remains pending.

            Assert.Equal(
                new[] { NetherActionKind.SelectEventOption, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(0, bridge.GetOnlyBeginCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);

            bridge.FloorParentCompleted = true;
            Pump(3); // final Battle parent terminal -> exactly one authority GET -> stable Battle snapshot.

            // Reconcile establishes the authoritative Battle state; the next frame is the
            // separate battle-lifecycle boundary.  The composition contract must not infer
            // either a Play terminal or a second child mutation while doing so.
            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Equal(NetherSessionStatus.Battle, bridge.CurrentSnapshot.Status);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    [Fact]
    public void Production_controller_pauses_before_get_when_parent_terminal_lacks_required_code_stage()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10);
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10);
        NetherSnapshot prematureParentTerminal = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 15)
            with { MapHash = "event-code-missing-child" };
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = prematureParentTerminal,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.Event,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
                RawFloorType = (int)NetherFloorNodeType.Event,
                Options = new[]
                {
                    new NetherEventOption(1, new NetherEffect[]
                    {
                        new NetherEffect(NetherEffectKind.NetherGoldGain, 5),
                        new NetherEffect(NetherEffectKind.AbyssCodeChanged, 0) { ReplacementCodeId = 30024 },
                    }),
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
            NetherAutoClimbController.Update(); // SelectFloor
            NetherAutoClimbController.Update(); // Event child
            NetherAutoClimbController.Update(); // premature parent terminal

            Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
            Assert.Equal(NetherPauseReason.BindingUnavailable, NetherAutoClimbController.PauseReason);
            Assert.Equal("floor-parent-incomplete-owned-popup-stage", NetherAutoClimbController.PauseDetail);
            Assert.Equal(0, bridge.GetOnlyBeginCount);
            Assert.Equal(0, bridge.GetOnlyPollCount);
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
            ScriptedRuntimeBridge bridge = RunOwnedFloorTransaction(
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

            // The production coordinator holds the original floor parent across the
            // purchase child and invokes the exact SetupPopupEvent close once.  A second
            // frame after stable must not replay either mutation.
            Assert.Equal(1, bridge.ShopCloseInvokeCount);
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.OwnedPopupActions.Count(action => action.Kind == NetherActionKind.BuyShopItem));
        }
        finally
        {
            AbyssMod.Config.NetherAutoClimbShopMode.Value = previous;
        }
    }

    [Fact]
    public void Production_controller_pauses_shop_buy_without_close_or_get_when_purchase_child_faults()
    {
        NetherShopMode previous = AbyssMod.Config.NetherAutoClimbShopMode.Value;
        AbyssMod.Config.NetherAutoClimbShopMode.Value = NetherShopMode.EquipmentBags;
        try
        {
            NetherSnapshot routeStart = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Play,
                NetherFloorNodeType.Shop,
                floorId: 1,
                gold: 10
            );
            NetherSnapshot popupWait = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Wait,
                NetherFloorNodeType.Shop,
                floorId: 2,
                gold: 10
            );
            var bridge = new ScriptedRuntimeBridge
            {
                CurrentSnapshot = routeStart,
                FloorSelectionDispatchSnapshot = popupWait,
                OwnedPopup = new NetherRuntimePopupContext
                {
                    Kind = NetherRuntimePopupKind.Shop,
                    OwnerAction = NetherActionKind.SelectFloor,
                    OwnerGeneration = 1,
                    Sequence = 1,
                    ShopContents = new[]
                    {
                        new NetherShopContent(42, 42, 91, NetherRewardRarity.Gold, 7, true, 1, true),
                    },
                },
                RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
                InteractivePreEntryFactory = (snapshot, settings) =>
                    ScriptedRuntimeBridge.OwnedInteractivePreEntry(snapshot, settings, NetherFloorNodeType.Shop, null),
                ShopPurchaseChildPollResult = NetherNativeActionResult.UnknownOutcome("scripted-buy-child-fault"),
            };
            var lease = new RecordingLeaseDriver();
            var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
            using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

            try
            {
                NetherAutoClimbController.Initialize();
                NetherAutoClimbController.Toggle();
                Pump(3); // SelectFloor -> Buy child -> child terminal fault.

                Assert.Equal(NetherAutoClimbPhase.Paused, NetherAutoClimbController.Phase);
                Assert.Equal(0, bridge.ShopCloseInvokeCount);
                Assert.Equal(0, bridge.GetOnlyBeginCount);
                Assert.Single(bridge.OwnedPopupActions.Where(action => action.Kind == NetherActionKind.BuyShopItem));
            }
            finally
            {
                NetherAutoClimbController.OnPluginUnload();
            }
        }
        finally
        {
            AbyssMod.Config.NetherAutoClimbShopMode.Value = previous;
        }
    }

    [Fact]
    public void Production_controller_drains_owned_shop_buy_after_off_without_replaying_buy_or_close()
    {
        NetherShopMode previous = AbyssMod.Config.NetherAutoClimbShopMode.Value;
        AbyssMod.Config.NetherAutoClimbShopMode.Value = NetherShopMode.EquipmentBags;
        try
        {
            NetherSnapshot routeStart = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Play,
                NetherFloorNodeType.Shop,
                floorId: 1,
                gold: 10
            );
            NetherSnapshot popupWait = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Wait,
                NetherFloorNodeType.Shop,
                floorId: 2,
                gold: 10
            );
            NetherSnapshot afterPurchase = ScriptedRuntimeBridge.OwnedRouteSnapshot(
                NetherSessionStatus.Play,
                NetherFloorNodeType.Shop,
                floorId: 2,
                gold: 3
            ) with { AcquiredItems = new[] { new NetherRewardItem(42, 1) } };
            var bridge = new ScriptedRuntimeBridge
            {
                CurrentSnapshot = routeStart,
                FloorSelectionDispatchSnapshot = popupWait,
                OwnedPopupAfterSnapshot = afterPurchase,
                OwnedPopup = new NetherRuntimePopupContext
                {
                    Kind = NetherRuntimePopupKind.Shop,
                    OwnerAction = NetherActionKind.SelectFloor,
                    OwnerGeneration = 1,
                    Sequence = 1,
                    ShopContents = new[]
                    {
                        new NetherShopContent(42, 42, 91, NetherRewardRarity.Gold, 7, true, 1, true),
                    },
                },
                RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
                InteractivePreEntryFactory = (snapshot, settings) =>
                    ScriptedRuntimeBridge.OwnedInteractivePreEntry(snapshot, settings, NetherFloorNodeType.Shop, null),
                ShopPurchaseChildPollResult = NetherNativeActionResult.Started("scripted-shop-purchase-pending"),
                RequireExplicitFloorParentTerminal = true,
            };
            var lease = new RecordingLeaseDriver();
            var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
            using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

            try
            {
                NetherAutoClimbController.Initialize();
                NetherAutoClimbController.Toggle();
                Pump(3); // SelectFloor -> Buy child -> observed child pending.

                Assert.Single(bridge.OwnedPopupActions.Where(action => action.Kind == NetherActionKind.BuyShopItem));
                Assert.Equal(0, bridge.ShopCloseInvokeCount);

                NetherAutoClimbController.Toggle(); // off: preserve the already-sent Buy.
                NetherAutoClimbController.Toggle(); // off->on repeat must be ignored while draining.
                Assert.False(NetherAutoClimbController.IsEnabled);

                bridge.ShopPurchaseChildPollResult = NetherNativeActionResult.Completed("scripted-shop-purchase-terminal");
                Pump(3); // child -> exact close -> original parent remains genuinely pending.

                Assert.Equal(1, bridge.ShopCloseInvokeCount);
                Assert.Equal(0, bridge.FloorParentTerminalCount);
                Assert.Equal(0, bridge.GetOnlyBeginCount);

                bridge.FloorParentCompleted = true;
                Pump(3); // original parent -> one GET -> Disabled.

                Assert.False(NetherAutoClimbController.IsEnabled);
                Assert.Equal(NetherAutoClimbPhase.Disabled, NetherAutoClimbController.Phase);
                Assert.Single(bridge.OwnedPopupActions.Where(action => action.Kind == NetherActionKind.BuyShopItem));
                Assert.Equal(1, bridge.ShopCloseInvokeCount);
                Assert.Equal(1, bridge.FloorParentTerminalCount);
                Assert.Equal(1, bridge.GetOnlyBeginCount);
                Assert.Equal(1, bridge.GetOnlyPollCount);
            }
            finally
            {
                NetherAutoClimbController.OnPluginUnload();
            }
        }
        finally
        {
            AbyssMod.Config.NetherAutoClimbShopMode.Value = previous;
        }
    }

    [Fact]
    public void Production_controller_drains_owned_code_reload_after_off_without_a_second_reload()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:before-off-reload" };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:before-off-reload" };
        NetherSnapshot afterReload = popupWait with
        {
            CodeReloadCount = 1,
            CodeHash = "codes:after-off-reload-offer",
            MapHash = "code-reload-off-wait",
        };
        NetherSnapshot afterSelect = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 10)
            with
            {
                CodeReloadCount = 1,
                CodeCapacity = 3,
                CodeHash = "codes:30024",
                Codes = new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapState(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
            };
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = afterSelect,
            CodeReloadAfterSnapshot = afterReload,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            CodeCandidates = new NetherRuntimeCodeCandidatesResult(
                new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapCandidate(
                        codeId: 40024,
                        rawCategory: (int)NetherCodeCategory.ErosionEnhancement,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
                IsMasterComplete: true,
                Detail: string.Empty
            ),
            ReloadCodeCandidates = new NetherRuntimeCodeCandidatesResult(
                new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapCandidate(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
                IsMasterComplete: true,
                Detail: string.Empty
            ),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
            CodeReloadTaskPollResult = NetherNativeActionResult.Started("scripted-reroll-pending"),
            RequireExplicitFloorParentTerminal = true,
        };
        var lease = new RecordingLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);
        using IDisposable scope = NetherAutoClimbController.PushRuntimeBridgeForTests(bridge, lifecycle);

        try
        {
            NetherAutoClimbController.Initialize();
            NetherAutoClimbController.Toggle();
            Pump(3); // SelectFloor -> RerollAsync -> observed child pending.

            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            NetherAutoClimbController.Toggle();
            NetherAutoClimbController.Toggle();
            Assert.False(NetherAutoClimbController.IsEnabled);

            bridge.CodeReloadTaskPollResult = NetherNativeActionResult.Completed("scripted-reroll-terminal");
            Pump(4); // refresh epoch -> Select -> original parent remains genuinely pending.

            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            Assert.Equal(0, bridge.FloorParentTerminalCount);
            Assert.Equal(0, bridge.GetOnlyBeginCount);

            bridge.FloorParentCompleted = true;
            Pump(3); // original parent -> one GET -> Disabled.

            Assert.False(NetherAutoClimbController.IsEnabled);
            Assert.Equal(NetherAutoClimbPhase.Disabled, NetherAutoClimbController.Phase);
            Assert.Equal(1, bridge.CodeReloadInvokeCount);
            Assert.Equal(
                new[] { NetherActionKind.ReloadCode, NetherActionKind.SelectCode },
                bridge.OwnedPopupActions.Select(action => action.Kind)
            );
            Assert.Equal(1, bridge.FloorParentTerminalCount);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
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

    private static ScriptedRuntimeBridge CreateTwoEpochReloadBridge(bool finalKeep = false)
    {
        string initialCodeHash = finalKeep ? "codes:none" : "codes:reload-e0";
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 3, CodeCapacity = 3, CodeHash = initialCodeHash };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 3, CodeCapacity = 3, CodeHash = initialCodeHash };
        NetherSnapshot afterFirstReload = popupWait with
        {
            CodeReloadCount = 2,
            CodeHash = finalKeep ? initialCodeHash : "codes:reload-e1",
            MapHash = "code-reload-e1-wait",
        };
        NetherSnapshot afterSecondReload = popupWait with
        {
            CodeReloadCount = 1,
            CodeHash = finalKeep ? initialCodeHash : "codes:reload-e2",
            MapHash = "code-reload-e2-wait",
        };
        NetherSnapshot afterSelect = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 10)
            with
            {
                CodeReloadCount = 1,
                CodeCapacity = 3,
                CodeHash = "codes:30024",
                Codes = new[]
                {
                    NetherCodeRuntimeSemanticMapper.MapState(
                        codeId: 30024,
                        rawCategory: (int)NetherCodeCategory.ErosionResistance,
                        effectType: 1,
                        level: 1,
                        rarity: 1
                    ),
                },
            };
        var bridge = new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = afterSelect,
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            CodeCandidates = RiskCodeCandidates(40024),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
            RequireExplicitFloorParentTerminal = true,
        };
        if (finalKeep)
        {
            bridge.OwnedPopupAfterSnapshot = ScriptedRuntimeBridge.InteractiveRouteSnapshot(
                NetherSessionStatus.Play,
                floorId: 2,
                gold: 10
            ) with
            {
                CodeReloadCount = 1,
                CodeCapacity = 3,
                CodeHash = initialCodeHash,
                Codes = Array.Empty<NetherCodeState>(),
            };
        }
        bridge.EnqueueCodeReloadRefresh(afterFirstReload, RiskCodeCandidates(40025));
        bridge.EnqueueCodeReloadRefresh(afterSecondReload, finalKeep ? RiskCodeCandidates(40026) : SafeCodeCandidates(30024));
        return bridge;
    }

    private static ScriptedRuntimeBridge CreateReserveKeepBridge()
    {
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        return new ScriptedRuntimeBridge
        {
            CurrentSnapshot = routeStart,
            FloorSelectionDispatchSnapshot = popupWait,
            OwnedPopupAfterSnapshot = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 2, gold: 10)
                with { CodeReloadCount = 1, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() },
            OwnedPopup = new NetherRuntimePopupContext
            {
                Kind = NetherRuntimePopupKind.CodeOffer,
                OwnerAction = NetherActionKind.SelectFloor,
                OwnerGeneration = 1,
                Sequence = 1,
            },
            CodeCandidates = RiskCodeCandidates(40024),
            RouteSafetyOverride = ScriptedRuntimeBridge.InteractiveRouteSafety(),
            InteractivePreEntryFactory = (snapshot, settings) => ScriptedRuntimeBridge.InteractivePreEntry(snapshot, settings),
            RequireExplicitFloorParentTerminal = true,
        };
    }

    private static ScriptedRuntimeBridge CreateOneReloadKeepBridge()
    {
        ScriptedRuntimeBridge bridge = CreateReserveKeepBridge();
        NetherSnapshot routeStart = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Play, floorId: 1, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        NetherSnapshot popupWait = ScriptedRuntimeBridge.InteractiveRouteSnapshot(NetherSessionStatus.Wait, floorId: 2, gold: 10)
            with { CodeReloadCount = 2, CodeCapacity = 3, CodeHash = "codes:none", Codes = Array.Empty<NetherCodeState>() };
        bridge.CurrentSnapshot = routeStart;
        bridge.FloorSelectionDispatchSnapshot = popupWait;
        bridge.CodeReloadAfterSnapshot = popupWait with
        {
            CodeReloadCount = 1,
            CodeHash = "codes:none",
            MapHash = "code-reload-keep-e1-wait",
        };
        bridge.CodeCandidates = RiskCodeCandidates(40024);
        bridge.ReloadCodeCandidates = RiskCodeCandidates(40025);
        return bridge;
    }

    private static NetherRuntimeCodeCandidatesResult SafeCodeCandidates(long codeId) => new(
        new[]
        {
            NetherCodeRuntimeSemanticMapper.MapCandidate(
                codeId,
                (int)NetherCodeCategory.ErosionResistance,
                effectType: 1,
                level: 1,
                rarity: 1
            ),
        },
        IsMasterComplete: true,
        Detail: string.Empty
    );

    private static NetherRuntimeCodeCandidatesResult RiskCodeCandidates(long codeId) => new(
        new[]
        {
            NetherCodeRuntimeSemanticMapper.MapCandidate(
                codeId,
                (int)NetherCodeCategory.ErosionEnhancement,
                effectType: 1,
                level: 1,
                rarity: 1
            ),
        },
        IsMasterComplete: true,
        Detail: string.Empty
    );

    private static ScriptedRuntimeBridge RunOwnedFloorTransaction(
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
            Pump(expectedChild == NetherActionKind.BuyShopItem ? 7 : 5);

            Assert.Equal(NetherAutoClimbPhase.Stable, NetherAutoClimbController.Phase);
            Assert.Single(bridge.Invocations.Where(action => action == NetherActionKind.SelectFloor));
            NetherPlannedAction child = Assert.Single(bridge.OwnedPopupActions);
            Assert.Equal(expectedChild, child.Kind);
            Assert.Equal(1, bridge.GetOnlyBeginCount);
            Assert.Equal(1, bridge.GetOnlyPollCount);
            return bridge;
        }
        finally
        {
            NetherAutoClimbController.OnPluginUnload();
        }
    }

    private sealed class ScriptedRuntimeBridge : INetherRuntimeBridge, INetherOwnedPopupNativeStagePort
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
        private bool _shopPurchaseSnapshotApplied;
        private bool _codeReloadSnapshotApplied;
        private bool _codeKeepSnapshotApplied;
        // The scripted native port deliberately leaves popup and SelectFloor parent pending
        // after every child.  The production core below is therefore the only sequencing
        // implementation exercised by E2E tests.
        private readonly NetherOwnedPopupStageBridgeEntry _ownedPopupStageEntry;

        public ScriptedRuntimeBridge()
        {
            _ownedPopupStageEntry = new NetherOwnedPopupStageBridgeEntry(this, maximumPendingPumps: 8);
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
        public NetherSnapshot? CodeReloadAfterSnapshot { get; set; }
        public NetherRuntimeRouteSafetyData? RouteSafetyOverride { get; set; }
        public Func<NetherSnapshot, NetherAutoClimbSettings, NetherRuntimeInteractivePreEntryInputsResult>? InteractivePreEntryFactory { get; set; }
        public NetherRuntimeCodeCandidatesResult CodeCandidates { get; set; } =
            NetherRuntimeCodeCandidatesResult.Failure("e2e-no-code-popup");
        public NetherRuntimeCodeCandidatesResult ReloadCodeCandidates { get; set; } =
            NetherRuntimeCodeCandidatesResult.Failure("e2e-no-reloaded-code-popup");
        public List<NetherActionKind> Invocations { get; } = new();
        public List<NetherPlannedAction> OwnedPopupActions { get; } = new();
        public int BeginFloorParentCount { get; private set; }
        public int OwnedPopupInvokeCount { get; private set; }
        public int ShopCloseInvokeCount { get; private set; }
        public int CodeReloadInvokeCount { get; private set; }
        public int CodeKeepInvokeCount { get; private set; }
        public NetherNativeActionResult ShopPurchaseChildPollResult { get; set; } =
            NetherNativeActionResult.Completed("scripted-shop-purchase-complete");
        public NetherNativeActionResult CodeReloadTaskPollResult { get; set; } =
            NetherNativeActionResult.Completed("scripted-code-reload-complete");
        public NetherNativeActionResult CodeKeepTaskPollResult { get; set; } =
            NetherNativeActionResult.Completed("scripted-code-keep-cancel-complete");
        public int GetOnlyBeginCount { get; private set; }
        public int GetOnlyPollCount { get; private set; }
        public int ContinuePreflightCount { get; private set; }
        public int ContinueNativeInvokeCount { get; private set; }
        public int ContinueReadOnlyBeginCount { get; private set; }
        public int ResultPollCount { get; private set; }
        public int FloorParentPollCount { get; private set; }
        public int FloorParentTerminalCount { get; private set; }
        public int ContinueParentPollCount { get; private set; }
        public List<string> Trace { get; } = new();
        public bool ContinueParentCompleted { get; set; }
        /// <summary>
        /// Opt-in truthful parent-task seam for modal E2E tests.  The default remains eager for
        /// legacy fixtures, while transaction tests can prove a child/close never fabricates
        /// the SelectFloor parent terminal or starts GET early.
        /// </summary>
        public bool RequireExplicitFloorParentTerminal { get; set; }
        public bool FloorParentCompleted { get; set; }
        public bool FloorOwnerTerminated { get; set; }
        public long CurrentRuntimeGeneration { get; set; } = 1;

        private readonly Queue<NetherRuntimePopupContext> _queuedOwnedPopups = new();
        private readonly Queue<NetherSnapshot?> _queuedOwnedPopupSnapshots = new();
        private readonly Queue<(NetherSnapshot Snapshot, NetherRuntimeCodeCandidatesResult Candidates)> _queuedCodeReloadRefreshes = new();

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

        public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent)
        {
            if (OwnedPopup != null)
            {
                NetherRuntimePopupContext popup = OwnedPopup;
                if (popup.Kind == NetherRuntimePopupKind.CodeOffer)
                {
                    popup = popup with
                    {
                        DecisionEpoch = _ownedPopupStageEntry.GetDecisionEpoch(
                            new NetherOwnedPopupStageOwner(
                                popup.OwnerAction,
                                popup.OwnerGeneration,
                                popup.Sequence,
                                0
                            )
                        ),
                    };
                }
                return NetherRuntimePopupResult.Success(popup);
            }
            return _queuedOwnedPopups.Count == 0
                ? NetherRuntimePopupResult.Failure("no-owned-popup")
                : NetherRuntimePopupResult.Success(_queuedOwnedPopups.Peek());
        }

        public void EnqueueOwnedPopup(NetherRuntimePopupContext popup, NetherSnapshot? snapshotAfterInvoke)
        {
            _queuedOwnedPopups.Enqueue(popup);
            _queuedOwnedPopupSnapshots.Enqueue(snapshotAfterInvoke);
        }

        public void EnqueueCodeReloadRefresh(
            NetherSnapshot snapshot,
            NetherRuntimeCodeCandidatesResult candidates
        ) => _queuedCodeReloadRefreshes.Enqueue((snapshot, candidates));

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
            return _ownedPopupStageEntry.Dispatch(
                parent,
                popup,
                action,
                DispatchScriptedNonStagePopup,
                () => DispatchScriptedNonStagePopup(new NetherPlannedAction(NetherActionKind.LeaveShop)),
                DispatchScriptedNonStagePopup
            );
        }

        private NetherNativeActionResult DispatchScriptedNonStagePopup(NetherPlannedAction action)
        {
            if (OwnedPopup != null)
            {
                OwnedPopup = null;
                if (OwnedPopupAfterSnapshot != null)
                    CurrentSnapshot = OwnedPopupAfterSnapshot;
            }
            else if (_queuedOwnedPopups.Count > 0)
            {
                _queuedOwnedPopups.Dequeue();
                NetherSnapshot? snapshotAfterInvoke = _queuedOwnedPopupSnapshots.Dequeue();
                if (snapshotAfterInvoke != null)
                    CurrentSnapshot = snapshotAfterInvoke;
            }
            else
            {
                return NetherNativeActionResult.BindingUnavailable("missing-scripted-owned-popup");
            }
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
            NetherOwnedPopupStageParentGate stage = _ownedPopupStageEntry.PumpBeforeParent();
            if (!stage.MayPollParent)
                return stage.Native;
            if (OwnedPopup != null || _queuedOwnedPopups.Count > 0)
                return NetherNativeActionResult.Started("native-floor-parent-awaiting-owned-popup");
            if (RequireExplicitFloorParentTerminal && !FloorParentCompleted)
                return NetherNativeActionResult.Started("native-floor-parent-still-pending");
            FloorParentTerminalCount++;
            return NetherNativeActionResult.Completed("native-floor-parent-terminal");
        }

        bool INetherOwnedPopupNativeStagePort.IsCurrentOwnedPopup(
            NetherRuntimePopupKind kind,
            NetherOwnedPopupStageOwner owner
        )
        {
            NetherRuntimePopupContext? popup = OwnedPopup ?? (
                _queuedOwnedPopups.Count > 0 ? _queuedOwnedPopups.Peek() : null
            );
            return _floorParentPending
                && popup != null
                && popup.Kind == kind
                && popup.OwnerAction == owner.OwnerAction
                && popup.OwnerGeneration == owner.Generation
                && popup.Sequence == owner.Sequence;
        }

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeShopPurchase(
            NetherOwnedPopupStageOwner owner,
            NetherPlannedAction action
        ) => NetherNativeActionResult.Started("scripted-shop-purchase-invoked");

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollShopPurchaseTask(
            NetherShopPurchaseCloseOwner owner
        )
        {
            NetherNativeActionResult result = ShopPurchaseChildPollResult;
            if (result.Kind == NetherNativeActionResultKind.Completed && !_shopPurchaseSnapshotApplied)
            {
                _shopPurchaseSnapshotApplied = true;
                if (OwnedPopupAfterSnapshot != null)
                    CurrentSnapshot = OwnedPopupAfterSnapshot;
            }
            return result;
        }

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeExactShopClose(
            NetherShopPurchaseCloseOwner owner
        )
        {
            ShopCloseInvokeCount++;
            OwnedPopup = null;
            return NetherNativeActionResult.Started("scripted-shop-close");
        }

        NetherOwnedPopupCodeReloadStart INetherOwnedPopupNativeStagePort.CaptureCodeReloadStart(
            NetherOwnedPopupStageOwner owner
        ) => new(CurrentSnapshot.CodeReloadCount, CodeCandidates, string.Empty);

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeReload(
            NetherCodeReloadEpochOwner owner
        )
        {
            CodeReloadInvokeCount++;
            return NetherNativeActionResult.Started("scripted-code-reload-invoked");
        }

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollCodeReloadTask(
            NetherCodeReloadEpochOwner owner
        )
        {
            NetherNativeActionResult result = CodeReloadTaskPollResult;
            if (result.Kind == NetherNativeActionResultKind.Completed && _queuedCodeReloadRefreshes.Count > 0)
            {
                (NetherSnapshot snapshot, NetherRuntimeCodeCandidatesResult candidates) = _queuedCodeReloadRefreshes.Dequeue();
                CurrentSnapshot = snapshot;
                CodeCandidates = candidates;
            }
            else if (result.Kind == NetherNativeActionResultKind.Completed && !_codeReloadSnapshotApplied)
            {
                _codeReloadSnapshotApplied = true;
                if (CodeReloadAfterSnapshot != null)
                    CurrentSnapshot = CodeReloadAfterSnapshot;
                CodeCandidates = ReloadCodeCandidates;
            }
            return result;
        }

        NetherCodeReloadEpochRefresh INetherOwnedPopupNativeStagePort.CaptureFreshCodeReloadOffer(
            NetherCodeReloadEpochOwner owner
        ) => new(owner, CurrentSnapshot.CodeReloadCount, CodeCandidates);

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.InvokeCodeKeepCancel(
            NetherCodeKeepCancelOwner owner
        )
        {
            CodeKeepInvokeCount++;
            return _ownedPopupStageEntry.ObserveKeepCancelTask(owner)
                ? NetherNativeActionResult.Started("scripted-code-keep-cancel-invoked")
                : NetherNativeActionResult.BindingUnavailable("scripted-code-keep-task-observer-unavailable");
        }

        NetherNativeActionResult INetherOwnedPopupNativeStagePort.PollCodeKeepCancelTask(
            NetherCodeKeepCancelOwner owner
        )
        {
            NetherNativeActionResult result = CodeKeepTaskPollResult;
            if (result.Kind == NetherNativeActionResultKind.Completed && !_codeKeepSnapshotApplied)
            {
                _codeKeepSnapshotApplied = true;
                OwnedPopup = null;
                if (OwnedPopupAfterSnapshot != null)
                    CurrentSnapshot = OwnedPopupAfterSnapshot;
            }
            return result;
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
            _queuedOwnedPopups.Clear();
            _queuedOwnedPopupSnapshots.Clear();
            _ownedPopupStageEntry.Reset();
            _codeReloadSnapshotApplied = false;
            _codeKeepSnapshotApplied = false;
            _queuedCodeReloadRefreshes.Clear();
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

        public NetherNativeActionResult ProbePersistedLease() => NeedsRecovery
            ? NetherNativeActionResult.Started("e2e-persisted-lease-awaiting-accessor")
            : NetherNativeActionResult.Completed("e2e-no-persisted-lease");

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

    private sealed class StartupLeaseHarness : IDisposable
    {
        private readonly string _previousConfigPath;

        public StartupLeaseHarness()
        {
            _previousConfigPath = BepInEx.Paths.ConfigPath;
            ConfigPath = Path.Combine(Path.GetTempPath(), "abyssmod-round4-startup-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ConfigPath);
            BepInEx.Paths.ConfigPath = ConfigPath;
            OriginalLease = CreateLease(new StartupLeaseNative(autoEnabled: false, speed: 1));
        }

        public string ConfigPath { get; }
        public string LeaseFilePath => Path.Combine(ConfigPath, "AbyssMod.nether-battle-settings-lease.json");
        public NetherBattleSettingsLease OriginalLease { get; }

        public NetherBattleSettingsLease CreateDetachedLease() => (NetherBattleSettingsLease)Activator.CreateInstance(
            typeof(NetherBattleSettingsLease),
            nonPublic: true
        )!;

        public void Attach(NetherBattleSettingsLease lease, INetherBattleSettingsNative native) => typeof(NetherBattleSettingsLease)
            .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lease, native);

        public void Dispose()
        {
            BepInEx.Paths.ConfigPath = _previousConfigPath;
            if (Directory.Exists(ConfigPath))
                Directory.Delete(ConfigPath, recursive: true);
        }

        private static NetherBattleSettingsLease CreateLease(INetherBattleSettingsNative native)
        {
            var lease = (NetherBattleSettingsLease)Activator.CreateInstance(
                typeof(NetherBattleSettingsLease),
                nonPublic: true
            )!;
            typeof(NetherBattleSettingsLease)
                .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(lease, native);
            return lease;
        }
    }

    private sealed class StartupLeaseNative : INetherBattleSettingsNative
    {
        public StartupLeaseNative(bool autoEnabled, int speed)
        {
            AutoEnabled = autoEnabled;
            Speed = speed;
        }

        public bool AutoEnabled { get; private set; }
        public int Speed { get; private set; }
        public int WriteCalls { get; private set; }

        public bool TryRead(out bool autoEnabled, out int speed, out string error)
        {
            autoEnabled = AutoEnabled;
            speed = Speed;
            error = string.Empty;
            return true;
        }

        public bool TryForceAutoAndHighestSpeed(out string error)
        {
            AutoEnabled = true;
            Speed = 3;
            error = string.Empty;
            return true;
        }

        public bool TryWrite(bool autoEnabled, int speed, out string error)
        {
            WriteCalls++;
            AutoEnabled = autoEnabled;
            Speed = speed;
            error = string.Empty;
            return true;
        }
    }
}
