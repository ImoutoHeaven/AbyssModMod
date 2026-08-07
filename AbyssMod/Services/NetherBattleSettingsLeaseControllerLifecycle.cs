#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Production-facing lifecycle seam between the F12 controller, the exact native accessor
/// registration patch, and the persisted battle-settings lease.  It intentionally does not
/// cache a session-wide restore flag: the real lease phase determines whether every battle may
/// acquire settings and whether every boundary must restore them.
/// </summary>
internal sealed class NetherBattleSettingsLeaseControllerLifecycle
{
    private readonly NetherBattleSettingsLeaseRuntimeCoordinator _runtime;
    private bool _startupRecoveryRequested;
    private bool _startupRecoveryComplete;
    private bool _exactAccessorRegistered;
    private long _updateTick;

    public NetherBattleSettingsLeaseControllerLifecycle(
        INetherBattleSettingsLeaseDriver lease,
        int maximumRestoreRetries = 3,
        int retryIntervalUpdates = 60
    )
    {
        _runtime = new NetherBattleSettingsLeaseRuntimeCoordinator(
            lease,
            maximumRestoreRetries,
            retryIntervalUpdates
        );
    }

    public NetherBattleSettingsLeasePhase LeasePhase => _runtime.LeasePhase;

    public NetherBattleSettingsLeaseRuntimeState RuntimeState => _runtime.State;

    public bool IsExactAccessorRegistered => _exactAccessorRegistered;

    /// <summary>
    /// No startup restore is allowed before the exact BottomRight native accessor is registered.
    /// Until that point F12 must not enter a route or force battle settings.
    /// </summary>
    public bool BlocksRouteOrBattle => !_exactAccessorRegistered
        || !_startupRecoveryComplete
        || _runtime.BlocksBattleEntry;

    public void OnControllerInitialized()
    {
        // Deliberately no call to RecoverOnLoad: the accessor patch establishes the only safe
        // moment at which a persisted value can be read back and restored.
    }

    public NetherNativeActionResult OnExactAccessorRegistered()
    {
        _exactAccessorRegistered = true;
        bool needsExactRecovery = !_startupRecoveryRequested
            || _runtime.State == NetherBattleSettingsLeaseRuntimeState.RetryWait
            || _runtime.LeasePhase is NetherBattleSettingsLeasePhase.Forced
                or NetherBattleSettingsLeasePhase.RestorePending;
        if (needsExactRecovery)
        {
            _startupRecoveryRequested = true;
            NetherNativeActionResult recovery = _runtime.RecoverOnStartup();
            ObserveStartupRecovery(recovery);
            return recovery;
        }

        _startupRecoveryComplete = !_runtime.BlocksBattleEntry;
        return _startupRecoveryComplete
            ? NetherNativeActionResult.Completed("battle-settings-accessor-rebound")
            : NetherNativeActionResult.Started("battle-settings-accessor-rebound-awaiting-retry");
    }

    public void OnExactAccessorUnregistered()
    {
        _exactAccessorRegistered = false;
        _startupRecoveryComplete = false;
    }

    public NetherNativeActionResult OnBattleEnter()
    {
        if (BlocksRouteOrBattle)
        {
            return NetherNativeActionResult.Rejected(
                "battle-settings-lifecycle-entry-blocked:"
                + _exactAccessorRegistered
                + ":"
                + _startupRecoveryComplete
                + ":"
                + _runtime.State
            );
        }
        return _runtime.OnBattleEnter();
    }

    public NetherNativeActionResult OnBattleClearOrClose() => _runtime.OnBattleExit();

    public NetherNativeActionResult OnF12Off() => _runtime.OnF12Off();

    public NetherNativeActionResult OnLeaveNether() => _runtime.OnLeaveNether();

    public NetherNativeActionResult OnPluginUnload() => _runtime.OnPluginUnload();

    public NetherNativeActionResult OnAutomationPause() => _runtime.OnAutomationPause();

    /// <summary>
    /// May be called every controller update.  The runtime coordinator makes a real retry only
    /// when the configured interval is due, so this method remains silent between attempts.
    /// </summary>
    public NetherBattleSettingsLeaseRetryPumpResult PumpUpdate()
    {
        if (_updateTick < long.MaxValue)
            _updateTick++;
        if (!_exactAccessorRegistered)
            return NetherBattleSettingsLeaseRetryPumpResult.NotAttempted;

        NetherBattleSettingsLeaseRetryPumpResult result = _runtime.PumpScheduledRetry(_updateTick);
        if (result.Attempted && result.Result is { } nativeResult)
            ObserveStartupRecovery(nativeResult);
        return result;
    }

    private void ObserveStartupRecovery(NetherNativeActionResult result)
    {
        _startupRecoveryComplete = result.Kind == NetherNativeActionResultKind.Completed
            && !_runtime.BlocksBattleEntry;
    }
}
