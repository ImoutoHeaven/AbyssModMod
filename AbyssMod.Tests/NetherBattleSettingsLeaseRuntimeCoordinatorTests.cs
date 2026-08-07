using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

[CollectionDefinition("nether-battle-settings-lease-runtime", DisableParallelization = true)]
public sealed class NetherBattleSettingsLeaseRuntimeCollection { }

[Collection("nether-battle-settings-lease-runtime")]
public class NetherBattleSettingsLeaseRuntimeCoordinatorTests
{
    [Fact]
    public void RealLease_AcquiresAndRestoresEachBattleInsteadOfUsingASessionRestoreOnceFlag()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Forced, coordinator.LeasePhase);
        Assert.True(harness.Native.AutoEnabled);
        Assert.Equal(3, harness.Native.Speed);
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleExit().Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Restored, coordinator.LeasePhase);
        Assert.False(harness.Native.AutoEnabled);
        Assert.Equal(1, harness.Native.Speed);

        harness.Native.AutoEnabled = true;
        harness.Native.Speed = 2;
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleExit().Kind);

        Assert.Equal(2, harness.Native.ForceCalls);
        Assert.Equal(new[] { (false, 1), (true, 2) }, harness.Native.RestoreWrites);
        Assert.True(harness.Native.AutoEnabled);
        Assert.Equal(2, harness.Native.Speed);
    }

    [Fact]
    public void RealLease_ReadbackMismatchRetainsLeaseBlocksEntryAndMovesCoordinatorToRetryWait()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        harness.Native.NextReadOverride = (true, 2);

        NetherNativeActionResult restore = coordinator.OnBattleExit();

        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, restore.Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Faulted, coordinator.LeasePhase);
        Assert.Equal(NetherBattleSettingsLeaseRuntimeState.RetryWait, coordinator.State);
        Assert.True(coordinator.BlocksBattleEntry);
        Assert.True(File.Exists(harness.LeaseFilePath));
        Assert.Equal(NetherNativeActionResultKind.Rejected, coordinator.OnBattleEnter().Kind);
        Assert.Equal(1, harness.Native.ForceCalls);
    }

    [Fact]
    public void RealLease_TransientRestoreWriteFailureRetriesWithReadbackAndReopensOnlyAfterCompleted()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease, maximumRestoreRetries: 2);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        harness.Native.RestoreWriteFailuresRemaining = 1;
        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, coordinator.OnBattleExit().Kind);
        Assert.True(coordinator.BlocksBattleEntry);

        NetherNativeActionResult retry = coordinator.RetryRestore();

        Assert.Equal(NetherNativeActionResultKind.Completed, retry.Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Restored, coordinator.LeasePhase);
        Assert.Equal(NetherBattleSettingsLeaseRuntimeState.Ready, coordinator.State);
        Assert.False(coordinator.BlocksBattleEntry);
        Assert.False(File.Exists(harness.LeaseFilePath));
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
    }

    [Fact]
    public void RealLease_BoundedRestoreRetriesPauseAndKeepThePersistedLeaseWhenEveryReadbackFails()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease, maximumRestoreRetries: 2);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        harness.Native.RestoreWriteFailuresRemaining = 3;
        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, coordinator.OnBattleExit().Kind);
        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, coordinator.RetryRestore().Kind);

        NetherNativeActionResult exhausted = coordinator.RetryRestore();

        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, exhausted.Kind);
        Assert.Equal(NetherBattleSettingsLeaseRuntimeState.Paused, coordinator.State);
        Assert.True(coordinator.BlocksBattleEntry);
        Assert.True(File.Exists(harness.LeaseFilePath));
    }

    [Fact]
    public void RealLease_OffAndUnloadRestoreTheActiveLeaseOfLaterBattles()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleExit().Kind);

        harness.Native.AutoEnabled = true;
        harness.Native.Speed = 2;
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnF12Off().Kind);
        Assert.True(harness.Native.AutoEnabled);
        Assert.Equal(2, harness.Native.Speed);

        harness.Native.AutoEnabled = false;
        harness.Native.Speed = 0;
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnPluginUnload().Kind);
        Assert.False(harness.Native.AutoEnabled);
        Assert.Equal(0, harness.Native.Speed);
    }

    [Fact]
    public void RealLease_LeavingNetherRestoresTheActiveBattleLease()
    {
        using var harness = new LeaseHarness(autoEnabled: true, speed: 2);
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease);

        Assert.Equal(NetherNativeActionResultKind.Completed, coordinator.OnBattleEnter().Kind);
        NetherNativeActionResult leave = coordinator.OnLeaveNether();

        Assert.Equal(NetherNativeActionResultKind.Completed, leave.Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Restored, coordinator.LeasePhase);
        Assert.True(harness.Native.AutoEnabled);
        Assert.Equal(2, harness.Native.Speed);
    }

    [Fact]
    public void RealLease_StartupRecoveryRestoresPersistedLeaseBeforeNewBattleCanEnter()
    {
        using var harness = new LeaseHarness(autoEnabled: false, speed: 1);
        var firstCoordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(harness.Lease);
        Assert.Equal(NetherNativeActionResultKind.Completed, firstCoordinator.OnBattleEnter().Kind);
        Assert.True(File.Exists(harness.LeaseFilePath));

        var recoveredNative = new NativeSettings(autoEnabled: true, speed: 3);
        NetherBattleSettingsLease recoveredLease = harness.CreateLease(recoveredNative);
        var recoveredCoordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(recoveredLease);

        NetherNativeActionResult recovery = recoveredCoordinator.RecoverOnStartup();

        Assert.Equal(NetherNativeActionResultKind.Completed, recovery.Kind);
        Assert.Equal(NetherBattleSettingsLeasePhase.Restored, recoveredCoordinator.LeasePhase);
        Assert.False(recoveredCoordinator.BlocksBattleEntry);
        Assert.False(recoveredNative.AutoEnabled);
        Assert.Equal(1, recoveredNative.Speed);
        Assert.False(File.Exists(harness.LeaseFilePath));
    }

    [Fact]
    public void RejectedLeaseResultPausesRatherThanPermittingAnotherBattle()
    {
        var driver = new RejectingLeaseDriver();
        var coordinator = new NetherBattleSettingsLeaseRuntimeCoordinator(driver);

        NetherNativeActionResult result = coordinator.OnBattleExit();

        Assert.Equal(NetherNativeActionResultKind.Rejected, result.Kind);
        Assert.Equal(NetherBattleSettingsLeaseRuntimeState.Paused, coordinator.State);
        Assert.True(coordinator.BlocksBattleEntry);
        Assert.Equal(0, driver.AcquireCalls);
    }

    private sealed class LeaseHarness : IDisposable
    {
        private readonly string _previousConfigPath;

        public LeaseHarness(bool autoEnabled, int speed)
        {
            _previousConfigPath = BepInEx.Paths.ConfigPath;
            ConfigPath = Path.Combine(Path.GetTempPath(), "abyssmod-lease-tests-" + Guid.NewGuid().ToString("N"));
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
        public int RestoreWriteFailuresRemaining { get; set; }
        public (bool AutoEnabled, int Speed)? NextReadOverride { get; set; }
        public List<(bool AutoEnabled, int Speed)> RestoreWrites { get; } = new();

        public bool TryRead(out bool autoEnabled, out int speed, out string error)
        {
            if (NextReadOverride is (bool overrideAuto, int overrideSpeed))
            {
                NextReadOverride = null;
                autoEnabled = overrideAuto;
                speed = overrideSpeed;
                error = string.Empty;
                return true;
            }

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
            if (RestoreWriteFailuresRemaining > 0)
            {
                RestoreWriteFailuresRemaining--;
                error = "transient-write-failure";
                return false;
            }

            RestoreWrites.Add((autoEnabled, speed));
            AutoEnabled = autoEnabled;
            Speed = speed;
            error = string.Empty;
            return true;
        }
    }

    private sealed class RejectingLeaseDriver : INetherBattleSettingsLeaseDriver
    {
        public int AcquireCalls { get; private set; }
        public NetherBattleSettingsLeasePhase Phase => NetherBattleSettingsLeasePhase.Forced;
        public bool NeedsRecovery => true;
        public NetherNativeActionResult ProbePersistedLease() => NetherNativeActionResult.Rejected("probe-rejected");
        public NetherNativeActionResult AcquireAndForce()
        {
            AcquireCalls++;
            return NetherNativeActionResult.Rejected("unexpected-acquire");
        }

        public NetherNativeActionResult Restore(string reason) => NetherNativeActionResult.Rejected("restore-rejected");
        public NetherNativeActionResult RecoverOnLoad() => NetherNativeActionResult.Rejected("recover-rejected");
        public NetherNativeActionResult RetryRestoreAfterNativeAccessorRegistered() => NetherNativeActionResult.Rejected("retry-rejected");
    }
}
