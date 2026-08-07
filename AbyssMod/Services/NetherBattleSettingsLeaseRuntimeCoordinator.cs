#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Minimal boundary around the real persisted lease.  The coordinator intentionally owns no
/// copy of Auto/speed or a session-wide "already restored" bit: the lease's phase and native
/// readback remain the authority for every battle.
/// </summary>
internal interface INetherBattleSettingsLeaseDriver
{
    NetherBattleSettingsLeasePhase Phase { get; }

    bool NeedsRecovery { get; }

    NetherNativeActionResult AcquireAndForce();

    NetherNativeActionResult Restore(string reason);

    NetherNativeActionResult RecoverOnLoad();

    NetherNativeActionResult RetryRestoreAfterNativeAccessorRegistered();
}

internal enum NetherBattleSettingsLeaseRuntimeState
{
    Ready,
    RetryWait,
    Paused,
}

/// <summary>
/// The Controller only logs a retry when an actual native read/write retry was attempted.  This
/// keeps a pending persisted lease recoverable without turning every update into a log entry or
/// a native settings write.
/// </summary>
internal readonly record struct NetherBattleSettingsLeaseRetryPumpResult(
    bool Attempted,
    NetherNativeActionResult? Result
)
{
    public static NetherBattleSettingsLeaseRetryPumpResult NotAttempted => new(false, null);
}

/// <summary>
/// Coordinates the persisted native Auto/speed lease at battle boundaries.  It is deliberately
/// separate from the F12 Controller in C2a so controller integration cannot accidentally hide a
/// restore failure behind session-global bookkeeping.
/// </summary>
internal sealed class NetherBattleSettingsLeaseRuntimeCoordinator
{
    private readonly INetherBattleSettingsLeaseDriver _lease;
    private readonly int _maximumRestoreRetries;
    private readonly int _retryIntervalUpdates;
    private int _restoreRetries;
    private long _nextRetryUpdate = 1;

    public NetherBattleSettingsLeaseRuntimeCoordinator(
        INetherBattleSettingsLeaseDriver lease,
        int maximumRestoreRetries = 3,
        int retryIntervalUpdates = 60
    )
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (maximumRestoreRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRestoreRetries));
        if (retryIntervalUpdates <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryIntervalUpdates));
        _maximumRestoreRetries = maximumRestoreRetries;
        _retryIntervalUpdates = retryIntervalUpdates;
        State = lease.NeedsRecovery
            ? NetherBattleSettingsLeaseRuntimeState.RetryWait
            : NetherBattleSettingsLeaseRuntimeState.Ready;
    }

    public NetherBattleSettingsLeaseRuntimeState State { get; private set; }

    /// <summary>Always exposes the underlying persisted lease phase rather than a cached copy.</summary>
    public NetherBattleSettingsLeasePhase LeasePhase => _lease.Phase;

    public int RestoreRetries => _restoreRetries;

    public bool BlocksBattleEntry => State != NetherBattleSettingsLeaseRuntimeState.Ready
        || _lease.Phase is not (NetherBattleSettingsLeasePhase.Empty or NetherBattleSettingsLeasePhase.Restored);

    public NetherNativeActionResult OnBattleEnter()
    {
        if (BlocksBattleEntry)
            return NetherNativeActionResult.Rejected("battle-settings-lease-entry-blocked:" + State + ":" + _lease.Phase);

        NetherNativeActionResult result = _lease.AcquireAndForce();
        if (result.Kind == NetherNativeActionResultKind.Completed
            && _lease.Phase == NetherBattleSettingsLeasePhase.Forced)
        {
            return result;
        }

        return ObserveFailedEntry(result);
    }

    public NetherNativeActionResult OnBattleExit() => RequestRestore("battle-exit");

    public NetherNativeActionResult OnF12Off() => RequestRestore("f12-off");

    public NetherNativeActionResult OnLeaveNether() => RequestRestore("leave-nether");

    public NetherNativeActionResult OnPluginUnload() => RequestRestore("plugin-unload");

    public NetherNativeActionResult OnAutomationPause() => RequestRestore("automation-pause");

    public NetherNativeActionResult RecoverOnStartup()
    {
        NetherNativeActionResult result = _lease.RecoverOnLoad();
        return ObserveRestoreResult(result);
    }

    /// <summary>
    /// One bounded retry.  The real lease performs its own native write/readback before this
    /// method can reopen entry; a successful method return alone is not enough unless the lease
    /// phase is Empty/Restored.
    /// </summary>
    public NetherNativeActionResult RetryRestore()
    {
        if (State != NetherBattleSettingsLeaseRuntimeState.RetryWait)
            return NetherNativeActionResult.Rejected("battle-settings-lease-retry-not-pending:" + State);

        _restoreRetries++;
        NetherNativeActionResult result = _lease.RetryRestoreAfterNativeAccessorRegistered();
        return ObserveRestoreResult(result);
    }

    /// <summary>
    /// Runs at most one retry for a due update tick.  A caller may invoke this every frame: no
    /// native call is made before the bounded interval elapses, and a terminal result disables
    /// the pump automatically through <see cref="State"/>.
    /// </summary>
    public NetherBattleSettingsLeaseRetryPumpResult PumpScheduledRetry(long updateTick)
    {
        if (updateTick < 0)
            throw new ArgumentOutOfRangeException(nameof(updateTick));
        if (State != NetherBattleSettingsLeaseRuntimeState.RetryWait || updateTick < _nextRetryUpdate)
            return NetherBattleSettingsLeaseRetryPumpResult.NotAttempted;

        _nextRetryUpdate = updateTick > long.MaxValue - _retryIntervalUpdates
            ? long.MaxValue
            : updateTick + _retryIntervalUpdates;
        return new NetherBattleSettingsLeaseRetryPumpResult(true, RetryRestore());
    }

    private NetherNativeActionResult RequestRestore(string reason)
    {
        NetherNativeActionResult result = _lease.Phase is NetherBattleSettingsLeasePhase.Faulted
            or NetherBattleSettingsLeasePhase.RestorePending
            ? _lease.RetryRestoreAfterNativeAccessorRegistered()
            : _lease.Restore(reason);
        return ObserveRestoreResult(result);
    }

    private NetherNativeActionResult ObserveFailedEntry(NetherNativeActionResult result)
    {
        if (result.Kind == NetherNativeActionResultKind.Rejected)
        {
            State = NetherBattleSettingsLeaseRuntimeState.Paused;
            return result;
        }

        State = _lease.NeedsRecovery
            ? NetherBattleSettingsLeaseRuntimeState.RetryWait
            : NetherBattleSettingsLeaseRuntimeState.Paused;
        return result;
    }

    private NetherNativeActionResult ObserveRestoreResult(NetherNativeActionResult result)
    {
        if (result.Kind == NetherNativeActionResultKind.Completed
            && _lease.Phase is NetherBattleSettingsLeasePhase.Empty or NetherBattleSettingsLeasePhase.Restored)
        {
            _restoreRetries = 0;
            State = NetherBattleSettingsLeaseRuntimeState.Ready;
            return result;
        }

        if (result.Kind == NetherNativeActionResultKind.Rejected)
        {
            State = NetherBattleSettingsLeaseRuntimeState.Paused;
            return result;
        }

        State = _lease.NeedsRecovery && _restoreRetries < _maximumRestoreRetries
            ? NetherBattleSettingsLeaseRuntimeState.RetryWait
            : NetherBattleSettingsLeaseRuntimeState.Paused;
        return result;
    }
}
