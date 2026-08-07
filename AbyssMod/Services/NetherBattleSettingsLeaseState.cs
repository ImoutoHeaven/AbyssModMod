#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Pure transition table for the recoverable native battle-settings lease.  Persisting and
/// native setter calls live outside this class so every state transition can be characterized
/// without Unity or IL2CPP dependencies.
/// </summary>
internal enum NetherBattleSettingsLeasePhase
{
    Empty,
    Saved,
    Forced,
    RestorePending,
    Restored,
    Faulted,
}

internal sealed class NetherBattleSettingsLeaseState
{
    private bool _hasOriginal;

    public NetherBattleSettingsLeasePhase Phase { get; private set; } = NetherBattleSettingsLeasePhase.Empty;

    public bool OriginalAutoEnabled { get; private set; }

    public int OriginalSpeed { get; private set; }

    public string LastReason { get; private set; } = string.Empty;

    public bool CanForce => Phase == NetherBattleSettingsLeasePhase.Saved;

    public bool NeedsRecovery => _hasOriginal && Phase is NetherBattleSettingsLeasePhase.Saved
        or NetherBattleSettingsLeasePhase.Forced
        or NetherBattleSettingsLeasePhase.RestorePending
        or NetherBattleSettingsLeasePhase.Faulted;

    public bool SaveOriginal(bool autoEnabled, int speed)
    {
        if (speed < 0 || Phase is not (NetherBattleSettingsLeasePhase.Empty or NetherBattleSettingsLeasePhase.Restored))
            return false;

        OriginalAutoEnabled = autoEnabled;
        OriginalSpeed = speed;
        _hasOriginal = true;
        LastReason = string.Empty;
        Phase = NetherBattleSettingsLeasePhase.Saved;
        return true;
    }

    public bool MarkForced()
    {
        if (Phase != NetherBattleSettingsLeasePhase.Saved)
            return false;

        Phase = NetherBattleSettingsLeasePhase.Forced;
        return true;
    }

    public bool RequestRestore(string reason)
    {
        if (!_hasOriginal || string.IsNullOrWhiteSpace(reason))
            return false;
        if (Phase == NetherBattleSettingsLeasePhase.RestorePending)
            return true;
        if (Phase is not (NetherBattleSettingsLeasePhase.Saved or NetherBattleSettingsLeasePhase.Forced))
            return false;

        LastReason = reason;
        Phase = NetherBattleSettingsLeasePhase.RestorePending;
        return true;
    }

    public bool ObserveRestored(bool autoEnabled, int speed)
    {
        if (Phase != NetherBattleSettingsLeasePhase.RestorePending)
            return false;
        if (autoEnabled != OriginalAutoEnabled || speed != OriginalSpeed)
        {
            Phase = NetherBattleSettingsLeasePhase.Faulted;
            LastReason = "restore-mismatch";
            return false;
        }

        Phase = NetherBattleSettingsLeasePhase.Restored;
        _hasOriginal = false;
        return true;
    }

    public bool RecoverPersistedActive(bool autoEnabled, int speed)
    {
        if (!SaveOriginal(autoEnabled, speed))
            return false;
        return RequestRestore("recover-on-load");
    }

    public void FailSave(string reason)
    {
        LastReason = string.IsNullOrWhiteSpace(reason) ? "save-failed" : reason;
        Phase = NetherBattleSettingsLeasePhase.Faulted;
    }

    public void FailRestore(string reason)
    {
        if (!_hasOriginal)
            return;
        LastReason = string.IsNullOrWhiteSpace(reason) ? "restore-failed" : reason;
        Phase = NetherBattleSettingsLeasePhase.Faulted;
    }

    public bool RetryRestore()
    {
        if (!_hasOriginal || Phase != NetherBattleSettingsLeasePhase.Faulted)
            return false;

        Phase = NetherBattleSettingsLeasePhase.RestorePending;
        return true;
    }
}
