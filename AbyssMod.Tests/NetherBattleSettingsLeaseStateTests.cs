using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherBattleSettingsLeaseStateTests
{
    [Fact]
    public void Acquire_saves_original_values_before_force_transition()
    {
        var state = new NetherBattleSettingsLeaseState();

        Assert.True(state.SaveOriginal(autoEnabled: false, speed: 1));
        Assert.Equal(NetherBattleSettingsLeasePhase.Saved, state.Phase);
        Assert.False(state.OriginalAutoEnabled);
        Assert.Equal(1, state.OriginalSpeed);
        Assert.True(state.MarkForced());
        Assert.Equal(NetherBattleSettingsLeasePhase.Forced, state.Phase);
    }

    [Fact]
    public void Restore_returns_exact_original_auto_and_speed()
    {
        var state = ForcedState(autoEnabled: true, speed: 2);

        Assert.True(state.RequestRestore("battle-settled"));
        Assert.True(state.ObserveRestored(autoEnabled: true, speed: 2));

        Assert.Equal(NetherBattleSettingsLeasePhase.Restored, state.Phase);
        Assert.False(state.NeedsRecovery);
    }

    [Fact]
    public void F12_off_requests_restore_even_during_battle()
    {
        var state = ForcedState(autoEnabled: false, speed: 3);

        Assert.True(state.RequestRestore("f12-off"));

        Assert.Equal(NetherBattleSettingsLeasePhase.RestorePending, state.Phase);
        Assert.Equal("f12-off", state.LastReason);
    }

    [Fact]
    public void Plugin_unload_requests_restore()
    {
        var state = ForcedState(autoEnabled: true, speed: 1);

        Assert.True(state.RequestRestore("plugin-unload"));

        Assert.Equal(NetherBattleSettingsLeasePhase.RestorePending, state.Phase);
        Assert.Equal("plugin-unload", state.LastReason);
    }

    [Fact]
    public void Persisted_active_lease_is_recovered_on_next_load()
    {
        var state = new NetherBattleSettingsLeaseState();

        Assert.True(state.RecoverPersistedActive(autoEnabled: false, speed: 2));

        Assert.Equal(NetherBattleSettingsLeasePhase.RestorePending, state.Phase);
        Assert.False(state.OriginalAutoEnabled);
        Assert.Equal(2, state.OriginalSpeed);
        Assert.True(state.NeedsRecovery);
    }

    [Fact]
    public void Failed_atomic_save_faults_without_forcing_settings()
    {
        var state = new NetherBattleSettingsLeaseState();

        state.FailSave("atomic-save-failed");

        Assert.Equal(NetherBattleSettingsLeasePhase.Faulted, state.Phase);
        Assert.False(state.CanForce);
        Assert.Equal("atomic-save-failed", state.LastReason);
    }

    [Fact]
    public void Failed_restore_remains_recoverable_and_pauses_climber()
    {
        var state = ForcedState(autoEnabled: false, speed: 3);
        Assert.True(state.RequestRestore("battle-settled"));

        Assert.False(state.ObserveRestored(autoEnabled: true, speed: 3));

        Assert.Equal(NetherBattleSettingsLeasePhase.Faulted, state.Phase);
        Assert.True(state.NeedsRecovery);
        Assert.True(state.RetryRestore());
        Assert.Equal(NetherBattleSettingsLeasePhase.RestorePending, state.Phase);
    }

    private static NetherBattleSettingsLeaseState ForcedState(bool autoEnabled, int speed)
    {
        var state = new NetherBattleSettingsLeaseState();
        Assert.True(state.SaveOriginal(autoEnabled, speed));
        Assert.True(state.MarkForced());
        return state;
    }
}
