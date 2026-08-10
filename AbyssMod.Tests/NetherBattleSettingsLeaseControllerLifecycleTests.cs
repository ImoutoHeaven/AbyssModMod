using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

[Collection("nether-battle-settings-lease-runtime")]
public class NetherBattleSettingsLeaseControllerLifecycleTests
{
    [Fact]
    public void ProductionControllerLifecycle_DefersPersistedRecoveryUntilExactAccessorRegistration()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        Assert.Equal(NetherNativeActionResultKind.Completed, harness.Lease.AcquireAndForce().Kind);
        Assert.True(File.Exists(harness.LeaseFilePath));

        var recoveredNative = new NativeSettings(autoEnabled: true, speed: 3);
        NetherBattleSettingsLease recoveredLease = harness.CreateLease(recoveredNative);
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(recoveredLease);

        lifecycle.OnControllerInitialized();

        Assert.True(File.Exists(harness.LeaseFilePath));
        Assert.True(recoveredNative.AutoEnabled);
        Assert.Equal(3, recoveredNative.Speed);
        Assert.True(lifecycle.BlocksRouteOrBattle);

        NetherNativeActionResult recovery = lifecycle.OnExactAccessorRegistered();

        Assert.Equal(NetherNativeActionResultKind.Completed, recovery.Kind);
        Assert.False(File.Exists(harness.LeaseFilePath));
        Assert.False(recoveredNative.AutoEnabled);
        Assert.Equal(1, recoveredNative.Speed);
        Assert.False(lifecycle.BlocksRouteOrBattle);
    }

    [Fact]
    public void ProductionControllerLifecycle_ProbesRealPersistedLeaseBeforeAccessorAndBlocksRouteWithoutNativeWrites()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        Assert.Equal(NetherNativeActionResultKind.Completed, harness.Lease.AcquireAndForce().Kind);
        Assert.True(File.Exists(harness.LeaseFilePath));

        var recoveryNative = new NativeSettings(autoEnabled: true, speed: 3);
        NetherBattleSettingsLease recoveredLease = harness.CreateLeaseWithoutNative();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(recoveredLease);

        lifecycle.OnControllerInitialized();

        Assert.True(lifecycle.BlocksRoute);
        Assert.False(lifecycle.IsExactAccessorRegistered);
        Assert.Equal(0, recoveryNative.WriteCalls);
        Assert.True(File.Exists(harness.LeaseFilePath));

        harness.AttachNative(recoveredLease, recoveryNative);
        NetherNativeActionResult recovery = lifecycle.OnExactAccessorRegistered();

        Assert.Equal(NetherNativeActionResultKind.Completed, recovery.Kind);
        Assert.False(recoveryNative.AutoEnabled);
        Assert.Equal(1, recoveryNative.Speed);
        Assert.Equal(1, recoveryNative.WriteCalls);
        Assert.False(File.Exists(harness.LeaseFilePath));
        Assert.False(lifecycle.BlocksRoute);
    }

    [Fact]
    public void ProductionControllerLifecycle_NoLeaseStaysRouteableBeforeBattleAccessor()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        NetherBattleSettingsLease lease = harness.CreateLeaseWithoutNative();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease);

        lifecycle.OnControllerInitialized();

        Assert.False(File.Exists(harness.LeaseFilePath));
        Assert.False(lifecycle.BlocksRoute);
        Assert.False(lifecycle.IsExactAccessorRegistered);
    }

    [Fact]
    public void ProductionControllerLifecycle_CorruptLeaseBlocksThenReadOnlyRetryCanReleaseWithoutAccessor()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        File.WriteAllText(harness.LeaseFilePath, "not-json");
        NetherBattleSettingsLease lease = harness.CreateLeaseWithoutNative();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(lease, retryIntervalUpdates: 1);

        lifecycle.OnControllerInitialized();

        Assert.True(lifecycle.BlocksRoute);
        Assert.Equal(NetherBattleSettingsLeaseRuntimeState.RetryWait, lifecycle.RuntimeState);

        File.Delete(harness.LeaseFilePath);
        NetherBattleSettingsLeaseRetryPumpResult retry = lifecycle.PumpUpdate();

        Assert.True(retry.Attempted);
        Assert.Equal(NetherNativeActionResultKind.Completed, retry.Result?.Kind);
        Assert.False(lifecycle.BlocksRoute);
        Assert.False(lifecycle.IsExactAccessorRegistered);
    }

    [Fact]
    public void ProductionControllerLifecycle_RestoresEachBattleAndUsesOffAndUnloadForLaterBattles()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(harness.Lease);
        lifecycle.OnControllerInitialized();
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnExactAccessorRegistered().Kind);

        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleClearOrClose().Kind);

        harness.Native.AutoEnabled = true;
        harness.Native.Speed = 2;
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnF12Off().Kind);
        Assert.True(harness.Native.AutoEnabled);
        Assert.Equal(2, harness.Native.Speed);

        harness.Native.AutoEnabled = false;
        harness.Native.Speed = 0;
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnPluginUnload().Kind);
        Assert.False(harness.Native.AutoEnabled);
        Assert.Equal(0, harness.Native.Speed);
        Assert.Equal(3, harness.Native.ForceCalls);
    }

    [Fact]
    public void ProductionControllerLifecycle_BoundedUpdateRetriesDoNotRetryEveryFrame()
    {
        var driver = new RetryLeaseDriver
        {
            RetryResults = new Queue<NetherNativeActionResult>(new[]
            {
                NetherNativeActionResult.UnknownOutcome("retry-one-failed"),
                NetherNativeActionResult.Completed("retry-two-restored"),
            }),
        };
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(
            driver,
            retryIntervalUpdates: 3
        );
        lifecycle.OnControllerInitialized();
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnExactAccessorRegistered().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, lifecycle.OnBattleClearOrClose().Kind);

        Assert.True(lifecycle.PumpUpdate().Attempted);
        Assert.False(lifecycle.PumpUpdate().Attempted);
        Assert.False(lifecycle.PumpUpdate().Attempted);
        Assert.True(lifecycle.PumpUpdate().Attempted);

        Assert.Equal(2, driver.RetryCalls);
        Assert.False(lifecycle.BlocksRouteOrBattle);
    }

    [Fact]
    public void ProductionControllerLifecycle_RestoreFaultBlocksBothRouteAndNextBattle()
    {
        var driver = new RetryLeaseDriver();
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(driver);
        lifecycle.OnControllerInitialized();
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnExactAccessorRegistered().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);

        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, lifecycle.OnBattleClearOrClose().Kind);

        Assert.True(lifecycle.BlocksRouteOrBattle);
        Assert.Equal(NetherNativeActionResultKind.Rejected, lifecycle.OnBattleEnter().Kind);
        Assert.Equal(1, driver.AcquireCalls);
    }

    [Fact]
    public void ProductionControllerLifecycle_AccessorDestroyUnbindsButKeepsPersistedRecoveryEvidence()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(harness.Lease);
        lifecycle.OnControllerInitialized();
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnExactAccessorRegistered().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);
        Assert.True(File.Exists(harness.LeaseFilePath));

        harness.DetachNative(harness.Lease);
        lifecycle.OnExactAccessorUnregistered();
        NetherNativeActionResult unload = lifecycle.OnPluginUnload();

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, unload.Kind);
        Assert.False(lifecycle.IsExactAccessorRegistered);
        Assert.True(lifecycle.BlocksRouteOrBattle);
        Assert.True(File.Exists(harness.LeaseFilePath));
    }

    [Fact]
    public void ProductionControllerLifecycle_ReboundAccessorRestoresActiveSecondBattleBeforeRouteCanResume()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var lifecycle = new NetherBattleSettingsLeaseControllerLifecycle(harness.Lease);
        lifecycle.OnControllerInitialized();
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnExactAccessorRegistered().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, lifecycle.OnBattleEnter().Kind);

        harness.DetachNative(harness.Lease);
        lifecycle.OnExactAccessorUnregistered();
        var reboundNative = new NativeSettings(autoEnabled: true, speed: 3);
        harness.AttachNative(harness.Lease, reboundNative);

        NetherNativeActionResult rebound = lifecycle.OnExactAccessorRegistered();

        Assert.Equal(NetherNativeActionResultKind.Completed, rebound.Kind);
        Assert.False(reboundNative.AutoEnabled);
        Assert.Equal(1, reboundNative.Speed);
        Assert.False(File.Exists(harness.LeaseFilePath));
        Assert.False(lifecycle.BlocksRouteOrBattle);
    }

    private sealed class LeaseHarness : IDisposable
    {
        private readonly string _previousConfigPath;

        public LeaseHarness(bool autoEnabled, int speed)
        {
            _previousConfigPath = BepInEx.Paths.ConfigPath;
            ConfigPath = Path.Combine(Path.GetTempPath(), "abyssmod-controller-lease-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ConfigPath);
            BepInEx.Paths.ConfigPath = ConfigPath;
            Native = new NativeSettings(autoEnabled, speed);
            Lease = CreateLease(Native);
        }

        public string ConfigPath { get; }
        public string LeaseFilePath => Path.Combine(ConfigPath, "AbyssMod.nether-battle-settings-lease.json");
        public NativeSettings Native { get; }
        public NetherBattleSettingsLease Lease { get; }

        public NetherBattleSettingsLease CreateLease(NativeSettings native)
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

        public NetherBattleSettingsLease CreateLeaseWithoutNative() => (NetherBattleSettingsLease)Activator.CreateInstance(
            typeof(NetherBattleSettingsLease),
            nonPublic: true
        )!;

        public void DetachNative(NetherBattleSettingsLease lease) => typeof(NetherBattleSettingsLease)
            .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lease, null);

        public void AttachNative(NetherBattleSettingsLease lease, INetherBattleSettingsNative native) => typeof(NetherBattleSettingsLease)
            .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lease, native);

        public void Dispose()
        {
            BepInEx.Paths.ConfigPath = _previousConfigPath;
            if (Directory.Exists(ConfigPath))
                Directory.Delete(ConfigPath, recursive: true);
        }
    }

    private sealed class NativeSettings : INetherBattleSettingsNative
    {
        public NativeSettings(bool autoEnabled, int speed)
        {
            AutoEnabled = autoEnabled;
            Speed = speed;
        }

        public bool AutoEnabled { get; set; }
        public int Speed { get; set; }
        public int ForceCalls { get; private set; }
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
            ForceCalls++;
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

    private sealed class RetryLeaseDriver : INetherBattleSettingsLeaseDriver
    {
        public Queue<NetherNativeActionResult> RetryResults { get; set; } = new();
        public int AcquireCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public NetherBattleSettingsLeasePhase Phase { get; private set; } = NetherBattleSettingsLeasePhase.Empty;
        public bool NeedsRecovery { get; private set; }

        public NetherNativeActionResult ProbePersistedLease() => NeedsRecovery
            ? NetherNativeActionResult.Started("retry-persisted-lease-awaiting-accessor")
            : NetherNativeActionResult.Completed("retry-no-persisted-lease");

        public NetherNativeActionResult AcquireAndForce()
        {
            AcquireCalls++;
            Phase = NetherBattleSettingsLeasePhase.Forced;
            return NetherNativeActionResult.Completed("forced");
        }

        public NetherNativeActionResult Restore(string reason)
        {
            Phase = NetherBattleSettingsLeasePhase.Faulted;
            NeedsRecovery = true;
            return NetherNativeActionResult.UnknownOutcome("restore-fault");
        }

        public NetherNativeActionResult RecoverOnLoad()
        {
            Phase = NetherBattleSettingsLeasePhase.Restored;
            NeedsRecovery = false;
            return NetherNativeActionResult.Completed("startup-restored");
        }

        public NetherNativeActionResult RetryRestoreAfterNativeAccessorRegistered()
        {
            RetryCalls++;
            NetherNativeActionResult result = RetryResults.Count == 0
                ? NetherNativeActionResult.UnknownOutcome("retry-fault")
                : RetryResults.Dequeue();
            if (result.Kind == NetherNativeActionResultKind.Completed)
            {
                Phase = NetherBattleSettingsLeasePhase.Restored;
                NeedsRecovery = false;
            }
            else
            {
                Phase = NetherBattleSettingsLeasePhase.Faulted;
                NeedsRecovery = true;
            }
            return result;
        }
    }
}
