#nullable enable

using System;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace AbyssMod.Services;

/// <summary>
/// A native accessor is intentionally registered only by a version-confirmed controller patch.
/// The lease does not try a property-name heuristic for Auto or speed: absent an exact accessor,
/// F12 pauses before changing a player setting.
/// </summary>
internal interface INetherBattleSettingsNative
{
    bool TryRead(out bool autoEnabled, out int speed, out string error);

    bool TryForceAutoAndHighestSpeed(out string error);

    bool TryWrite(bool autoEnabled, int speed, out string error);
}

/// <summary>
/// Persists an original native battle Auto/speed pair before changing it.  The on-disk lease
/// is small, atomic and free of server data; deleting it is deferred until both values have
/// been read back as restored.
/// </summary>
internal sealed class NetherBattleSettingsLease : IDisposable, INetherBattleSettingsLeaseDriver
{
    private const int SchemaVersion = 1;
    private readonly NetherBattleSettingsLeaseState _state = new();
    private INetherBattleSettingsNative? _native;
    private string? _leasePath;
    private bool _initialized;
    private bool _recoveryLoadAttempted;
    private bool _recoveryPending;

    public static NetherBattleSettingsLease Instance { get; } = new();

    public NetherBattleSettingsLeasePhase Phase => _state.Phase;

    public string LastReason => _state.LastReason;

    public bool IsFaulted => _state.Phase == NetherBattleSettingsLeasePhase.Faulted;

    /// <summary>True while the persisted original values still require a verified native restore.</summary>
    public bool NeedsRecovery => _state.NeedsRecovery;

    private NetherBattleSettingsLease() { }

    public static void Initialize() => Instance.InitializeCore();

    public static void RegisterNativeAccessor(INetherBattleSettingsNative accessor)
    {
        if (accessor == null)
            throw new ArgumentNullException(nameof(accessor));
        Instance._native = accessor;
    }

    public static void UnregisterNativeAccessor(INetherBattleSettingsNative accessor)
    {
        if (accessor == null)
            return;
        if (ReferenceEquals(Instance._native, accessor))
            Instance._native = null;
    }

    public NetherNativeActionResult AcquireAndForce()
    {
        InitializeCore();
        if (_native == null)
            return NetherNativeActionResult.BindingUnavailable("native-battle-settings-accessor-unregistered");
        if (!_native.TryRead(out bool autoEnabled, out int speed, out string readError))
            return Fault("native-battle-settings-read-failed:" + readError, saveFailure: false);
        if (!_state.SaveOriginal(autoEnabled, speed))
            return NetherNativeActionResult.Rejected("battle-settings-lease-not-empty:" + _state.Phase);
        if (!TryWriteLease(active: true, autoEnabled, speed, out string saveError))
        {
            _state.FailSave("atomic-save-failed:" + saveError);
            return NetherNativeActionResult.Rejected("battle-settings-lease-save-failed:" + saveError);
        }
        if (!_native.TryForceAutoAndHighestSpeed(out string forceError))
        {
            _state.FailRestore("native-battle-settings-force-failed:" + forceError);
            return NetherNativeActionResult.UnknownOutcome("native-battle-settings-force-failed:" + forceError);
        }
        if (!_state.MarkForced())
            return Fault("battle-settings-force-transition-failed", saveFailure: false);

        Logger.Info("[F12][NetherClimb] battle settings lease saved and native Auto/highest-speed forced");
        return NetherNativeActionResult.Completed("battle-settings-forced");
    }

    public NetherNativeActionResult Restore(string reason)
    {
        InitializeCore();
        if (!_state.RequestRestore(reason))
        {
            return _state.Phase is NetherBattleSettingsLeasePhase.Empty or NetherBattleSettingsLeasePhase.Restored
                ? NetherNativeActionResult.Completed("battle-settings-already-restored")
                : NetherNativeActionResult.Rejected("battle-settings-restore-transition:" + _state.Phase);
        }
        if (_native == null)
            return Fault("native-battle-settings-accessor-unregistered", saveFailure: false);
        if (!_native.TryWrite(_state.OriginalAutoEnabled, _state.OriginalSpeed, out string writeError))
        {
            _state.FailRestore("native-battle-settings-restore-write-failed:" + writeError);
            return NetherNativeActionResult.UnknownOutcome("native-battle-settings-restore-write-failed:" + writeError);
        }
        if (!_native.TryRead(out bool observedAuto, out int observedSpeed, out string readError))
        {
            _state.FailRestore("native-battle-settings-restore-read-failed:" + readError);
            return NetherNativeActionResult.UnknownOutcome("native-battle-settings-restore-read-failed:" + readError);
        }
        if (!_state.ObserveRestored(observedAuto, observedSpeed))
            return NetherNativeActionResult.UnknownOutcome("native-battle-settings-restore-mismatch");
        if (!TryDeleteLease(out string deleteError))
        {
            _state.FailRestore("lease-delete-failed:" + deleteError);
            return NetherNativeActionResult.UnknownOutcome("lease-delete-failed:" + deleteError);
        }

        Logger.Info("[F12][NetherClimb] battle settings restored: " + reason);
        return NetherNativeActionResult.Completed("battle-settings-restored:" + reason);
    }

    public NetherNativeActionResult RecoverOnLoad()
    {
        InitializeCore();
        if (_recoveryLoadAttempted)
            return RetryRestoreAfterNativeAccessorRegistered();
        _recoveryLoadAttempted = true;
        if (!TryReadLease(out LeaseFile? lease, out string readError))
            return NetherNativeActionResult.BindingUnavailable("lease-read-failed:" + readError);
        if (lease == null || !lease.Active)
            return NetherNativeActionResult.Completed("no-active-battle-settings-lease");
        if (lease.SchemaVersion != SchemaVersion || lease.OriginalSpeed < 0)
            return Fault("invalid-battle-settings-lease", saveFailure: false);
        if (!_state.RecoverPersistedActive(lease.OriginalAutoEnabled, lease.OriginalSpeed))
            return Fault("persisted-battle-settings-lease-transition-failed", saveFailure: false);
        _recoveryPending = true;
        if (_native == null)
            return NetherNativeActionResult.BindingUnavailable("native-battle-settings-accessor-awaiting-registration");
        return RetryRestoreAfterNativeAccessorRegistered();
    }

    /// <summary>
    /// Called when BottomRightView has supplied the exact native settings service.  A persisted
    /// Faulted lease is moved through RetryRestore rather than being silently abandoned.
    /// </summary>
    public NetherNativeActionResult RetryRestoreAfterNativeAccessorRegistered()
    {
        InitializeCore();
        if (!_recoveryPending && !_state.NeedsRecovery)
            return NetherNativeActionResult.Completed("no-pending-battle-settings-recovery");
        if (_native == null)
            return NetherNativeActionResult.BindingUnavailable("native-battle-settings-accessor-unregistered");
        if (_state.Phase == NetherBattleSettingsLeasePhase.Faulted && !_state.RetryRestore())
            return NetherNativeActionResult.Rejected("battle-settings-retry-restore-transition");

        NetherNativeActionResult result = Restore("retry-native-settings-accessor");
        if (result.Kind == NetherNativeActionResultKind.Completed)
            _recoveryPending = false;
        return result;
    }

    public void Dispose()
    {
        NetherNativeActionResult result = Restore("plugin-unload");
        if (result.Kind is NetherNativeActionResultKind.UnknownOutcome or NetherNativeActionResultKind.BindingUnavailable)
        {
            Logger.Error(
                "[F12][NetherClimb] battle settings lease restore on unload failed: " + result.Detail
            );
        }
    }

    private void InitializeCore()
    {
        if (_initialized)
            return;
        _leasePath = Path.Combine(Paths.ConfigPath, "AbyssMod.nether-battle-settings-lease.json");
        _initialized = true;
    }

    private NetherNativeActionResult Fault(string reason, bool saveFailure)
    {
        if (saveFailure)
            _state.FailSave(reason);
        else
            _state.FailRestore(reason);
        Logger.Error("[F12][NetherClimb] battle settings lease fault: " + reason);
        return NetherNativeActionResult.BindingUnavailable(reason);
    }

    private bool TryWriteLease(bool active, bool autoEnabled, int speed, out string error)
    {
        error = string.Empty;
        try
        {
            string path = RequiredLeasePath();
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                error = "missing-config-directory";
                return false;
            }
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = JsonSerializer.Serialize(new LeaseFile(
                SchemaVersion,
                active,
                autoEnabled,
                speed,
                DateTimeOffset.UtcNow
            ));
            File.WriteAllText(temporary, json);
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private bool TryDeleteLease(out string error)
    {
        error = string.Empty;
        try
        {
            string path = RequiredLeasePath();
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private bool TryReadLease(out LeaseFile? lease, out string error)
    {
        lease = null;
        error = string.Empty;
        try
        {
            string path = RequiredLeasePath();
            if (!File.Exists(path))
                return true;
            string json = File.ReadAllText(path);
            lease = JsonSerializer.Deserialize<LeaseFile>(json);
            if (lease == null)
            {
                error = "empty-lease-file";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private string RequiredLeasePath() => _leasePath ?? throw new InvalidOperationException("lease-not-initialized");

    private sealed record LeaseFile(
        int SchemaVersion,
        bool Active,
        bool OriginalAutoEnabled,
        int OriginalSpeed,
        DateTimeOffset CreatedAt
    );
}
